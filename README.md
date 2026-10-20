# Aphelion

A Minecraft: Java Edition launcher for Windows, rebuilt from scratch as a successor to the ideas of
[PCL2](https://github.com/Hex-Dragon/PCL2) — none of its code, structure or storage conventions were carried over.
The interface follows a Mission Control console language ("NASA-punk"): graphite panels, recessed data
screens, annunciator lamps that report real system state, and a guarded ignition button.

![Launch control](docs/screenshots/01-launch.png)

## What it does

| Area | Capabilities |
| --- | --- |
| Instances | Create vanilla, Fabric, Quilt, Forge (legacy and modern) and NeoForge instances; each is one self-contained folder. Rename, recolour, isolate, repair by hash, delete to the Recycle Bin. Reads existing `.minecraft` folders from other launchers. |
| Launch | Six-stage sequence (verify files → Java → account → natives → arguments → ignition), per-version Java selection with automatic download of Mojang runtimes, automatic heap sizing, Quick Play server join, live log console, rule-based crash diagnosis. |
| Accounts | Offline, Microsoft (device-code sign-in), and authlib-injector servers (LittleSkin, Blessing Skin). Tokens are encrypted with DPAPI; passwords are never stored. |
| Content | Modrinth search and install with required dependencies, update checks by file hash, drag-and-drop install, enable/disable, `.mrpack` import and export, world list. |
| Downloads | Parallel, resumable-by-file, SHA-1 verified, atomic. Official sources with automatic fallback to the BMCLAPI mirror. |
| Lifecycle | Standard per-user install (no admin), portable mode, in-place upgrade, uninstall that keeps data by default, explicit full cleanup. |

## Quick start

Requirements: Windows 10/11 x64, the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0),
and Microsoft Edge WebView2 (preinstalled on Windows 11).

```powershell
pwsh ./build.ps1              # UI bundle, checks, unit tests, single-file publish
./artifacts/publish/Aphelion.exe
```

The build is one file. Running it directly works immediately (data goes to `%LOCALAPPDATA%\Aphelion`).
**Settings › Data and storage › Install on this PC** copies it to `%LOCALAPPDATA%\Programs\Aphelion`
and registers it in Windows Settings › Apps. To run portably, put an empty file named
`Aphelion.portable` next to the executable.

## Documentation

- [Architecture](docs/architecture.md) — projects, layers, the UI bridge, technology choices
- [Data, install and uninstall](docs/lifecycle.md) — where every file lives and what removes it
- [Accounts](docs/accounts.md) — Microsoft client ID setup, authlib-injector, token storage
- [Development](docs/development.md) — building, testing, the smoke test, the design canvas
- [Compared with PCL2](docs/pcl2-comparison.md) — what was deliberately not carried over

## Status

This is a local, unpublished build (version 0.1.0). Eight version/loader combinations were installed and launched
for real, and install/upgrade/uninstall were exercised on Windows 11; see [docs/development.md](docs/development.md#verification)
for exactly what has been verified and what has not.

Minecraft is a trademark of Mojang Studios. Aphelion is not affiliated with Mojang or Microsoft.
