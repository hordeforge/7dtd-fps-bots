# Security Policy

## Supported versions

Only the latest release is supported: **0.8.0** (canonical constant in
`Source/BotMod/Core/BotModVersion.cs`, mirrored by `Source/BotMod/ModInfo.xml`;
older releases receive no fixes).

## Deployment reality checks

- Code mods require EAC disabled (`EACEnabled=false`); client authentication
  then rests on Steam/EOS alone. See README, Install.
- `AllowSyntheticAuthBypass` (default `false`) lets clients with synthetic
  Steam ids 76561199000000000..10000 join without Steam authentication while
  enabled. Enabling it on any server reachable by untrusted networks means
  those ids are accepted without proof of game ownership
  (`Source/BotMod/Patches/BotPatches.cs:21-22,29,39`). The startup log line
  reports the flag state (`AuthBypass=True|False`,
  `Source/BotMod/ModApi.cs:52`). A bypassed join is logged by in-world entity id
  only, not by Steam id or client IP (`BotPatches.cs:42-47`), so the log
  correlates the join to a session rather than to a network address.
- The admin web API (`GET/POST /api/bot`) performs no authentication of its
  own; it relies entirely on the dedicated server's stock webserver
  authentication and permission level 0
  (`Source/BotMod/Web/WebApi.cs:502`, `DefaultMethodPermissionLevels`). It also
  applies no rate limit or quota of its own; only per-request range clamps.
  Keep webtokens/webpermissions hardened.
- The `bot` console/telnet command has the same reach as the web API and no
  mod-side authorization: it never inspects the sender to decide whether to
  run (`Source/BotMod/Commands/BotConsoleCommands.cs:111`) and relies on the
  game's default console permission level. It does audit: every subcommand
  outside the read-only set logs one issuer-attributed line before dispatch
  (`BotConsoleCommands.cs:111-123`), naming a telnet client by entity id and
  the server console and RCON as `local`.
- The `BOTMOD_CONFIG` environment variable redirects where the config is read
  from and written to (`Source/BotMod/Config/BotConfig.cs:556-580`). Anyone who
  can set the server process's environment can point the mod at a different
  config file.

## Reporting

No private disclosure contact or process is defined in this repository.
The current attack surface and known gaps are catalogued in
`docs/THREAT_MODEL.md`.
