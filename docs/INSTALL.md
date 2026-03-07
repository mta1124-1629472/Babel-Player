# Install and Run

1. Install Visual Studio 2022/2023 with:
   - .NET Desktop Development workload
   - Windows App SDK / WinUI tooling
2. Open `PlayerApp.sln`.
3. Restore NuGet packages.
4. Build and run `PlayerApp.UI`.

## Use the app
1. Click **Open Video** and pick a local video.
2. Click **Load Subtitles (.srt)** and pick an SRT file for that video.
3. The app loads and translates subtitle cues locally.
4. Start playback and translated subtitles appear in real time.
5. Optional: click **Export Translated SRT** to save the translated cue track.

## Notes
- Translation service is local/offline fallback logic in this MVP (`MtService`).
- `scripts/download_models.ps1` and `model_manifest.json` are kept for future ONNX model-based translation.
