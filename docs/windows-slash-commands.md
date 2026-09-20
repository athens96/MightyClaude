# Windows slash-command discovery: decided differences

## Plugin path check

macOS (`SlashCommands.swift`): `path.hasPrefix("/")` — checks for a Unix absolute path.

Windows (`SlashCommands.cs`): `Path.IsPathRooted(candidate)` — covers both `C:\…` (Windows drive-rooted) and `/…` (Unix absolute) in a single cross-platform call, so the Core library compiles and tests correctly on the Mac CI runner as well.

## Row marks

macOS (`SlashCommandPalette.swift`) draws the two row marks with SF Symbols — `arrow.turn.down.left` for an app action and `chevron.right` for a command that continues with an argument. WinUI has no SF Symbols, so `MainWindow.SlashPalette.cs` draws the same two marks as the text glyphs `↵` and `›` with the identical tooltips (`SlashCommandStrings.PaletteActionTooltip` / `PaletteArgumentTooltip`).

## App actions left out of the Windows palette

None. `SlashPalette.UnavailableActions` lists the app actions this client has no screen for at all, and it is empty: every macOS app action has a Windows counterpart the palette calls — `OpenPlugins`, `NewConversation`, `ShowUsage`, `OpenSettings`, `Rename`, `Help`, `SetModel` and `SetPermission`.

`SlashPalette.Builtins` no longer filters any provider's rows either, so for every provider the Windows palette offers exactly the built-ins `SlashCommandCatalog.Builtins` — the macOS list, left untouched — offers:

- `OpenPlugins` — `/plugin` opens the Claude plugin window and `/plugins` opens the Codex plugin window; both go to `OpenPluginBrowser(pane.Provider)` (`docs/windows-plugins.md`, `MainWindow.Plugins.cs`). Gemini has no plugin browser on macOS, so the macOS catalog gives it no row and the palette invents none.

The `codex plugin palette …` and `slash palette leaves out actions Windows cannot do` Core checks assert both facts: `UnavailableActions` is empty, and each provider's palette rows equal its catalog rows one for one.
