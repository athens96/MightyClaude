# Windows slash-command discovery: decided differences

## Plugin path check

macOS (`SlashCommands.swift`): `path.hasPrefix("/")` — checks for a Unix absolute path.

Windows (`SlashCommands.cs`): `Path.IsPathRooted(candidate)` — covers both `C:\…` (Windows drive-rooted) and `/…` (Unix absolute) in a single cross-platform call, so the Core library compiles and tests correctly on the Mac CI runner as well.
