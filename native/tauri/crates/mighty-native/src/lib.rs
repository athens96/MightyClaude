//! Mighty Code's native forms (W6-A): the controls the C# settings builders use as a small virtual WinUI (`ui`), the port of C#
//! `WebNativeReader` (WebNative.cs) that reads them into the page's NativeNode JSON (`reader`, `ids`, `act`), the colour names it
//! writes (`tokens`), the settings kit (`kit`), and the views and surfaces the host pushes (`view`, `surface`). No Tauri, no OS code.

pub mod act;
pub mod ids;
pub mod kit;
pub mod reader;
pub mod surface;
pub mod tokens;
pub mod ui;
pub mod view;
