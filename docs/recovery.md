# Recovery Runbook

What state this system owns, what survives which disaster, and how to get it
back. Everything here is derived from code and scripts in this repo
(`Source/BotMod/ModApi.cs`, `Source/BotMod/Config/BotConfig.cs`,
`scripts/install.sh`, `evolved/README.md`).

## State inventory

| State | Lives where | Mutable at runtime? | Survives |
|---|---|---|---|
| Operator config | `<dedi>/Mods/BotMod/Config/botmod.json` (+ `.bak`) | yes: dashboard actions and console persists write it live (`ModApi.PersistConfigField`: `Enabled`, `BotVsBot/Zombie/Player`, `BotTeam`, `BotTeamCount`, `TeamAssignments`) | reinstalls (install.sh preserves it), torn/corrupt writes (.bak fallback in `BotConfig.Load`). Does NOT survive instance/disk loss or `make uninstall`. |
| Champion weights | `evolved/best.json` + `best.meta.json` | no (mod reads only; promotion is a git commit per `evolved/README.md`) | anything short of losing git remote + all clones |
| Default config template | repo `config/botmod.json`, shipped fresh on every build/install | no | git |
| Training-run artifacts | `evolved/runs/<ts>/` | written by tools/ga during training | nothing (git-ignored by design); reproducible only by re-running training (seeds are in the dir names; runs cost up to days, see docs/research REPORTs). Mitigate by promoting champions to git. |
| Scoreboard, idempotency ledger | process memory | yes | nothing (by design: restart resets scores; ledger only dedups retries inside a 10 min window) |
| Game world / player saves | dedicated server data dirs | yes | out of scope here: owned by the 7DTD dedicated server itself, not this mod |

Session-only knobs are acknowledged but deliberately not persisted and reset on
restart: `bot count`, `bot weapon`, and `skill` (console and web API set
Difficulty in memory only). Re-apply after restart or edit `botmod.json`
directly and `bot reload`.

## Disasters and what they cost

- **Bad deploy / reinstall** (`make install`): zero loss. install.sh stages
  `Config/botmod.json(.bak)` out before replacing the mod dir and puts it back.
- **Torn or corrupt config file** (crash/power cut mid-persist, bad manual
  edit): at most one mutation lost. Persists go through `AtomicTextFile`
  (fsynced temp file, previous content kept at `.bak`, then move over the
  primary), and `BotConfig.Load` recovers from `.bak` when the primary does not
  parse (logged as `BotConfig restored from backup ...`).
- **Instance/disk loss**: weights and default config come back from git;
  operator config is host-local, so it is gone unless you have a host-level
  backup or copied the file out. This repo schedules no off-host backup.
- **Manual deletion** (`rm -rf Mods/BotMod`, `make uninstall`): intentional
  destruction; copy `Mods/BotMod/Config/botmod.json` out first if you want it.

## Restore onto a fresh host

1. Install the Steam dedicated server and the TFP Harmony mod (build.sh probes
   both; see README Install).
2. Clone this repo, then `make build && make install` (or point
   `SEVENDTD_DS_DIR` elsewhere). This ships default config plus
   `evolved/best.json`.
3. Config: if a copy of the old `botmod.json` exists (host backup, preserved
   mount, manual export), place it at `Mods/BotMod/Config/botmod.json` before
   starting. Otherwise re-apply operator state through the dashboard or
   console: `bot vs ... on/off`, `bot team on/off`, `bot teams <n>`,
   `bot team assign <name> <id>`, `bot enable/disable` (all persisted).
4. Start the server and verify against the README Validation block
   (`[BotMod] BotMod v0.4.0 loading...`, DM spawns line, bots alive). A
   restored-from-backup config logs `BotConfig restored from backup`.
5. Neural brain: `bot neural status` should report the loaded weight hash; if
   `UseNeuralBrain=true` was part of the old config it reloads automatically,
   else `bot neural on`.

## Drill

Prove the above without touching production: point `SEVENDTD_DS_DIR` at a
scratch dedicated-server install, run steps 2-5 there once after any change to
config handling, and confirm the expected startup log lines.

## Open questions (not answerable from this repo)

- Is `/mods/BotMod/Config/botmod.json` a separate host mount in your container
  deployment? PersistConfigField also writes there when present; if mounted,
  operator config survives even a wiped image layer.
- Off-host backup of the dedi host is owned by whoever operates the machine;
  nothing in this repo can protect host-local files from disk loss.
