# Windows slash-command discovery: decided differences

## Plugin path check

macOS (`SlashCommands.swift`): `path.hasPrefix("/")` — checks for a Unix absolute path.

Windows (`SlashCommands.cs`): `Path.IsPathRooted(candidate)` — covers both `C:\…` (Windows drive-rooted) and `/…` (Unix absolute) in a single cross-platform call, so the Core library compiles and tests correctly on the Mac CI runner as well.

## Row marks

macOS (`SlashCommandPalette.swift`) draws the two row marks with SF Symbols — `arrow.turn.down.left` for an app action and `chevron.right` for a command that continues with an argument. WinUI has no SF Symbols, so `MainWindow.SlashPalette.cs` draws the same two marks as the text glyphs `↵` and `›` with the identical tooltips (`SlashCommandStrings.PaletteActionTooltip` / `PaletteArgumentTooltip`).

## App actions left out of the Windows palette

`SlashPalette.UnavailableActions` lists the app actions this client has no screen for at all. It is empty today: every macOS app action has a Windows counterpart the palette calls — `OpenPlugins`, `NewConversation`, `ShowUsage`, `OpenSettings`, `Rename`, `Help`, `SetModel` and `SetPermission`. `SlashCommandCatalog.Builtins` itself keeps the macOS list untouched.

One action is left out per provider rather than for everyone:

- `OpenPlugins` — `/plugin` opens the Claude plugin window and `/plugins` opens the Codex plugin window (`docs/windows-plugins.md`, `MainWindow.Plugins.cs`). Both palettes include the action; `SlashPalette.UnavailableActions` is therefore empty today. Gemini has no plugin browser on macOS either, so its palette still omits the action.
