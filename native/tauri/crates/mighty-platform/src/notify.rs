//! OS notifications (C# `CompletionNotification*`, the Mac's `UNUserNotificationCenter`): "a request finished" and the like.

use std::sync::Arc;

use crate::{BoxFuture, Result};

/// One notification. It never carries a prompt, tool arguments or a project path (C# `CompletionNotificationDecision`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Notification {
    /// "workspace · pane title", or the pane's title alone.
    pub title: String,
    pub body: String,
    /// The pane a click on it brings forward.
    pub pane_id: Option<String>,
}

/// Called with the clicked notification's pane id: the host brings its window forward and pushes `select{paneId}` to the page.
pub type ActivationHandler = Arc<dyn Fn(Option<String>) + Send + Sync>;

/// Contract: `notify` shows the notification or fails without asking anything of the user (a denied permission is `Refused`); the
/// handler set last is the one called, on any thread.
pub trait Notifier: Send + Sync {
    fn notify(&self, notification: Notification) -> BoxFuture<'_, Result<()>>;
    fn on_activated(&self, handler: ActivationHandler);
}
