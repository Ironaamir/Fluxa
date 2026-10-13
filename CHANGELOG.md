# Changelog

All notable changes to Fluxa are documented here. Versions follow the `Version` property in
`Directory.Build.props`.

## 0.1.1 — 2026-10-13

### Removed

- **Google Drive and Dropbox imports.** Added for this release, then cut before shipping.
  Google's desktop OAuth endpoint rejects the secret-less PKCE exchange with
  `invalid_request: client_secret is missing`, and the only working alternative embeds the
  generated client secret inside the distributed executable, where it can be extracted.
  Rather than ship a value that cannot be kept confidential in a native app, the feature is
  gone.
  - Removed the **Import from** row, the cloud folder browser, browser sign-in and linking,
    and the cloud settings section.
  - Removed the cloud import code, provider APIs and token store from the app and tests.
    Leftover cloud keys in an existing `settings.json` are ignored harmlessly.
  - `scripts/Publish.ps1` no longer accepts Google or Dropbox app identifiers and never
    embeds OAuth registration data.
  - If you linked an account while testing, revoke access under
    [Google account permissions](https://myaccount.google.com/permissions) and
    [Dropbox connected apps](https://www.dropbox.com/account/connected_apps).

### Fixed

- **`Start.ps1` reported "Published app not found".** The 0.1.0 release was source-only, and
  the launcher only checked for `dist/Fluxa.exe`, which cannot exist in a source ZIP. It now
  launches an adjacent portable `dist` directly from any folder (spaces included), builds a
  source checkout when the .NET SDK is available, and otherwise explains whether the portable
  ZIP or the build tools are needed.

### Added

- **`scripts/Publish.ps1`** produces the portable distribution: an audited ZIP plus a SHA-256
  checksum. It publishes only the App project into isolated staging, verifies the required
  WinUI and native resources, audits the content, preserves the previous `dist` under ignored
  artifacts, and refuses to overwrite an existing archive.
- **`CHANGELOG.md`** — this file.

### Changed

- Shared copyright is now **Fluxa contributors** instead of a personal account name, so no
  account name is embedded in the runtime. Release publishing also disables debug symbols and
  git revision suffixes.

### Verification

- `dotnet build Fluxa.sln -c Release -p:Platform=x64`: 0 warnings, 0 errors.
- `dotnet test`: 49 passed, 0 failed, 0 skipped.
- The packaged ZIP was extracted and launched through its own `Start.ps1` from a different
  working directory. The executable reports file version `0.1.1.0` and product `0.1.1`.

## 0.1.0 — 2026-10-13

- First public release. Native WinUI 3 image converter with a full raster format catalog, queue/finished workflow, tray, Explorer context-menu integration, six accent palettes with light/dark themes, and source-only GitHub release.
