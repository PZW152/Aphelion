# Compared with PCL2

Aphelion was written from scratch; no PCL2 source, assets or file formats were reused. This page records
what was deliberately done differently and why. Statements about PCL2 refer to its public repository
(<https://github.com/Hex-Dragon/PCL2>, `Plain Craft Launcher 2/Modules/Base/ModBase.vb` and the README) as
read on 2026-10-20.

## Structure

| PCL2 | Aphelion |
| --- | --- |
| VB.NET WPF application whose modules share many public mutable globals (`Handle`, `PathTemp`, `Lang`, `IsProgramEnding`, …) | An engine library (`Aphelion.Core`) with no UI and no global mutable state beyond the logger, injected into a thin shell; the interface is a separate project |
| UI, download and launch logic in one assembly | One-way dependencies Web → App → Core; Core is covered by 137 offline unit tests |
| Public source synced only at releases (per its README) | Single repository with a reproducible `build.ps1` |

## Files on disk

| PCL2 | Aphelion |
| --- | --- |
| INI settings in a `PCL\` folder beside the executable; temporary files in `%TEMP%\PCL\`; falls back to `C:\ProgramData\PCL\` when paths contain non-ASCII characters | One data root (standard, portable or development) split by lifecycle: `config/`, `runtime/`, `cache/`, `temp/`, `logs/` — see [lifecycle.md](lifecycle.md). Non-ASCII paths are supported everywhere (the default game folder can be `主線生存`). |
| INI key/value storage | Versioned JSON with schema migrations, atomic replace and automatic `.bak` recovery |
| WebView profile n/a | WebView2's profile is kept in `cache/webview` instead of next to the executable |

## Behaviour

| Topic | Aphelion's approach |
| --- | --- |
| Instance layout | New instances are self-contained (merged version JSON + own client jar); folders made by other launchers are still read as-is, `inheritsFrom` included. |
| Downloads | Every file verified by SHA-1 before it is moved into place; official sources first with automatic BMCLAPI fallback in Auto mode. |
| Mod loaders | Forge and NeoForge are installed by running the installer's own processors (no GUI), with output verification. OptiFine and LiteLoader are not offered (OptiFine has no official download API; LiteLoader is discontinued). |
| Content source | Modrinth only. CurseForge requires a private API key that cannot be shipped in an open client. |
| Accounts | Microsoft sign-in needs your own Azure client ID; Aphelion does not reuse another launcher's ID. Tokens encrypted with DPAPI. |
| Deletion | Instances and mods go to the Recycle Bin. |
| Uninstall | Manifest-driven; keeps data unless full cleanup is chosen; reports anything it could not remove. |
| Homepage | No custom XAML homepage: arbitrary markup executed by the launcher is an attack surface, and the launch screen is the home page. |
| Online multiplayer (联机) | Not included: it depends on third-party relay services. |
