//! The system clipboard's text (C# `Clipboard.SetContent` / `Clipboard.GetContent().GetTextAsync()`, MainWindow.cs:285 and
//! MainWindow.Terminal.cs:254-262): a terminal's selection copied, the clipboard pasted into a terminal.

use crate::Result;

/// Reads and writes the clipboard's plain text. Both calls are short and synchronous; another program holding the clipboard is
/// waited out briefly, then refused.
pub trait Clipboard: Send + Sync {
    /// The clipboard's text, or None when it holds no text (a picture, files, nothing).
    fn read_text(&self) -> Result<Option<String>>;
    /// Replaces the clipboard's content with `text`.
    fn write_text(&self, text: &str) -> Result<()>;
}
