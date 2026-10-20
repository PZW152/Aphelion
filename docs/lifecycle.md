# Data, installation and removal

## Where files live

Exactly one **data root** holds everything Aphelion creates:

| Mode | Data root | Chosen when |
| --- | --- | --- |
| Standard | `%LOCALAPPDATA%\Aphelion` | default |
| Portable | `<exe folder>\Data` | a file named `Aphelion.portable` exists next to the executable |
| Development | `%APHELION_HOME%` | the environment variable is set |

Inside the root, folders are split by lifecycle:

| Folder | Contents | Kept by a normal uninstall | Full cleanup |
| --- | --- | --- | --- |
| `config/` | `settings.json`, `accounts.json` (tokens DPAPI-encrypted) | yes | deleted |
| `minecraft/` | built-in game folder (instances, worlds, mods) | yes | deleted only if the extra box is ticked |
| `runtime/` | Java runtimes Aphelion downloaded, authlib-injector | yes | deleted |
| `cache/` | version manifest cache, WebView2 profile | yes | deleted |
| `temp/` | per-launch natives, installer scratch | cleaned at every start | deleted |
| `logs/` | launcher logs, newest 10 kept, secrets redacted | yes | deleted |

Game folders you add yourself (for example an existing `D:\Games\.minecraft`) are referenced from
`settings.json` but **never** deleted by anything — removing one from the list only forgets it.

Inside a game folder Aphelion writes only standard launcher files plus one file per instance it manages:
`versions/<id>/aphelion.instance.json` (display name, colour, overrides, play time).

The program writes nothing to the registry except the uninstall entry of an installed copy, creates no
services or startup items, and never asks for administrator rights.

## Crash safety

- Settings, accounts and instance metadata are written to a temp file and swapped in (`File.Replace`); the previous version is kept as `.bak` and used automatically if the main file is unreadable (the damaged file is preserved as `.corrupt-<time>`).
- Downloads land in `.part` files and are moved into place only after the size and SHA-1 match.
- New instances are assembled in `versions/.aphelion-staging-*` and moved into place when complete; leftovers from an interrupted install are removed at the next start.
- Java runtimes are staged in `runtime/.<component>.staging` and swapped in.

## Settings migrations

`settings.json` carries a `schema` number. Older files are backed up as `settings.json.v<n>.bak`, then
upgraded one schema step at a time (`SettingsStore.Migrate`). Files from a newer Aphelion are read
without being rewritten silently.

## Install, upgrade, uninstall

| Action | How | What happens |
| --- | --- | --- |
| Run without installing | start `Aphelion.exe` anywhere | works immediately; data in the standard root |
| Install | Settings › Data and storage › *Install on this PC*, or `Aphelion.exe --install` | copies the program files to `%LOCALAPPDATA%\Programs\Aphelion` (staged, then swapped), creates a Start menu shortcut, registers `HKCU\…\Uninstall\Aphelion_<AppId>`, writes `install-manifest.json` listing every created item |
| Upgrade | run `--install` from a newer build | same as install; the existing program folder is replaced in place, data untouched |
| Uninstall | Windows Settings › Apps, Settings › *Uninstall…*, or `--uninstall` | a temporary copy of the executable waits for Aphelion to exit, asks *keep data* vs *full cleanup*, removes exactly the items in the manifest, reports anything it could not remove, then deletes itself |

The application identity (`AppInfo.AppId`) never changes, so upgrades always find the existing installation.

## Portable mode

Create an empty `Aphelion.portable` beside the executable. All data then goes to the `Data` folder next to
it, and the whole folder can be moved or copied. Microsoft and Yggdrasil tokens are encrypted for the
current Windows user, so on another machine those accounts ask to sign in again; nothing else is lost.
Portable copies are not registered with Windows and are removed by deleting their folder.
