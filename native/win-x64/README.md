# Windows x64 native dependencies

`libmpv-2.dll` is **not stored in Git** (it exceeds GitHub’s per-file size limit without LFS).

After cloning on Windows, fetch it (along with `tools/win-x64/uv.exe`, `ffmpeg.exe`, and `ffprobe.exe`) once:

```powershell
pwsh ./scripts/fetch-win-native-deps.ps1
```

Release builds use the same script in CI without extra flags, so the portable zip stays self-contained.
