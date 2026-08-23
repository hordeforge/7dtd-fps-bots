# Security Policy

## Supported versions

Only the latest release is supported: **0.4.0** (canonical constant in
`Source/BotMod/Core/BotModVersion.cs`, mirrored by `Source/BotMod/ModInfo.xml`;
older releases receive no fixes).

## Deployment reality checks

- Code mods require EAC disabled (`EACEnabled=false`); client authentication
  then rests on Steam/EOS alone. See README, Install.
- `AllowSyntheticAuthBypass` (default `false`) lets clients with synthetic
  Steam ids 76561199000000000..10000 join without Steam authentication while
  enabled. Enabling it on any server reachable by untrusted networks means
  those ids are accepted without proof of game ownership
  (`Source/BotMod/Patches/BotPatches.cs`). The startup log line reports the
  flag state (`AuthBypass=True|False`).
- The admin web API (`GET/POST /api/bot`) performs no authentication of its
  own; it relies entirely on the dedicated server's stock webserver
  authentication and permission level 0
  (`Source/BotMod/Web/WebApi.cs:298`). Keep webtokens/webpermissions hardened.

## Reporting

No private disclosure contact or process is defined in this repository.
The current attack surface and known gaps are catalogued in
`docs/THREAT_MODEL.md`.
