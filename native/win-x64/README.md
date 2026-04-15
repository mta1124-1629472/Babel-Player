# Windows x64 native dependencies

`libmpv-2.dll` is **not stored in Git** (it exceeds GitHub’s per-file size limit without LFS).

After cloning on Windows, fetch it (and `tools/win-x64/uv.exe`) once:

```powershell
pwsh ./scripts/fetch-win-native-deps.ps1
```

Release builds use the same script in CI with `-IncludeFfmpeg` so the portable zip stays self-contained.
