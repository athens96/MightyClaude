//! The completion toast and its activation on Windows (C# MainWindow.CompletionNotification.cs `WindowsAppNotifier`). Owner: W6-A.
//!
//! A WinRT toast (`ToastNotificationManager`) under the app's AppUserModelID, `launch="sessionId=…"` as C#'s, its two lines the
//! notification's title and body; a click on it calls the activation handler with the pane's id while the app runs. An unpackaged app
//! may show toasts only under an AUMID registered for the user ([`register`]: `HKCU\Software\Classes\AppUserModelId\<id>`), which the
//! host writes only with `--allow-state-write`.

use std::sync::{Arc, Mutex};

use mighty_platform::notify::ActivationHandler;
use mighty_platform::{BoxFuture, Notification, Notifier, PlatformError, PrefValue, PrefsStore, Result};
use windows::Data::Xml::Dom::XmlDocument;
use windows::Foundation::TypedEventHandler;
use windows::UI::Notifications::{ToastNotification, ToastNotificationManager};
use windows::core::HSTRING;

use crate::prefs::RegistryPrefs;

/// The app's AppUserModelID: its development identity (tauri.conf.json `identifier`), never the installed C# app's.
pub const AUMID: &str = "dev.mightycode.tauri";

/// Registers the AUMID for the user so its toasts show (the key's `DisplayName`). The caller decides whether it may write.
pub fn register() -> Result<()> {
    RegistryPrefs::under(&format!(r"Software\Classes\AppUserModelId\{AUMID}")).set("DisplayName", PrefValue::Text("Mighty Code".into()))
}

fn escape(text: &str) -> String {
    text.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;").replace('\'', "&apos;")
}

/// C# `WebUtility.UrlEncode`: letters, digits and `-_.!*()` kept, a space as `+`, every other UTF-8 byte as `%XX` (upper case).
fn url_encode(text: &str) -> String {
    text.bytes()
        .map(|b| match b {
            b' ' => "+".to_string(),
            _ if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'!' | b'*' | b'(' | b')') => (b as char).to_string(),
            _ => format!("%{b:02X}"),
        })
        .collect()
}

/// How many shown toasts are kept alive: a toast's `Activated` fires only while its object lives.
const LIVE_TOASTS: usize = 20;

/// The toast's XML (C# `SendAsync`).
pub fn toast_xml(notification: &Notification) -> String {
    format!(
        "<toast launch=\"sessionId={}\"><visual><binding template=\"ToastGeneric\"><text>{}</text><text>{}</text></binding></visual></toast>",
        url_encode(notification.pane_id.as_deref().unwrap_or_default()),
        escape(&notification.title),
        escape(&notification.body)
    )
}

/// The toast notifier of Windows.
#[derive(Default)]
pub struct ToastNotifier {
    handler: Arc<Mutex<Option<ActivationHandler>>>,
    /// The last [`LIVE_TOASTS`] toasts shown, kept so a click on one still showing reaches its handler (review M3).
    live: Mutex<std::collections::VecDeque<ToastNotification>>,
}

fn failed(error: windows::core::Error) -> PlatformError {
    PlatformError::Refused(format!("toast: {error}"))
}

impl ToastNotifier {
    fn show(&self, notification: &Notification) -> Result<()> {
        // SAFETY: initializes WinRT for this thread (already initialized is fine and is ignored).
        let _ = unsafe { windows::Win32::System::WinRT::RoInitialize(windows::Win32::System::WinRT::RO_INIT_MULTITHREADED) };
        let document = XmlDocument::new().map_err(failed)?;
        document.LoadXml(&HSTRING::from(toast_xml(notification))).map_err(failed)?;
        let toast = ToastNotification::CreateToastNotification(&document).map_err(failed)?;
        let (handler, pane) = (self.handler.clone(), notification.pane_id.clone());
        toast
            .Activated(&TypedEventHandler::new(move |_, _| {
                let handler = handler.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).clone();
                if let Some(handler) = handler {
                    handler(pane.clone());
                }
                Ok(())
            }))
            .map_err(failed)?;
        ToastNotificationManager::CreateToastNotifierWithId(&HSTRING::from(AUMID)).map_err(failed)?.Show(&toast).map_err(failed)?;
        let mut live = self.live.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        live.push_back(toast);
        while live.len() > LIVE_TOASTS {
            live.pop_front();
        }
        Ok(())
    }
}

impl Notifier for ToastNotifier {
    fn notify(&self, notification: Notification) -> BoxFuture<'_, Result<()>> {
        let shown = self.show(&notification);
        Box::pin(async move { shown })
    }

    fn on_activated(&self, handler: ActivationHandler) {
        *self.handler.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(handler);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// C#'s toast: the launch argument names the pane, the title and body escaped as XML.
    #[test]
    fn the_toast_names_the_pane_and_escapes_its_lines() {
        let notification = Notification {
            title: "Mighty Code · Task complete".into(),
            body: "A <b> & \"c\" has finished.".into(),
            pane_id: Some("a b/c".into()),
        };
        assert_eq!(
            toast_xml(&notification),
            "<toast launch=\"sessionId=a+b%2Fc\"><visual><binding template=\"ToastGeneric\"><text>Mighty Code · Task complete</text>\
             <text>A &lt;b&gt; &amp; &quot;c&quot; has finished.</text></binding></visual></toast>"
        );
        assert_eq!(url_encode("5a0e0b0b-0000-4000-8000-00000000b10b"), "5a0e0b0b-0000-4000-8000-00000000b10b", "a GUID stays as it is");
    }
}
