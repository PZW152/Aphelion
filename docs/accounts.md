# Accounts

## Offline

A name of 3–16 letters, digits or underscores. The UUID is derived exactly like the vanilla server does
(`UUID v3` of `OfflinePlayer:<name>`), so worlds and servers in offline mode recognise the same player.

## Microsoft

Sign-in uses the OAuth 2.0 **device code** flow: Aphelion shows a short code, opens
`https://microsoft.com/link` (or the URI Microsoft returns) in your browser, and polls until you finish.
Afterwards it exchanges tokens with Xbox Live, XSTS and Minecraft services and reads your profile.
The Microsoft refresh token and the Minecraft access token are stored DPAPI-encrypted in
`config/accounts.json`; the access token is refreshed automatically before launches.

### The Azure client ID

Microsoft requires every launcher to identify itself with its own Azure application ID, and Aphelion does
not borrow another launcher's ID. To enable Microsoft sign-in:

1. Sign in to <https://portal.azure.com> → *Microsoft Entra ID* → *App registrations* → *New registration*.
2. Supported account types: **Personal Microsoft accounts only** (or "any organizational directory and personal accounts").
3. After creating it, open *Authentication* → *Advanced settings* → set **Allow public client flows** to *Yes*.
4. Copy the *Application (client) ID* into Aphelion › Settings › Accounts.

Since 2023 Mojang additionally requires new application IDs to be approved before they may call the
Minecraft services API (request it at <https://aka.ms/mce-reviewappid>). Until approval,
`login_with_xbox` returns HTTP 403 and Aphelion reports the error from the service.

Error messages for common Xbox Live problems (no Xbox profile, child account, unsupported region) are
translated into concrete next steps.

## Authlib-injector servers (外置登入)

Enter the server address (for example `littleskin.cn`); Aphelion follows the API Location Indication
header, reads the server metadata and authenticates with your e-mail and password. The password is sent
only to that server and never stored. If the account owns several characters, you pick one.

At launch Aphelion downloads the latest authlib-injector (checked against its published SHA-256),
validates or refreshes the token, and starts the game with
`-javaagent:authlib-injector.jar=<server>` plus the prefetched metadata.

## When credentials stop working

If a token is revoked, expired beyond refresh, or cannot be decrypted (for example after copying a
portable folder to another PC), the account is marked *Sign in again* instead of failing silently; the
rest of the account entry is kept.
