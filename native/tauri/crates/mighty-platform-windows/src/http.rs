//! The HTTPS GET on Windows: WinHTTP, redirects off, the system proxy (C# AccountUsageRuntime.cs's HttpClient). Owner: W6-C.
//!
//! One ephemeral request (C# `AccountUsageRuntime.RawSendAsync`): HTTPS to the given host on its own port only, a GET, no cookies, no
//! credential store or automatic logon, no redirect followed (a 3xx comes back with its `Location`, for the caller to refuse), the
//! caller's timeout for each phase, and a body read only for a 2xx, refused past the cap. There is no retry. Which hosts and paths may
//! be asked is the caller's guard (`mighty_accounts::usage::guard`); no message here carries a header value.
//!
//! [`WinHttpGet`] is mighty-platform's `HttpsGet` port over it (lib.rs `https_get`): an `https://host/path` URL on HTTPS's own port
//! only, anything else refused before WinHTTP is opened.

use std::ffi::c_void;
use std::time::Duration;

use mighty_platform::{BoxFuture, PlatformError, Result};
use windows_sys::Win32::Networking::WinHttp::{
    ERROR_WINHTTP_HEADER_NOT_FOUND, INTERNET_DEFAULT_HTTPS_PORT, WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_ADDREQ_FLAG_ADD,
    WINHTTP_ADDREQ_FLAG_REPLACE, WINHTTP_AUTOLOGON_SECURITY_LEVEL_HIGH, WINHTTP_DISABLE_COOKIES, WINHTTP_DISABLE_REDIRECTS,
    WINHTTP_FLAG_SECURE, WINHTTP_OPTION_AUTOLOGON_POLICY, WINHTTP_OPTION_DISABLE_FEATURE, WINHTTP_OPTION_REDIRECT_POLICY,
    WINHTTP_OPTION_REDIRECT_POLICY_NEVER, WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_QUERY_LOCATION, WINHTTP_QUERY_RETRY_AFTER,
    WINHTTP_QUERY_STATUS_CODE, WinHttpAddRequestHeaders, WinHttpCloseHandle, WinHttpConnect, WinHttpOpen, WinHttpOpenRequest,
    WinHttpQueryDataAvailable, WinHttpQueryHeaders, WinHttpReadData, WinHttpReceiveResponse, WinHttpSendRequest, WinHttpSetOption,
    WinHttpSetTimeouts,
};

use crate::win::{check, last_error, wide};

/// One GET: `https://<host><path_and_query>` with `headers`, each phase within `timeout`.
#[derive(Clone)]
pub struct HttpsRequest {
    pub host: String,
    /// Starts with `/`.
    pub path_and_query: String,
    pub headers: Vec<(String, String)>,
    pub timeout: Duration,
}

/// The headers' names only: a value can be a sign-in.
impl std::fmt::Debug for HttpsRequest {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HttpsRequest")
            .field("host", &self.host)
            .field("path_and_query", &self.path_and_query)
            .field("headers", &self.headers.iter().map(|(name, _)| name.as_str()).collect::<Vec<_>>())
            .field("timeout", &self.timeout)
            .finish()
    }
}

/// The answer (the port's): its status, the body of a 2xx (else empty), `Retry-After`, and a 3xx's `Location` (`"refused"` when it named
/// none).
pub use mighty_platform::http::HttpsResponse;

/// The WinHTTP handles of one request, closed in reverse order.
struct Handles(Vec<*mut c_void>);

impl Drop for Handles {
    fn drop(&mut self) {
        for handle in self.0.drain(..).rev() {
            // SAFETY: each handle came from WinHttpOpen, WinHttpConnect or WinHttpOpenRequest and is closed once.
            unsafe { WinHttpCloseHandle(handle) };
        }
    }
}

fn opened(handles: &mut Handles, handle: *mut c_void) -> Result<*mut c_void> {
    if handle.is_null() {
        return Err(last_error());
    }
    handles.0.push(handle);
    Ok(handle)
}

fn option(handle: *mut c_void, option: u32, value: u32) -> Result<()> {
    // SAFETY: `value` is a live u32 for the call, its size given.
    check(unsafe { WinHttpSetOption(handle, option, (&value as *const u32).cast(), 4) })
}

/// A header's text, None when the response has none.
fn header(request: *mut c_void, level: u32) -> Result<Option<String>> {
    let mut size = 0u32;
    // SAFETY: a null buffer asks for the size, written to `size`.
    let ok = unsafe { WinHttpQueryHeaders(request, level, std::ptr::null(), std::ptr::null_mut(), &mut size, std::ptr::null_mut()) };
    if ok != 0 {
        return Ok(Some(String::new()));
    }
    let error = std::io::Error::last_os_error();
    if error.raw_os_error() == Some(ERROR_WINHTTP_HEADER_NOT_FOUND as i32) {
        return Ok(None);
    }
    let mut buffer = vec![0u16; size as usize / 2 + 1];
    let mut length = (buffer.len() * 2) as u32;
    // SAFETY: `buffer` holds `length` writable bytes.
    check(unsafe { WinHttpQueryHeaders(request, level, std::ptr::null(), buffer.as_mut_ptr().cast(), &mut length, std::ptr::null_mut()) })?;
    Ok(Some(String::from_utf16_lossy(&buffer[..length as usize / 2])))
}

fn validate(request: &HttpsRequest) -> Result<()> {
    let bad = |text: &str| text.chars().any(|c| c.is_control());
    if request.host.is_empty() || bad(&request.host) || request.host.contains(['/', ':', '@']) {
        return Err(PlatformError::Invalid("an HTTPS GET needs a host name".into()));
    }
    if !request.path_and_query.starts_with('/') || bad(&request.path_and_query) {
        return Err(PlatformError::Invalid("an HTTPS GET needs a path".into()));
    }
    if request.headers.iter().any(|(name, value)| name.is_empty() || bad(name) || name.contains(':') || bad(value)) {
        return Err(PlatformError::Invalid("a header holds a line break".into()));
    }
    Ok(())
}

fn send(request: &HttpsRequest, max_body: usize) -> Result<HttpsResponse> {
    let mut handles = Handles(vec![]);
    let agent = wide("MightyCode");
    // SAFETY: `agent` is a NUL-terminated wide string; no proxy names are given, so the system's (WPAD, PAC, settings) are used.
    let session = opened(&mut handles, unsafe {
        WinHttpOpen(agent.as_ptr(), WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, std::ptr::null(), std::ptr::null(), 0)
    })?;
    let milliseconds = request.timeout.as_millis().clamp(1, i32::MAX as u128) as i32;
    // SAFETY: a session handle this call owns.
    check(unsafe { WinHttpSetTimeouts(session, milliseconds, milliseconds, milliseconds, milliseconds) })?;
    let host = wide(&request.host);
    // SAFETY: `host` is NUL-terminated; HTTPS's own port only.
    let connection = opened(&mut handles, unsafe { WinHttpConnect(session, host.as_ptr(), INTERNET_DEFAULT_HTTPS_PORT, 0) })?;
    let (verb, path) = (wide("GET"), wide(&request.path_and_query));
    // SAFETY: NUL-terminated strings; default version, no referrer, any accept type; TLS required.
    let handle = opened(&mut handles, unsafe {
        WinHttpOpenRequest(
            connection,
            verb.as_ptr(),
            path.as_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            WINHTTP_FLAG_SECURE,
        )
    })?;
    option(handle, WINHTTP_OPTION_REDIRECT_POLICY, WINHTTP_OPTION_REDIRECT_POLICY_NEVER)?;
    option(handle, WINHTTP_OPTION_DISABLE_FEATURE, WINHTTP_DISABLE_COOKIES | WINHTTP_DISABLE_REDIRECTS)?;
    option(handle, WINHTTP_OPTION_AUTOLOGON_POLICY, WINHTTP_AUTOLOGON_SECURITY_LEVEL_HIGH)?;
    for (name, value) in &request.headers {
        let line = wide(format!("{name}: {value}"));
        // SAFETY: `line` is NUL-terminated (length -1 tells WinHTTP to find its end).
        check(unsafe { WinHttpAddRequestHeaders(handle, line.as_ptr(), u32::MAX, WINHTTP_ADDREQ_FLAG_ADD | WINHTTP_ADDREQ_FLAG_REPLACE) })?;
    }
    // SAFETY: no extra headers and no body.
    check(unsafe { WinHttpSendRequest(handle, std::ptr::null(), 0, std::ptr::null(), 0, 0, 0) })?;
    // SAFETY: the request was sent on this handle.
    check(unsafe { WinHttpReceiveResponse(handle, std::ptr::null_mut()) })?;
    let mut status = 0u32;
    let mut size = 4u32;
    // SAFETY: `status` is a writable u32 of `size` bytes.
    check(unsafe {
        WinHttpQueryHeaders(
            handle,
            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            std::ptr::null(),
            (&mut status as *mut u32).cast(),
            &mut size,
            std::ptr::null_mut(),
        )
    })?;
    let retry_after = header(handle, WINHTTP_QUERY_RETRY_AFTER)?;
    let redirect_location = (300..400)
        .contains(&status)
        .then(|| header(handle, WINHTTP_QUERY_LOCATION))
        .transpose()?
        .map(|location| location.filter(|l| !l.is_empty()).unwrap_or_else(|| "refused".into()));
    let mut body = Vec::new();
    if (200..300).contains(&status) {
        loop {
            let mut available = 0u32;
            // SAFETY: `available` is writable.
            check(unsafe { WinHttpQueryDataAvailable(handle, &mut available) })?;
            if available == 0 {
                break;
            }
            if body.len() + available as usize > max_body {
                return Err(PlatformError::TooLarge(max_body));
            }
            let start = body.len();
            body.resize(start + available as usize, 0);
            let mut read = 0u32;
            // SAFETY: `body[start..]` holds `available` writable bytes.
            check(unsafe { WinHttpReadData(handle, body[start..].as_mut_ptr().cast(), available, &mut read) })?;
            body.truncate(start + read as usize);
            if read == 0 {
                break;
            }
        }
    }
    Ok(HttpsResponse { status: status as u16, body: String::from_utf8_lossy(&body).into_owned(), retry_after, redirect_location })
}

/// Sends `request` on a blocking thread and gives its answer; a body past `max_body` bytes is refused (`TooLarge`). The whole request
/// ends within its timeout, as C#'s `HttpClient.Timeout` bounds it, not each WinHTTP phase in turn.
pub async fn get(request: HttpsRequest, max_body: usize) -> Result<HttpsResponse> {
    validate(&request)?;
    let timeout = request.timeout;
    within(timeout, move || send(&request, max_body)).await
}

/// `work` on a blocking thread, its answer given up on once `timeout` passes (the thread itself ends with WinHTTP's phase timeouts).
async fn within<T: Send + 'static>(timeout: Duration, work: impl FnOnce() -> Result<T> + Send + 'static) -> Result<T> {
    match tokio::time::timeout(timeout.max(Duration::from_millis(1)), tokio::task::spawn_blocking(work)).await {
        Ok(Ok(answer)) => answer,
        Ok(Err(_)) => Err(PlatformError::Io(std::io::Error::other("the HTTPS request's thread ended"))),
        Err(_) => Err(PlatformError::TimedOut("the HTTPS request")),
    }
}

/// An `https://host[/path?query]` URL as its host and its path and query; anything else (another scheme or port, user info, a
/// fragment, no host, a host name of anything but ASCII letters, digits, dots and hyphens) refused.
fn split(url: &str) -> Result<(String, String)> {
    let refused = || PlatformError::Invalid("only an https://host/path address is asked".into());
    let rest = url.strip_prefix("https://").ok_or_else(refused)?;
    let (host, path) = match rest.find(['/', '?']) {
        Some(at) if rest[at..].starts_with('/') => (&rest[..at], rest[at..].to_string()),
        Some(at) => (&rest[..at], format!("/{}", &rest[at..])),
        None => (rest, "/".to_string()),
    };
    let host = host.to_ascii_lowercase();
    let named = !host.is_empty() && host.bytes().all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'.' || b == b'-');
    if !named || path.contains('#') {
        return Err(refused());
    }
    Ok((host, path))
}

/// mighty-platform's `HttpsGet` on Windows: [`get`] for an `https://` URL on port 443.
#[derive(Debug, Default)]
pub struct WinHttpGet;

impl mighty_platform::HttpsGet for WinHttpGet {
    fn get(&self, request: mighty_platform::http::HttpsRequest, max_body: usize) -> BoxFuture<'_, Result<HttpsResponse>> {
        Box::pin(async move {
            let (host, path_and_query) = split(&request.url)?;
            get(HttpsRequest { host, path_and_query, headers: request.headers, timeout: request.timeout }, max_body).await
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn request(host: &str, path: &str, headers: &[(&str, &str)]) -> HttpsRequest {
        HttpsRequest {
            host: host.into(),
            path_and_query: path.into(),
            headers: headers.iter().map(|(n, v)| (n.to_string(), v.to_string())).collect(),
            timeout: Duration::from_secs(1),
        }
    }

    /// A request that could carry a second request or reach another host is refused before WinHTTP is opened (nothing here connects).
    #[test]
    fn a_request_that_could_go_elsewhere_is_refused_before_anything_is_sent() {
        assert!(validate(&request("api.anthropic.com", "/api/oauth/usage", &[("Accept", "application/json")])).is_ok());
        for bad in [
            request("", "/x", &[]),
            request("evil.test/x", "/x", &[]),
            request("api.anthropic.com:8443", "/x", &[]),
            request("user@api.anthropic.com", "/x", &[]),
            request("api.anthropic.com", "x", &[]),
            request("api.anthropic.com", "/x\r\nHost: evil.test", &[]),
            request("api.anthropic.com", "/x", &[("Authorization", "Bearer a\r\nX-Evil: 1")]),
            request("api.anthropic.com", "/x", &[("Bad:Name", "v")]),
        ] {
            assert!(validate(&bad).is_err(), "{bad:?}");
        }
        let shown = format!("{:?}", request("api.anthropic.com", "/x", &[("Authorization", "Bearer sk-FIXTURE")]));
        assert!(shown.contains("Authorization") && !shown.contains("sk-FIXTURE"), "Debug names the headers only: {shown}");
    }

    /// The port's URL: `https://host/path?query` only, on HTTPS's own port.
    #[test]
    fn only_an_https_address_on_its_own_port_is_asked() {
        assert_eq!(
            split("https://API.anthropic.com/api/oauth/usage?at_wall=1&skip_spend=1").unwrap(),
            ("api.anthropic.com".to_string(), "/api/oauth/usage?at_wall=1&skip_spend=1".to_string())
        );
        assert_eq!(split("https://api.anthropic.com").unwrap().1, "/");
        for bad in [
            "http://api.anthropic.com/x",
            "https://api.anthropic.com:8443/x",
            "https://user@api.anthropic.com/x",
            "https://api.anthropic.com/x#frag",
            "https:///x",
            "ftp://x/y",
            // Review C12: a host name of ASCII letters, digits, dots and hyphens only.
            "https://api.anthropic.com\\evil.test/x",
            "https://api.anthropic.com%2Fevil.test/x",
            "https://api anthropic.com/x",
            "https://[::1]/x",
            "https://\u{0430}pi.anthropic.com/x",
            "https://api.anthropic.com\u{0}/x",
        ] {
            assert!(split(bad).is_err(), "{bad}");
        }
        assert_eq!(split("https://xn--bcher-kva.example/x").unwrap().0, "xn--bcher-kva.example", "a punycode name is a name");
    }

    /// Review C11: the whole request is given up on once its timeout passes, however long the blocking side would take.
    #[tokio::test]
    async fn the_whole_request_ends_within_its_timeout() {
        let started = std::time::Instant::now();
        let answer = within(Duration::from_millis(50), || {
            std::thread::sleep(Duration::from_millis(1500));
            Ok(())
        })
        .await;
        assert!(matches!(answer, Err(PlatformError::TimedOut(_))), "{answer:?}");
        assert!(started.elapsed() < Duration::from_secs(1), "given up on at the timeout");
        assert_eq!(within(Duration::from_secs(5), || Ok(7)).await.unwrap(), 7);
    }
}
