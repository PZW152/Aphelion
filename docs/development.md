# Development

## Requirements

- .NET 10 SDK, Node.js 22+ (npm), Windows 10/11 x64 with WebView2.

## Build

```powershell
pwsh ./build.ps1                 # npm ci → svelte-check → vite build → unit tests → publish
pwsh ./build.ps1 -SelfContained  # bundle the .NET runtime (larger, no runtime prerequisite)
```

Output: a single `artifacts/publish/Aphelion.exe` (≈2.4 MB) with the UI and the WebView2 loader embedded.

## Working on the interface

```powershell
npm run dev --prefix src/Aphelion.Web    # http://localhost:5178 with a mock backend
```

Outside WebView2 the page talks to `src/lib/mock.ts`, which returns sample data for every page, so the
interface can be reviewed in any browser. The mock is excluded from production bundles.

To inspect the real app: set `APHELION_DEVTOOLS=1` (enables DevTools), or
`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` to attach over CDP.
Set `APHELION_HOME=<folder>` to keep development data away from your real data.

## Tests

| Suite | What it covers | How to run |
| --- | --- | --- |
| Unit (137 tests, offline) | rules, Maven paths, version merging, library/native resolution, argument building, memory sizing, offline UUIDs, log4j parsing, crash rules, atomic files, settings migrations, data paths, account store and encryption boundary, redaction, mirror mapping, Java selection, instance store and rename, zip-slip protection, mod metadata, NBT world reading, loader catalogue mapping, task hub | `dotnet test tests/Aphelion.Core.Tests` |
| Type check | Svelte components and TypeScript (fails on warnings, including accessibility warnings) | `npm run check --prefix src/Aphelion.Web` |
| Smoke (network) | installs real versions from Mojang/Fabric/Quilt/Forge/NeoForge and launches each until its window appears | `dotnet run --project tools/Aphelion.Smoke -c Release -- <dataRoot> vanilla:1.21.1 fabric:1.21.1 …` |

## Verification

Results from this machine (Windows 11 26300, x64, 150 % display scaling), 2026-10-20.

### Verified

| Area | Result |
| --- | --- |
| Install + launch to a running game window | vanilla 1.21.1, vanilla 1.8.9, Fabric 1.21.1, Quilt 1.21.1, NeoForge 21.1 (1.21.1, processors), Forge 47.4 (1.20.1, processors), Forge 14.23.5 (1.12.2), Forge 10.13.4 (1.7.10, legacy installer) |
| Java | local JDK 21 picked for modern versions; Mojang `jre-legacy` (Java 8) downloaded automatically for 1.8.9/1.12.2/1.7.10 |
| Desktop app | WebView2 host renders the embedded UI; launch from the ignition button starts the game; live log console; stop from the UI; play time recorded |
| Modrinth | search, project details, install of Mod Menu resolved Placeholder API and Fabric API automatically; mod icons and metadata read from jars |
| Crash diagnosis | an invalid JVM option produced the `crash.badJvmArg` finding in the UI |
| Single file | the exe alone in an empty folder starts and renders; the loader is loaded from `runtime/webview2-loader/`; a tampered copy is detected and rewritten on the next start |
| Window chrome | drag to move, double-click to maximise/restore, maximise/close buttons, no clipping when maximised, correct size at 150 % scaling |
| Lifecycle | install (files, Start menu shortcut, Settings › Apps entry, manifest), in-place upgrade, uninstall keeping data, uninstall with `--purge`, temporary uninstaller removes itself |
| Bugs found by these runs and fixed | Fabric timestamps without a colon in the offset; embedded UI paths with backslashes; ROC-calendar dates in file names under zh-TW; DPI-unaware initial window size; start/exit event race for a JVM that dies instantly; old Forge needing `config/`; self-delete blocked by its own working directory |

### Not verified

- Microsoft sign-in end to end: it requires an approved Azure client ID, which this build does not include (see [accounts.md](accounts.md)). The token chain is implemented to the documented protocol but has not been exercised against Microsoft's services here.
- Authlib-injector sign-in against a live server (no test account was available).
- `.mrpack` import and export with a real modpack.
- Windows 10, ARM64 and 32-bit Windows; displays other than 150 %.
- The interactive uninstall dialog (the quiet paths were tested; the dialog was not clicked through).
- Self-contained publish (`-SelfContained`).

## Design canvas

The visual system was designed first as a Claude Design canvas ("Aphelion Launcher — NASA-punk":
tokens, launch control, launch sequence, instances, download, settings in the light theme). The
implementation in `src/Aphelion.Web` follows it; `docs/screenshots/` shows the real application.
