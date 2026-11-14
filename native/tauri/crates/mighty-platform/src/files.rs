//! The files pane's ports (W4): what an open file's handle really is (C# `WorkspaceFiles.WindowsFileHandles`), a PDF's first page
//! drawn (C# `Windows.Data.Pdf` in `WebFileImage`), the desktop's file actions (show a file in the file manager, open it with its
//! default app, a picture on the clipboard: C# `explorer.exe /select`, `Launcher.LaunchFileAsync`, `DataPackage.SetBitmap`), and the
//! save dialog (C# `FileSavePicker`). The rules around them (containment, caps, which file is shown) are the shared logic's.

use std::fs::File;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use crate::{BoxFuture, PlatformError, Result};

/// Where an open file really is, asked of its handle (C# `WorkspaceFiles.OpenFile` on Windows: `GetFileType`,
/// `GetFinalPathNameByHandleW`). This closes the gap between walking a path and opening it: a folder on the way swapped for a link
/// in between is caught by the handle's own final path. An OS that cannot ask a handle says so ([`FileHandles::asks_handles`]
/// false) and the caller walks the path again after the open instead (C# off Windows).
pub trait FileHandles: Send + Sync {
    /// Whether this OS asks a handle where it is; when false the other methods are not called.
    fn asks_handles(&self) -> bool;
    /// The handle is a file on a disk (`GetFileType` is `FILE_TYPE_DISK`), not a pipe, a console or another device a name opened.
    fn is_disk(&self, file: &File) -> bool;
    /// The path the handle really points at (`GetFinalPathNameByHandleW` with `\\?\` taken off and `\\?\UNC\` written `\\`);
    /// None when the OS cannot say.
    fn final_path(&self, file: &File) -> Option<PathBuf>;
    /// The final path of a folder (opened with backup semantics, so a folder handle exists); None when it cannot be opened.
    fn final_directory_path(&self, path: &Path) -> Option<PathBuf>;
}

/// What the Antigravity rule safety asks the disk about a path agy named (C# `AntigravityRuleSafety.ResolvedPath`, its
/// `GetDriveTypeW` and `GetFinalPathNameByHandleW`), and only once the path passed the text check (a plain local path: `X:\…` on
/// Windows, `/…` on the Mac), so a UNC or device name never reaches it. An OS that cannot ask says so ([`PathProbe::checks_paths`]
/// false): no file rule is offered or added then, as nothing could check it.
pub trait PathProbe: Send + Sync {
    /// Whether this OS's disk can be asked; when false the other methods are not called.
    fn checks_paths(&self) -> bool;
    /// The drive `letter` is a fixed disk (`GetDriveTypeW("X:\\") == DRIVE_FIXED`), not a removable, network or CD drive; an OS
    /// without drive letters says false.
    fn fixed_drive(&self, letter: char) -> bool;
    /// The link at `path` read without following it (a symbolic link, or a junction on Windows): its target as written, a `\\?\` or
    /// `\??\` before a drive taken off (`\\?\UNC\…` and other device names left as they are); None when `path` is no link or cannot
    /// be read.
    fn link_target(&self, path: &Path) -> Option<PathBuf>;
    /// What the file system calls the file or folder at `path` (Windows: `GetFinalPathNameByHandleW` with `\\?\` taken off and
    /// `\\?\UNC\` written `\\`: its case, long names for 8.3 ones, no trailing dots or spaces, links followed; the Mac: `realpath`);
    /// None when it does not exist or cannot be opened.
    fn final_path(&self, path: &Path) -> Option<PathBuf>;
}

impl PathProbe for crate::unsupported::Unsupported {
    fn checks_paths(&self) -> bool {
        false
    }
    fn fixed_drive(&self, _letter: char) -> bool {
        false
    }
    fn link_target(&self, _path: &Path) -> Option<PathBuf> {
        None
    }
    fn final_path(&self, _path: &Path) -> Option<PathBuf> {
        None
    }
}

/// A PDF's first page: its size in points and, when the caller gave it a size, the page drawn as a PNG of that many pixels.
#[derive(Debug, Clone, PartialEq)]
pub struct PdfPage {
    pub width: f64,
    pub height: f64,
    pub png: Option<Vec<u8>>,
}

/// Draws a PDF's first page (Windows: `Windows.Data.Pdf`; the Mac's is not written yet).
pub trait PdfRaster: Send + Sync {
    /// The document's first page, drawn at the pixel size `size` picks from its size in points (None: not drawn). Ok(None) when the
    /// document has no page; an error when it does not load or draw.
    fn first_page(&self, data: &[u8], size: &(dyn Fn(f64, f64) -> Option<(u32, u32)> + Sync)) -> Result<Option<PdfPage>>;
}

/// A picture for the clipboard: its pixels (RGBA, straight alpha, rows from the top) and the same picture as PNG.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClipboardImage {
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
    pub png: Vec<u8>,
}

/// What the files pane, the reference bubble and the picture viewer ask of the desktop. Every path given is one the shared logic
/// already resolved inside its folder.
pub trait Desktop: Send + Sync {
    /// Shows the file selected in the OS's file manager (C# `explorer.exe /select,"<path>"`).
    fn reveal(&self, path: &Path) -> Result<()>;
    /// Opens the file with the user's default app (C# `Launcher.LaunchFileAsync`); a file type that can run code is never started:
    /// Ok(false), as the launcher refuses programs. Ok(false) too when no app opens it.
    fn open(&self, path: &Path) -> Result<bool>;
    /// Puts the picture on the clipboard (C# `DataPackage.SetBitmap`, then `Clipboard.Flush`, so it stays after the app quits).
    fn copy_image(&self, image: &ClipboardImage) -> Result<()>;
}

/// What a save dialog offers (C# `FileSavePicker`: `SuggestedFileName`, one `FileTypeChoices` entry).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SaveRequest {
    pub suggested_name: String,
    /// The choice's name (C# the media type, `image/png`).
    pub filter_name: String,
    /// Its extension, without the dot.
    pub extension: String,
}

/// The OS's save dialog: the path the user chose, None when they cancelled.
pub trait SaveDialog: Send + Sync {
    fn pick(&self, request: SaveRequest) -> BoxFuture<'_, Result<Option<PathBuf>>>;
}

/// The files ports of one OS (the save dialog needs the app's window and comes from the host).
#[derive(Clone)]
pub struct FilePorts {
    pub handles: Arc<dyn FileHandles>,
    pub pdf: Arc<dyn PdfRaster>,
    pub desktop: Arc<dyn Desktop>,
}

impl std::fmt::Debug for FilePorts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FilePorts").field("asks_handles", &self.handles.asks_handles()).finish_non_exhaustive()
    }
}

impl FilePorts {
    /// Every files port unsupported: handles are not asked (the path is walked again), PDFs and the desktop's actions refuse.
    pub fn unsupported() -> FilePorts {
        let port = Arc::new(crate::unsupported::Unsupported);
        FilePorts { handles: port.clone(), pdf: port.clone(), desktop: port }
    }
}

impl FileHandles for crate::unsupported::Unsupported {
    fn asks_handles(&self) -> bool {
        false
    }
    fn is_disk(&self, _file: &File) -> bool {
        true
    }
    fn final_path(&self, _file: &File) -> Option<PathBuf> {
        None
    }
    fn final_directory_path(&self, _path: &Path) -> Option<PathBuf> {
        None
    }
}

impl PdfRaster for crate::unsupported::Unsupported {
    fn first_page(&self, _data: &[u8], _size: &(dyn Fn(f64, f64) -> Option<(u32, u32)> + Sync)) -> Result<Option<PdfPage>> {
        Err(PlatformError::unsupported("PdfRaster", "first_page"))
    }
}

impl Desktop for crate::unsupported::Unsupported {
    fn reveal(&self, _path: &Path) -> Result<()> {
        Err(PlatformError::unsupported("Desktop", "reveal"))
    }
    fn open(&self, _path: &Path) -> Result<bool> {
        Err(PlatformError::unsupported("Desktop", "open"))
    }
    fn copy_image(&self, _image: &ClipboardImage) -> Result<()> {
        Err(PlatformError::unsupported("Desktop", "copy_image"))
    }
}

impl SaveDialog for crate::unsupported::Unsupported {
    fn pick(&self, _request: SaveRequest) -> BoxFuture<'_, Result<Option<PathBuf>>> {
        Box::pin(async { Err(PlatformError::unsupported("SaveDialog", "pick")) })
    }
}

/// The files ports' fakes (re-exported from [`crate::fake`]).
pub mod fake {
    use std::sync::Mutex;

    use super::*;

    fn lock<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
        mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Handles as a test sets them: asked or not, a disk file or not, and the final paths to report (None: the OS cannot say).
    #[derive(Debug, Default)]
    pub struct FakeFileHandles {
        pub asks: bool,
        pub not_disk: bool,
        /// The final path every open file reports (unset: the file's path as the test opened it is not known here, so None).
        pub final_path: Mutex<Option<PathBuf>>,
        /// The final path every folder reports; unset: the folder as given.
        pub final_directory: Mutex<Option<PathBuf>>,
    }

    impl FakeFileHandles {
        /// Handles that are asked, every file reporting `file` and every folder `folder`.
        pub fn asked(file: Option<PathBuf>, folder: Option<PathBuf>) -> FakeFileHandles {
            FakeFileHandles { asks: true, not_disk: false, final_path: Mutex::new(file), final_directory: Mutex::new(folder) }
        }
    }

    impl FileHandles for FakeFileHandles {
        fn asks_handles(&self) -> bool {
            self.asks
        }
        fn is_disk(&self, _file: &File) -> bool {
            !self.not_disk
        }
        fn final_path(&self, _file: &File) -> Option<PathBuf> {
            lock(&self.final_path).clone()
        }
        fn final_directory_path(&self, path: &Path) -> Option<PathBuf> {
            Some(lock(&self.final_directory).clone().unwrap_or_else(|| path.to_path_buf()))
        }
    }

    /// One thing a [`RecordingPathProbe`] was asked.
    #[derive(Debug, Clone, PartialEq, Eq)]
    pub enum PathProbeCall {
        FixedDrive(char),
        LinkTarget(PathBuf),
        FinalPath(PathBuf),
    }

    /// A disk as a test sets it up, recording every question: the fixed drives (by upper-case letter; empty: every drive is fixed),
    /// the links (a path and where it leads) and the final paths of what exists (a path and what the file system calls it; a path
    /// not listed does not exist). `cannot` makes it an OS that cannot ask.
    #[derive(Debug, Default)]
    pub struct RecordingPathProbe {
        pub cannot: bool,
        pub fixed: Vec<char>,
        pub links: Vec<(PathBuf, PathBuf)>,
        pub finals: Vec<(PathBuf, PathBuf)>,
        pub calls: Mutex<Vec<PathProbeCall>>,
    }

    impl RecordingPathProbe {
        pub fn calls(&self) -> Vec<PathProbeCall> {
            lock(&self.calls).clone()
        }
    }

    impl PathProbe for RecordingPathProbe {
        fn checks_paths(&self) -> bool {
            !self.cannot
        }
        fn fixed_drive(&self, letter: char) -> bool {
            lock(&self.calls).push(PathProbeCall::FixedDrive(letter));
            self.fixed.is_empty() || self.fixed.contains(&letter.to_ascii_uppercase())
        }
        fn link_target(&self, path: &Path) -> Option<PathBuf> {
            lock(&self.calls).push(PathProbeCall::LinkTarget(path.to_path_buf()));
            self.links.iter().find(|(link, _)| link == path).map(|(_, target)| target.clone())
        }
        fn final_path(&self, path: &Path) -> Option<PathBuf> {
            lock(&self.calls).push(PathProbeCall::FinalPath(path.to_path_buf()));
            self.finals.iter().find(|(at, _)| at == path).map(|(_, named)| named.clone())
        }
    }

    /// A PDF whose first page is `width` 횞 `height` points (none when `pages` is 0), drawn as `png`; it records the sizes asked for.
    #[derive(Debug, Default)]
    pub struct FakePdf {
        pub pages: usize,
        pub width: f64,
        pub height: f64,
        pub png: Vec<u8>,
        pub fails: bool,
        pub drawn: Mutex<Vec<(u32, u32)>>,
    }

    impl PdfRaster for FakePdf {
        fn first_page(&self, _data: &[u8], size: &(dyn Fn(f64, f64) -> Option<(u32, u32)> + Sync)) -> Result<Option<PdfPage>> {
            if self.fails {
                return Err(PlatformError::Refused("the document does not load".into()));
            }
            if self.pages == 0 {
                return Ok(None);
            }
            let png = size(self.width, self.height).map(|pixels| {
                lock(&self.drawn).push(pixels);
                self.png.clone()
            });
            Ok(Some(PdfPage { width: self.width, height: self.height, png }))
        }
    }

    /// One thing the desktop was asked.
    #[derive(Debug, Clone, PartialEq, Eq)]
    pub enum DesktopCall {
        Reveal(PathBuf),
        Open(PathBuf),
        Copy { width: u32, height: u32, png: usize },
    }

    /// Records every call; `open` answers `opens` (a refused program is the real port's to decide).
    #[derive(Debug, Default)]
    pub struct RecordingDesktop {
        pub calls: Mutex<Vec<DesktopCall>>,
        pub refuses: bool,
    }

    impl RecordingDesktop {
        pub fn calls(&self) -> Vec<DesktopCall> {
            lock(&self.calls).clone()
        }
    }

    impl Desktop for RecordingDesktop {
        fn reveal(&self, path: &Path) -> Result<()> {
            lock(&self.calls).push(DesktopCall::Reveal(path.to_path_buf()));
            Ok(())
        }
        fn open(&self, path: &Path) -> Result<bool> {
            lock(&self.calls).push(DesktopCall::Open(path.to_path_buf()));
            Ok(!self.refuses)
        }
        fn copy_image(&self, image: &ClipboardImage) -> Result<()> {
            lock(&self.calls).push(DesktopCall::Copy { width: image.width, height: image.height, png: image.png.len() });
            Ok(())
        }
    }

    /// Answers every dialog with `choice` and records what it offered.
    #[derive(Debug, Default)]
    pub struct FakeSaveDialog {
        pub choice: Mutex<Option<PathBuf>>,
        pub asked: Mutex<Vec<SaveRequest>>,
    }

    impl SaveDialog for FakeSaveDialog {
        fn pick(&self, request: SaveRequest) -> BoxFuture<'_, Result<Option<PathBuf>>> {
            lock(&self.asked).push(request);
            let choice = lock(&self.choice).clone();
            Box::pin(async move { Ok(choice) })
        }
    }
}
