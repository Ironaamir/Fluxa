# Fluxa

A native **C# / WinUI 3** Windows desktop converter, renamed from FileConverter. Image conversion works now; Video, Audio, and PDF conversion and all four compression modes have navigable, clearly marked future workspaces. Files are processed locally. **Cloud imports are not included in this release** (see [CHANGELOG.md](CHANGELOG.md)).

## Run

Download the **portable Windows x64 ZIP** from release assets when available, then **extract the entire ZIP** and open `dist/Fluxa.exe` inside it. GitHub's **Source code.zip** is not a runnable download; the original 0.1.0 release was source-only. **Keep the entire `dist` folder together**: the executable needs the adjacent native codecs, WinUI resources, and runtime files. This is an unpackaged, self-contained Windows x64 build (~250 MB), not an installer or a single-file executable. No administrator rights are needed to run it.

Alternatively, right-click `Start.ps1` and choose **Run with PowerShell**, or run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Start.ps1
```

When run from a source checkout without `dist`, `Start.ps1` attempts a one-time build using `scripts/Publish.ps1`. Building requires the .NET 8 SDK and Windows WinUI build tools; machines without them get an actionable explanation, not "Published app not found." A portable runtime ZIP needs neither the SDK nor build tools. Launcher paths resolve relative to the script, including folders containing spaces.

Built and exercised on Windows 11 x64. Windows 10 19041+ is the project minimum, but has not been tested.

## Use

1. Choose **Images** in the sidebar.
2. Drop local image files onto the accent-bordered area, or use **Browse images**. Multiple files are supported. Folders are not recursively imported.
3. Select the output format, frame policy, and quality where applicable. Choose an output folder.
4. The **Queue** is at the top, above imports and conversion options. Click **Convert images**. The action bar stays visible while the workspace scrolls. Successful items immediately move out of the queue into **Finished**, below the options. Failed/cancelled items stay retryable in the queue. **Clear queue** and **Clear finished** affect only their own section; neither deletes files from disk. Finished history does not block re-adding the same sources or consume the 500-pending-file limit. Re-add one or more sources to convert them again without clearing Finished; duplicates already pending are ignored. Both lists are session-only.
5. Use **Open output folder** to view results. Originals and existing output files are never overwritten. Name collisions get ` (1)`, ` (2)`, etc.

Settings remember output folder/format, quality, metadata removal, frame policy, optional close-to-tray behavior, accent color, and light/dark appearance. Preferences live in `%LOCALAPPDATA%\Fluxa\settings.json`. Existing `%LOCALAPPDATA%\FileConverter\settings.json` preferences are read as a fallback when Fluxa has no settings file; the old file is never modified. Unexpected UI errors are recorded in Fluxa's `crash.log`.

### Cloud imports

Google Drive and Dropbox imports were added during this cycle, then **removed in 0.1.1**. Google's desktop OAuth endpoint rejects the secret-less PKCE exchange (`client_secret is missing`), and the only working alternative embeds the generated client secret in the distributed executable, where anyone can extract it. That value would be effectively public, so cloud imports were cut rather than shipped. See [CHANGELOG.md](CHANGELOG.md).

If you linked an account while testing a development build, revoke Fluxa's access under [Google account permissions](https://myaccount.google.com/permissions) and [Dropbox connected apps](https://www.dropbox.com/account/connected_apps). Every conversion feature works entirely on local files.

### Appearance and navigation

Open **Settings**, choose **Dark** or **Light** and one of **Red / Orange / Yellow / Green / Blue / Purple**, then **Save**. Changes apply without restarting. Cancel leaves the appearance unchanged. **Blue + Dark** is the default. The six palettes are fixed, bundled values: Fluxa never reads desktop palettes or Windows accent preferences at runtime. Text accents are brightened/darkened only as needed for contrast. Selection borders, Settings Save (including hover), slider tracks/thumbs, toggles, checkboxes, and popup controls use the selected Fluxa palette. Light surfaces are exactly `#F0F2EF` and `#E3E7E4`; dark surfaces remain `#1C1D1C` and `#292B2A`.

Each sidebar section, **Conversion modes** and **Compression modes**, has an independent arrow toggle. Expanded/collapsed state survives restarts. Compression icons have a compact badge to distinguish them from conversion icons. Sidebar hover changes directly to neutral gray (`#3B3C3C` in dark mode, `#D1D7D3` in light mode), with no stock white brush transition.

Compression pages are navigation scaffolding, not working compressors. Image, Video, Audio, and PDF compression each show their own subtitle and explicit coming-later message.

### Explorer context menu

On launch, Fluxa registers **Convert to .png with Fluxa** and **Open in Fluxa** for its supported image extensions, only for the current Windows user. No administrator privileges or default-file-association changes are needed. On Windows 11, right-click the image and choose **Show more options** to see these classic desktop commands.

- **Convert to .png with Fluxa** opens/restores Fluxa and immediately converts only those selected files. In **Settings → Windows context menu → Context menu converts to**, select PNG, JPEG, WebP, or any available image output and Save. The label updates to match. This preference is separate from the workspace format.
- **Open in Fluxa** opens/restores the Images workspace and adds the selection to its pending queue, without starting conversion.
- Context conversions use the current output folder, quality, metadata removal, and frame policy. Originals are untouched. Files already manually queued are not swept into an unrelated context-menu batch.
- A single Fluxa window receives later launches, including when hidden in the tray. Requests arriving during a conversion wait until it finishes.
- Classic `Document` verbs support Explorer selections of **up to 15 images**. Larger selections may hide the commands; use Fluxa's multi-file picker or drag/drop instead (up to 500 pending files).
- Toggle **Show Fluxa in the image context menu** off and Save to remove only Fluxa's two verbs. If you move the distribution folder, launch Fluxa from its new location to refresh registration. Disable integration before permanently deleting the app.
- These commands are stored under `HKCU\Software\Classes\SystemFileAssociations\<extension>\shell\Fluxa.Convert` / `Fluxa.Open`. No registry-backed script execution or image association takeover is used.

References: [Microsoft selection-model limits](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-employ-the-verb-selection-model), [Windows 11 classic context menus](https://blogs.windows.com/windowsdeveloper/2021/07/19/extending-the-context-menu-and-share-dialog-in-windows-11/).

### Tray

Left or right click the tray icon for **Images / Video / Audio / PDF / Open / Exit**. Choosing a mode shows/restores the window at that mode. Windows may put the icon in its hidden-icons overflow.

- Closing normally exits unless **Keep running in the system tray** is enabled.
- Closing during conversion retains the job; reopen from the tray.
- **Cancel remaining** and tray **Exit** take effect between native codec steps, not in the middle of a blocking decode/encode. Exit waits for that step to finish safely.
- The icon is cleaned up at normal exit and registered again when Explorer restarts.
- If tray registration fails, a warning is displayed; an in-progress conversion does not hide an unreachable window.

## Formats

The dropdown is built from a deliberate raster-format catalog intersected with the installed codec's write capabilities, not from a promise of literally every image format. Every output below was encoded, decoded, and converted back to PNG in the automated tests:

**PNG, JPEG, WebP, AVIF, TIFF, BMP, GIF, ICO, JPEG XL, JPEG 2000, TGA, PSD, OpenEXR, Radiance HDR, PPM, PGM, PBM, QOI, DDS, PCX, XBM.**

Inputs include those raster families plus **HEIC / HEIF** import, and JPEG/TIFF extension aliases. HEIC/HEIF imports are reported available by the bundled decoder but have not been exercised with real device fixtures. **HEIC/HEIF export is intentionally unavailable.** The bundled HEIC writer has licensing constraints. HEIC camera variations and unsupported/corrupt raster files may produce a per-file decoder error.

- Input type is selected explicitly from the supported extension and read from a stream. Renamed PDF/SVG/script inputs are not passed to an automatic external delegate.
- SVG, PDF, PostScript, RAW camera families, video, and audio are not implemented in this first version. No Ghostscript or FFmpeg dependency is installed.
- **Preserve frames/pages** is the default. Multi-frame input to an output that cannot represent a sequence fails clearly rather than silently discarding frames. **First frame only** explicitly permits single-frame export.
- Animated GIF, multipage TIFF, and animated WebP sequence retention are tested. Other combinations may be rejected by the codec and should be tested before being relied upon.
- JPEG transparency is flattened against white. PNG retains alpha. ICO output fits within 256 × 256 while retaining aspect ratio.
- Metadata removal defaults on, including embedded color profiles and location information. Disable it to retain profiles/metadata where the destination format permits them. Auto-orientation happens before stripping.
- The Q8 engine uses **8-bit components**. EXR/HDR/high-bit-depth sources are not archival-grade round trips; high-depth/HDR precision can be lost. The tests verify valid conversion and dimensions, not archival color fidelity.
- Grayscale/bitmap formats necessarily reduce colors; JPEG/AVIF/WebP and similar lossy formats can change pixels. Not every destination preserves layers, metadata, color space, or animation semantics.

### Resource policy

512 MB maximum input file, 500 queued items, 256 frames, and 32,768 pixels per dimension. Conversions are serialized off the UI thread. Pixel-cache memory is capped at 256 MB with up to 1 GB disk cache and two ImageMagick worker threads. **This is a codec cache policy, not a hard limit on total process memory or total execution time.** Native delegates can use additional memory. Cancellation is cooperative at codec boundaries.

## Build and test

Prerequisites: Windows x64, .NET 8 SDK, Windows SDK/build tools with WinUI XAML tooling. The existing Visual Studio Build Tools installation was sufficient; the Visual Studio IDE is not required.

```powershell
dotnet build .\Fluxa.sln -c Release -p:Platform=x64
dotnet test .\tests\Fluxa.Tests\Fluxa.Tests.csproj -c Release
dotnet publish .\src\Fluxa.App\Fluxa.App.csproj -c Release -p:Platform=x64 --self-contained true -o dist
```

The app project includes a publish target to retain compiled XAML (`.xbf`) and its application resource index (`.pri`). Omitting these gives a launch-time XAML failure even when publishing succeeds.

For a clean portable ZIP (audited runtime only: no tests, source, debug symbols, personal settings, or logs):

```powershell
.\scripts\Publish.ps1
```

Dependencies: Windows App SDK 2.5.1, Magick.NET-Q8-x64 14.17.2. Test dependencies: xUnit and Microsoft.NET.Test.Sdk. .NET 8 approaches end of support in November 2026; plan a tested SDK upgrade before release distribution.

For native UI checks, launch the published app first, then run on an interactive desktop:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-Desktop.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-Tray.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-Appearance.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-Revision.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-Explorer.ps1
```

The desktop test uses real Windows UI Automation, file/folder dialogs, and conversion controls. The appearance test checks all twelve palette/theme combinations using screen pixels, collapsible groups, compression destinations, Cancel behavior, pointer hover, and failed-file retry. The tray test simulates Shell's callback, then physically clicks the actual native menu; it does not automate Explorer's icon surface. Actual drag/drop testing uses the isolated WinForms OLE source helper: start `scripts/Start-DragSource.ps1` in a separate PowerShell process, then run `scripts/Test-Drop.ps1` with the app visible in Images mode and an empty queue. Avoid typing or moving the mouse during desktop tests.

See [CHANGELOG.md](CHANGELOG.md) for what changed between versions and [AGENT-HANDOFF.md](AGENT-HANDOFF.md) for agent-level verification details and remaining limitations.

## Architecture

- `src/Fluxa.Core/Conversion.cs`: UI-independent image engine, options/result records, mode enum, and `IConversionEngine`. Add dedicated request models/engines for video/audio/PDF rather than forcing non-image controls into image options.
- `src/Fluxa.App/MainWindow.xaml.cs`: native UI, pickers, file queue, progress, and mode routing. A minimal standard XAML entry point retains WinUI's supported application/metadata infrastructure.
- `src/Fluxa.App/Program.cs`: Windows App SDK single-instance activation redirection before XAML startup.
- `src/Fluxa.App/ExplorerIntegration.cs` / `LaunchRequest.cs`: per-user supported-image verbs, safe quoted arguments, and explicit open/convert requests.
- `src/Fluxa.Core/FileQueue.cs`: pending-only deduplication and capacity, independent of completed history.
- `src/Fluxa.App/TrayIcon.cs`: owned Win32 notification-area icon and menu; no additional tray dependency.
- `src/Fluxa.App/ThemePalette.cs`: six palette definitions and contrast-safe light/dark color tokens; plain C# also exercised in the test project.
- `src/Fluxa.App/AppSettings.cs`: persisted appearance, conversion, navigation-group, and tray preferences, plus read-only migration of old settings (old cloud-preference keys are ignored).

## Design

The updated `ConverterIcon.png` supplied on your desktop is bundled as `Assets/AppIcon.png`, plus a generated 16/24/32/48/64/128/256-pixel `.ico` for executable/window/tray use. No dependency on the original Desktop paths remains. The artwork itself stays the supplied blue design across all themes.

Palettes derive from `RedPallete.png`, `OrangePallete.png`, `YellowPallete.png`, `ColorScheme.png` (Green), `BluePallete.png`, and `PurplePallete.png`. The compressed palette images contain slight pixel variations; their intended accent ramps are represented by these consistent tokens:

| Palette | Ink | Deep | Pressed | Hover | Accent |
| --- | --- | --- | --- | --- | --- |
| Red | `#270B0B` | `#5A1414` | `#8B2C2C` | `#9D3131` | `#B34040` |
| Orange | `#27190B` | `#5A3714` | `#8B5B2C` | `#9D6731` | `#B37A40` |
| Yellow | `#27250B` | `#5A5414` | `#8B832C` | `#9D9431` | `#B3A940` |
| Green | `#11270B` | `#3C5A14` | `#598B2C` | `#669D31` | `#71B340` |
| Blue | `#0B1427` | `#142B5A` | `#2C4C8B` | `#31559D` | `#4066B3` |
| Purple | `#1E0B27` | `#43145A` | `#6B2C8B` | `#79319D` | `#8D40B3` |

The muted ramps fit the existing green theme. Brighter dark-mode accent text and darker light-mode accent text avoid low-contrast labels without changing the palette's character. Neutral text, buttons, cards, title bar, queue rows, and status text respond to saved appearance changes; no system accent setting is changed.

References: [WinUI unpackaged deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app), [Shell_NotifyIcon](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw), [Magick.NET](https://github.com/dlemstra/Magick.NET).
