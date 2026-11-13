//! Mighty Code's files pane, its previews and the 변경사항 view, with no Tauri and no OS code (the OS only through mighty-platform's
//! ports: an open file's handle, a PDF's first page, git through `ProcessHost`).
//!
//! The C# `MightyCode.Core` classes, each in its own module: the listing and its natural order (`listing`, C# `WorkspaceFiles.List`,
//! `Sorted`, `NaturalComparer`), how a file is previewed (`classify`, C# `FilePreviewClassifier`, `TextEncodings`), its lines and
//! colours and the find bar (`source`, C# `SourceLines`, `SourceHighlighter`, `SourceFind`), the pane's tree (`tree`, C#
//! `FilePaneTree`, `FilePaneDrawing`), the preview read off the page's thread (`preview`, C# `FilePreviewLoader`), the diff (`diff`, C#
//! `FileDiff`), the patches Claude's edits carry (`patch`, C# `FilePatch`, `FileEdit` and the parser's `EditedOriginal`), the originals
//! a run kept and git's HEAD (`changes`, C# `FileOriginalStore`, `GitHeadFile`, `FileChanges`), the files a reference names
//! (`reference`, C# `ReferencePreview`), the files pane's place in the layout (`pane`, C# `FilePaneKind`). Then what the WinUI host
//! builds from them for the page: the tree's and the preview's models (`model`, MainWindow.WebShell.Panes.cs `WebFilesModel`,
//! `WebFilePreview`, `WebFileImage`), pictures drawn to PNG (`raster`, C# `NativeSvgRaster`, the WIC decode and fit), and the HTML
//! frames' folders (`frames`, MainWindow.WebShell.cs `WebHtmlAddress`).

pub mod changes;
pub mod classify;
pub mod diff;
pub mod frames;
pub mod listing;
pub mod model;
pub mod pane;
pub mod patch;
pub mod preview;
pub mod raster;
pub mod reference;
pub mod source;
pub mod tree;
mod utf16;

pub use mighty_composer::files::{OpenError, OpenedFile, open_file};
