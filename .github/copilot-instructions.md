# Copilot Workspace Instructions for Babel-Player

## Overview
Babel-Player is a WinUI 3 media player application for Windows focused on modern desktop UI components, media playback, and extensibility.

## Build and Test
- **Build**: Use Visual Studio or `dotnet build` in the terminal. Ensure WinUI 3 SDK is installed.
- **Run**: Launch via the Visual Studio debugger with `BabelPlayer.WinUI` as the startup project, or run `powershell -ExecutionPolicy Bypass -File .\scripts\run.ps1`.
- **Test**: Run unit tests with `dotnet test` (assumes xUnit or MSTest). Integration tests may require a Windows environment.
- **Common issues**: If builds fail, check for missing NuGet packages (e.g., Microsoft.WindowsAppSDK) or WinUI version mismatches.

## Architecture and Patterns
- **UI Framework**: WinUI 3 with XAML for views. Use MVVM (Model-View-ViewModel) for separation of concerns.
- **Key Components**:
  - Media playback: Leverage Windows.Media APIs or third-party libraries like FFmpeg.NET.
  - Data binding: Use ObservableCollection and INotifyPropertyChanged.
  - Async operations: Always use async/await for UI responsiveness.
- **File Structure**: 
  - `Views/`: XAML files for UI.
  - `ViewModels/`: Logic for views.
  - `Models/`: Data models.
  - `Services/`: Playback and utility services.
- **Conventions**:
  - Naming: PascalCase for classes/properties, camelCase for locals.
  - Error handling: Use try-catch with logging via Serilog or built-in ILogger.
  - Avoid: Blocking calls on UI thread; prefer DispatcherQueue for updates.

## Anti-Patterns
- Don't mix old UWP APIs with WinUI 3 without migration (e.g., replace ApplicationView with AppWindow).
- Avoid hardcoding paths; use ApplicationData for storage.
- Don't ignore platform differences (e.g., Win32 vs. UWP interop).

## Development Environment
- **IDE**: Visual Studio 2022+ with WinUI workload.
- **Dependencies**: Windows 10/11, .NET 6+, WinUI 3 SDK.
- **Pitfalls**: WinUI migration may break existing code; test on multiple Windows versions. If encountering API deprecation, refer to Microsoft docs for equivalents.

## Example Prompts
- "Generate a ViewModel for playlist management."
- "Fix async issues in media playback."
- "Add unit tests for the playback service."

For feedback or iterations, clarify specific areas (e.g., frontend UI or backend services).
