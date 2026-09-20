# Windows slash-command discovery: decided differences

## Plugin path check

macOS (`SlashCommands.swift`): `path.hasPrefix("/")` — checks for a Unix absolute path.

Windows (`SlashCommands.cs`): `Path.IsPathRooted(candidate)` — covers both `C:\…` (Windows drive-rooted) and `/…` (Unix absolute) in a single cross-platform call, so the Core library compiles and tests correctly on the Mac CI runner as well.

## Row marks

macOS (`SlashCommandPalette.swift`) draws the two row marks with SF Symbols — `arrow.turn.down.left` for an app action and `chevron.right` for a command that continues with an argument. WinUI has no SF Symbols, so `MainWindow.SlashPalette.cs` draws the same two marks as the text glyphs `↵` and `›` with the identical tooltips (`SlashCommandStrings.PaletteActionTooltip` / `PaletteArgumentTooltip`).

## App actions left out of the Windows palette

`SlashPalette.UnavailableActions` lists the app actions this client has no screen for. `SlashPalette.Builtins(provider)` drops their built-ins, so the palette never shows a row that would do nothing; `SlashCommandCatalog.Builtins` itself keeps the macOS list untouched.

- `OpenPlugins` (`/plugin` on Claude, `/plugins` on Codex) — left out: Windows has no plugin marketplace screen yet. It returns to the palette with the `Claude 플러그인 목록` parity row.

Every other macOS app action has a Windows counterpart the palette calls: `NewConversation`, `ShowUsage`, `OpenSettings`, `Rename`, `Help`, `SetModel` and `SetPermission`.
