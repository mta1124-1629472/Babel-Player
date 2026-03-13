# Split Large Shell File

Decompose a large WinUI file into smaller workflow modules.

## Steps

1. Analyze the file for workflow clusters.
2. Identify modules such as:
   - playback
   - subtitles
   - shortcuts
   - library
   - window modes
3. Extract code into partial classes.
4. Keep MainWindow.xaml.cs minimal.

## Output

Suggested file structure:

MainWindow.Playback.cs  
MainWindow.Subtitles.cs  
MainWindow.Library.cs  
MainWindow.Shortcuts.cs  
MainWindow.WindowModes.cs
