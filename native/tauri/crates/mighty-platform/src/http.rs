//! The HTTPS GET port, redirects refused (C# AccountUsageRuntime.cs `RawSendAsync`, `AccountUsageHttpRequest`, `AccountUsageHttpResponse`):
//! the account usage's direct lookup. Owner: W6-A (the port); its Windows implementation (WinHTTP) and the guard over it (https on 443
//! only, the token only in Authorization) are W6-C's.

use std::time::Duration;

use crate::{BoxFuture, Result};

/// One GET (C# `AccountUsageHttpRequest`).
#[derive(Clone, PartialEq, Eq)]
pub struct HttpsRequest {
    pub url: String,
    /// Sent as given (C# `TryAddWithoutValidation`). A value here never reaches a log, an error or a snapshot (its `Debug` hides them).
    pub headers: Vec<(String, String)>,
    pub timeout: Duration,
}

impl std::fmt::Debug for HttpsRequest {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let names: Vec<&str> = self.headers.iter().map(|(name, _)| name.as_str()).collect();
        f.debug_struct("HttpsRequest").field("url", &self.url).field("headers", &names).field("timeout", &self.timeout).finish()
    }
}

/// Its answer (C# `AccountUsageHttpResponse`).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct HttpsResponse {
    pub status: u16,
    /// The body, read only for a 2xx; else empty.
    pub body: String,
    pub retry_after: Option<String>,
    /// A 3xx's `Location` (`refused` when it named none): never followed.
    pub redirect_location: Option<String>,
}

/// Contract: one request, no retry, no cookies, no stored credentials or automatic logon, no redirect followed, the system's proxy;
/// at most `max_body` bytes of a body (C# `MaxResponseContentBufferSize`); it fails on a transport error or the timeout, and never
/// puts a header's value into its error.
pub trait HttpsGet: Send + Sync {
    fn get(&self, request: HttpsRequest, max_body: usize) -> BoxFuture<'_, Result<HttpsResponse>>;
}
