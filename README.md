# PlayerApp

PlayerApp is a local video player for Windows that shows **real-time translated subtitles**.

## What works now
- Open and play local videos (`.mp4`, `.mkv`, `.mov`, `.avi`, `.webm`)
- Load a local `.srt` subtitle track
- Detect subtitle language with lightweight heuristics
- Generate local translated subtitle text (offline fallback MT service)
- Render translated subtitle cues in sync with the active playback position
- Export translated subtitles to a new `.srt`

## Architecture
- `PlayerApp.UI`: WinUI 3 desktop app and player UI
- `PlayerApp.Core`: language detection, translation service, subtitle parsing/timeline/export, and hardware summary

## Quick start
See `docs/INSTALL.md`.
