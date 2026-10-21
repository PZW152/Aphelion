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
Set `APHELION_HOME=<folder>` to keep development data away from your real data, and
`APHELION_STANDARD_ROOT=<folder>` to stand in for `%LOCALAPPDATA%\Aphelion` when exercising mode switching.

## Tests

| Suite | What it covers | How to run |
| --- | --- | --- |
| Unit (182 tests, offline) | rules, Maven paths, version merging, library/native resolution, argument building, memory sizing, offline UUIDs, log4j parsing, crash rules, atomic files, settings migrations, data paths, relative data paths, mode-switch moves with rollback (rename and copy), damaged-Java rejection, official-source alternates, ranged downloads with resume and fallback, local file reuse, edge racing (including a blackholed address), update signature/product/version checks and verified update download, account store and encryption boundary, redaction, Java selection, instance store and rename, zip-slip protection, mod metadata, NBT world reading, loader catalogue mapping, task hub | `dotnet test tests/Aphelion.Core.Tests` |
| Type check | Svelte components and TypeScript (fails on warnings, including accessibility warnings) | `npm run check --prefix src/Aphelion.Web` |
| Smoke (network) | installs real versions from Mojang/Fabric/Quilt/Forge/NeoForge and launches each until its window appears | `dotnet run --project tools/Aphelion.Smoke -c Release -- <dataRoot> vanilla:1.21.1 fabric:1.21.1 …` |

## Releasing

1. Set `<Version>` in `Directory.Build.props` and commit.
2. Write the release notes in `docs/releases/<version>/{zh-Hant,zh-Hans,en}.md` (shown in the update panel).
3. `pwsh tools/release.ps1 -Version <version>` builds and tests, then writes `artifacts/release/<version>/`:
   `Aphelion.exe`, `update.json`, `update.json.sig` and `SHA256SUMS.txt`. It refuses to finish if the
   signature does not verify with the public key compiled into `Update/Updates.cs`.
4. `-Publish` additionally creates the GitHub release `v<version>` with those files. Existing copies find it
   through `https://github.com/PZW152/Aphelion/releases/latest/download/update.json`.

The private signing key lives outside the repository (`%USERPROFILE%\.aphelion\update-signing-key.pem`, or
`APHELION_SIGNING_KEY`). **Back it up.** Without it no later release can be offered as an update to existing
copies; with it, anyone can. Rotating the key means shipping a release, signed with the old key, whose program
carries the new public key.

`APHELION_UPDATE_URL=<base>` points a development build at another channel (for example a local server; plain
HTTP is accepted only for loopback addresses).

## Verification
Results from this machine (Windows 11 26300, x64, 150 % display scaling), 2026-10-20 and 2026-10-21.

### Verified

| Area | Result |
| --- | --- |
| Install + launch to a running game window | vanilla 1.21.1, vanilla 1.8.9, Fabric 1.21.1, Quilt 1.21.1, NeoForge 21.1 (1.21.1, processors), Forge 47.4 (1.20.1, processors), Forge 14.23.5 (1.12.2), Forge 10.13.4 (1.7.10, legacy installer) |
| Java | local JDK 21 picked for modern versions; Mojang `jre-legacy` (Java 8) downloaded automatically for 1.8.9/1.12.2/1.7.10 |
| Desktop app | WebView2 host renders the embedded UI; launch from the ignition button starts the game; live log console; stop from the UI; play time recorded |
| Modrinth | search, project details, install of Mod Menu resolved Placeholder API and Fabric API automatically; mod icons and metadata read from jars |
| Crash diagnosis | an invalid JVM option produced the `crash.badJvmArg` finding in the UI |
| Single file | the exe alone in an empty folder starts and renders; the loader is loaded from `runtime/webview2-loader/`; a tampered copy is detected and rewritten on the next start |
| Motion | page transitions use View Transitions in the real WebView2; orbit bodies move and reach the aphelion smoothly on selection; with 6 bodies no label collides with another, the focus or the frame over a full 40 s sway (measured every second); Reduced motion stops orbiting and skips transitions |
| Mode switching | on the real exe with an isolated standard root: standard → portable beside the program, portable → standard, and standard → portable copy in another folder with the game folder moved but Java left behind; settings stored relative, the program restarted in the new mode, the old folder's leftovers removed and unmoved items kept usable |
| Narrow windows | every page at the new 900 × 600 minimum: the rail folds to glyphs with slide-out names, the instance header keeps the name readable, settings and launch control repack instead of overlapping |
| Self-update | 0.2.0 found a locally served, really signed 0.2.1 manifest by itself 8 s after start, showed it in Settings › About, downloaded it (SHA-256 checked), handed over, was replaced in place and restarted as 0.2.1 with *Updated to 0.2.1*; the `.old` file and download folder were removed; 0.2.1 offered no further update. A manifest edited after signing was rejected with *invalid signature*. Release tooling produced and self-verified the manifest. |
| Official downloads | connection check from this PC (Taiwan) against the real hosts: all reachable; Mojang game files sampled at about 44 MB/s. Smoke test with official sources only and no local reuse, fresh data folder: vanilla 1.21.1 installed in 40 s (3,960 files, 876 MB), Fabric 1.21.1 in 2 s on top, Forge 1.20.1 in 27 s (installer processors included), NeoForge 21.1 in 22 s; each launched to a running game. Ranged download, resume, whole-file fallback, local reuse and edge racing are covered by unit tests against a local server. |
| Window chrome | drag to move, double-click to maximise/restore, maximise/close buttons, no clipping when maximised, correct size at 150 % scaling |
| Lifecycle | install (files, Start menu shortcut, Settings › Apps entry, manifest), in-place upgrade, uninstall keeping data, uninstall with `--purge`, temporary uninstaller removes itself |
| Bugs found by these runs and fixed | Fabric timestamps without a colon in the offset; embedded UI paths with backslashes; ROC-calendar dates in file names under zh-TW; DPI-unaware initial window size; start/exit event race for a JVM that dies instantly; old Forge needing `config/`; self-delete blocked by its own working directory; a damaged `javaw.exe` making Windows show a blocking 16-bit-application box; a page change waiting on a View Transition that never got a frame |

### Not verified

- Microsoft sign-in end to end: it requires an approved Azure client ID, which this build does not include (see [accounts.md](accounts.md)). The token chain is implemented to the documented protocol but has not been exercised against Microsoft's services here.
- Authlib-injector sign-in against a live server (no test account was available).
- `.mrpack` import and export with a real modpack.
- Windows 10, ARM64 and 32-bit Windows; displays other than 150 %.
- The interactive uninstall dialog (the quiet paths were tested; the dialog was not clicked through).
- Self-contained publish (`-SelfContained`).
- Download speed from mainland China networks. The techniques above help with latency, loss and bad edges, but nothing here was measured from China; the connection check exists so users there can see what their network does.
- Updating an *installed* copy end to end (the in-place install it reuses was tested on its own; the loose-copy update path was tested end to end). GitHub's release downloads are often slow or blocked in mainland China, which affects updates the same way.

## Design canvas

The visual system was designed first as a Claude Design canvas ("Aphelion Launcher — NASA-punk":
tokens, launch control, launch sequence, instances, download, settings in the light theme). The
implementation in `src/Aphelion.Web` follows it; `docs/screenshots/` shows the real application.
