# Changelog

All notable changes to Fluxa are documented here. Versions follow the `Version` property in
`Directory.Build.props`.

## 0.2.0 — 2026-10-16

### Added

- **Video conversion**, powered by FFmpeg. Inputs: MP4, MKV, MOV, WebM, AVI, WMV, FLV, M4V,
  MPG/MPEG, TS/MTS, 3GP and OGV. Outputs: MP4 (H.264), WebM (VP9), MKV (H.264), MOV (H.264),
  AVI (MPEG-4) and animated GIF.
- **Video options**: quality presets (High, Balanced, Compact), an audio track toggle, and
  container metadata removal.
- **FFmpeg detection prompt** with a download link, PATH instructions, and a manual
  `ffmpeg.exe` picker.

### Changed

- **FFmpeg is an external dependency and is not bundled.** Fluxa looks for `ffmpeg.exe` beside
  the executable, on `PATH`, in WinGet's link directory, and in the common install locations,
  and reports a clear error when it cannot find one. Install it with
  `winget install Gyan.FFmpeg`, or drop `ffmpeg.exe` into the app folder. Image conversion
  needs no external tools.
- README shortened.
