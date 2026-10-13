# Changelog

All notable changes to Fluxa are documented here. Versions follow the app's `Directory.Build.props` version.

## 0.1.1 (unreleased)

### Removed

- **Google Drive and Dropbox imports.** Added during this cycle, then removed at the publisher's request. Google's desktop OAuth flow rejects the standard secret-less PKCE exchange with `invalid_request: client_secret is missing`, and the only working fallback embeds the generated client secret in the distributed executable, where anyone can extract it. That value would be public, so cloud imports were cut entirely rather than shipped.
  - Removed the **Import from** import sources, the cloud folder browser, browser sign-in/linking, and the cloud settings section.
  - Removed cloud import code, provider APIs and the token store from the app and tests; leftover cloud settings keys in old `settings.json` files are ignored harmlessly.
  - `scripts/Publish.ps1` no longer accepts Google or Dropbox app identifiers and never embeds OAuth registration data.
  - Existing users should revoke Fluxa's access under [Google account permissions](https://myaccount.google.com/permissions) and [Dropbox connected apps](https://www.dropbox.com/account/connected_apps) if they linked an account during testing.

### Changed

- **Launcher and distribution.** `Start.ps1` no longer fails with "Published app not found": it launches a portable `dist` directly from any folder (spaces included), builds a source checkout when .NET is available, and otherwise explains whether the portable ZIP or the build tools are needed. The original 0.1.0 GitHub release was source-only, which caused the error.
- **Clean portable packaging.** New `scripts/Publish.ps1` produces an audited ZIP (runtime only: no tests, source, debug symbols, personal settings, tokens, or logs) plus a SHA-256 checksum, without touching Git.

### Verification

- Solution builds with 0 warnings/0 errors; all unit tests pass (see `AGENT-HANDOFF.md` for the latest count and detail).

## 0.1.0 (2026-10-13)

- First public release. Native WinUI 3 image converter with a full raster format catalog, queue/finished workflow, tray, Explorer context-menu integration, six accent palettes with light/dark themes, and source-only GitHub release.
