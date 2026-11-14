//! Mighty Code's native forms (W6-A): the controls the C# settings builders use as a small virtual WinUI (`ui`), the port of C#
//! `WebNativeReader` (WebNative.cs) that reads them into the page's NativeNode JSON (`reader`, `ids`, `act`), the colour names it
//! writes (`tokens`), the settings kit (`kit`), the views and surfaces the host pushes (`view`, `surface`), and what makes a push
//! comparable with C#'s capture (`parity`). No Tauri, no OS code.

pub mod act;
pub mod ids;
pub mod kit;
pub mod parity;
pub mod reader;
pub mod surface;
pub mod tokens;
pub mod ui;
pub mod view;
