# File Decomposition

Large files should be decomposed by workflow responsibility.

## Guidelines

Files larger than ~1000 lines should be split.

Suggested MainWindow structure:

- MainWindow.Playback.cs
- MainWindow.Subtitles.cs
- MainWindow.Library.cs
- MainWindow.Shortcuts.cs
- MainWindow.WindowModes.cs
