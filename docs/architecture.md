# Architecture

```
src/
  Aphelion.Core/     launcher engine — net10.0, no UI, no third-party packages
  Aphelion.App/      Windows shell — WinForms window + WebView2, RPC bridge, DPAPI, installer
  Aphelion.Web/      interface — Svelte 5 + TypeScript, bundled by Vite and embedded in the exe
tests/
  Aphelion.Core.Tests/   xUnit, offline, deterministic
tools/
  Aphelion.Smoke/    end-to-end install + launch against the real services (not shipped)
  make-icon.ps1      renders the application icon
```

Dependencies point one way: `Web → (bridge) → App → Core`. Core never references App or Windows UI.

## Technology choices

| Choice | Why |
| --- | --- |
| **.NET 10 (C#)** for the engine | Mature HTTP, JSON, ZIP, hashing, process and P/Invoke support in the base library, so the engine has zero third-party dependencies. Long-term-support runtime. |
| **WebView2 + Svelte** for the interface | The console design (variable-width type, grids, SVG orbit map, layered surfaces) is far cheaper to build and keep consistent in CSS than in XAML. WebView2 ships with Windows 11 and is shared system-wide, so the app stays small: one 2.4 MB executable. Svelte compiles to plain DOM code (~100 KB gzip) with no runtime framework. |
| **WinForms host** | The host only owns a window and a WebView2 control; WinForms is the lightest way to do that. The native caption is removed with `WM_NCCALCSIZE` while keeping the frame, so snapping, resizing and shadows stay native. |
| **One file** | `WebView2Loader.dll` is native and Windows only loads native DLLs from disk, so it is embedded in the exe, written to `runtime/webview2-loader/<sha256>/` inside the data root (never `%TEMP%`), verified against the embedded bytes before each load, and passed to WebView2 with `SetLoaderDllFolderPath`. Older copies are removed at startup. |
| **No installer framework** | The executable installs, upgrades and uninstalls itself per user (see [lifecycle.md](lifecycle.md)), so there is no WiX/Inno toolchain and nothing needs administrator rights. |

Trade-off accepted: WebView2 uses more memory than a native XAML UI (tens of MB). The launcher is idle while the game runs, so this does not compete with the game.

## Core modules

| Namespace | Responsibility |
| --- | --- |
| `Storage` | `AppPaths` (one data root, split by lifecycle), `AtomicFile` (temp-write + replace + `.bak`), JSON options |
| `Settings` | Versioned `settings.json` with sequential migrations and value normalisation |
| `Net` | Shared `HttpClient`, BMCLAPI mirror mapping, parallel verified `Downloader` (`.part` files, SHA-1, stall timeout, candidate fallback) |
| `Minecraft` | Version JSON model, rule evaluation, `inheritsFrom` flattening, library/native resolution, asset handling, path-traversal guards |
| `Install` | Self-contained instance creation (staged then moved into place), repair, loader catalogues, Forge/NeoForge installer (modern processors and legacy universal jar) |
| `Java` | Discovery (registry, known folders, PATH, official launcher runtimes), selection per game version, Mojang runtime download |
| `Accounts` | Offline UUIDs, Microsoft device-code → Xbox → XSTS → Minecraft, Yggdrasil/authlib-injector, encrypted store |
| `Launch` | Argument building (pure, unit-tested), natives extraction, process session with log4j XML parsing, memory sizing |
| `Diagnostics` | Crash analysis rules over game output, crash reports, `hs_err` logs |
| `Content` | Modrinth client, jar/pack metadata, install with dependencies, update checks, `.mrpack` import/export, world list |
| `Tasks` | Background operations with progress, cancellation and stable error codes |

### Instances are self-contained

A new instance's version JSON is the vanilla definition merged with the loader's, and its client jar sits in
its own folder (`versions/<id>/<id>.jar`). Deleting an instance therefore never leaves an orphaned parent
version behind; libraries and assets stay shared in the game root. Instances created by other launchers
(which use `inheritsFrom`) are read and launched as they are.

## The UI bridge

The page talks to the host through `chrome.webview.postMessage` with a small JSON-RPC protocol
(`Aphelion.App/Bridge/RpcRouter.cs`, `Aphelion.Web/src/lib/rpc.ts`):

- request `{"id":1,"method":"instances.list","params":{}}` → `{"id":1,"result":[…]}` or `{"id":1,"error":{"code","message","args"}}`
- events `{"event":"tasks.changed","data":[…]}` — task progress is coalesced to ~8 updates per second, game logs are batched every 100 ms.

Errors carry a stable `code` that the UI translates (zh-Hant, zh-Hans, en); the English `message` is only a fallback.

Security of the page:
- served from embedded resources at `https://app.aphelion.internal/` with a strict Content-Security-Policy (no remote scripts);
- messages are accepted only from that origin; navigation away is cancelled and http(s) links open in the default browser;
- third-party Markdown (Modrinth descriptions) is sanitised with DOMPurify;
- browser accelerators, default context menus, autofill and DevTools (unless `APHELION_DEVTOOLS=1`) are disabled.

## Design system

The interface implements the canvas "Aphelion Launcher — NASA-punk" (Claude Design). Tokens live in
`src/Aphelion.Web/src/app.css`:

- Surfaces: panel `#1A1F22`, screen `#0D1113`, rules `#2C3439`; light "Manual" theme `#E4E7E4` / `#F3F5F3`.
- One accent (International Orange `#FF5B14`, user-selectable) reserved for actions; lamp colours (nominal, caution, warning) only report state.
- One type family, Archivo, with the width axis as hierarchy (125% identity, 112% titles, 100% reading, 75% panel legends); JetBrains Mono only for logs.
- Zero radius; hierarchy comes from surfaces and 1 px rules, not shadows.

### Motion

All timing and curves live in `src/lib/motion.ts` and the `--ease-*` / `--t-*` tokens in `app.css`. Movement
always explains a state change, in the console's vocabulary:

| Moment | Motion |
| --- | --- |
| Start-up | the console powers on once: rail rows cascade in, annunciator lamps flicker on one by one, the main screen rises |
| Page change | View Transition: the old page lifts away, the new one settles in |
| Orbit map | orbits trace themselves; bodies sway in unison along their orbits; choosing an instance moves every body smoothly to its new orbit, the target reticle pings and its ticks rotate |
| Selection | the identity plate flips to the new name, readings cascade in |
| Launch | finished stages fill green, the active stage carries a scanning light; the ignition guard's stripes march when armed; a running game pulses its lamp; the logo's craft orbits while anything is active |
| Lists | rows rise in with a short stagger, removed rows slide out, the rest close the gap (FLIP); skeleton rows while loading |
| Controls | tab bars and the rail use one gliding indicator; switches have a sprung knob; primary buttons sweep once when armed |
| Dialogs | open like a hatch from the centre line and close in reverse |
| Toasts | slide in with a countdown line that pauses on hover |
| Theme | the new theme opens as a circle from the pressed control |

Settings › Appearance › Motion offers *Follow system* (Windows "Animation effects"), *Full* and *Reduced*;
Reduced removes transitions, orbiting and flicker while keeping state changes instant and visible.
