# Recovery Runbook

What state this system owns, what survives which disaster, and how to get it
back. Everything here is derived from code and scripts in this repo
(`Source/BotMod/ModApi.cs`, `Source/BotMod/Config/BotConfig.cs`,
`scripts/install.sh`, `evolved/README.md`).

## State inventory

| State | Lives where | Mutable at runtime? | Survives |
|---|---|---|---|
| Operator config | `<dedi>/Mods/BotMod/Config/botmod.json` (+ `.bak`), or the file `BOTMOD_CONFIG` names, or `/mods/BotMod/Config/botmod.json` when a container deployment has it | yes: dashboard actions and console persists write it live (`ModApi.PersistConfigField`: `Enabled`, `TargetBotCount`, `Difficulty`, `BotWeapon`, `UseNeuralBrain`, `BotVsBot/Zombie/Player`, `BotTeam`, `BotTeamCount`, `TeamAssignments`) | reinstalls (install.sh preserves the mod-dir copy; a `BOTMOD_CONFIG` file lives outside it and is never touched), torn/corrupt writes (.bak fallback in `BotConfig.Load`), `make uninstall` (snapshot first). Does NOT survive instance/disk loss unless snapshots are written off-host. `make backup` snapshots whichever file is live: set `BOTMOD_CONFIG` on the backup host too, and `make restore` refuses a `BOTMOD_CONFIG` snapshot without it. The container path is snapshotted as `botmod.container.json` (override with `BOTMOD_CONTAINER_CONFIG`), and `make restore` writes it only where that mount exists, refusing a container-only snapshot on a host that lacks it. |
| Champion weights | `evolved/best.json` + `best.meta.json` | no (mod reads only; promotion is a git commit per `evolved/README.md`) | anything short of losing git remote + all clones |
| Default config template | repo `config/botmod.json`, shipped fresh on every build/install | no | git |
| Training-run artifacts | `evolved/runs/<ts>/` | written by tools/ga during training | nothing (git-ignored by design); reproducible only by re-running training (seeds are in the dir names; runs cost up to days, see docs/research REPORTs). Mitigate by promoting champions to git. |
| Scoreboard, idempotency ledger | process memory | yes | nothing (by design: restart resets scores; ledger only dedups retries inside a 10 min window) |
| Game world / player saves | dedicated server data dirs | yes | out of scope here: owned by the 7DTD dedicated server itself, not this mod |

Every admin mutation persists: `bot count`, `bot skill`, `bot weapon`,
`bot enable|disable`, `bot neural on/off`, `bot vs ...`, `bot team ...`,
`bot teams`, and the web API equivalents each write their key back to
`Config/botmod.json`. Deliberately not persisted: the live bots themselves
(respawned to `TargetBotCount` on start) and a one-off
`bot neural reload <path>` argument (only the configured
`BotNeuralWeightPath` reloads automatically).

## RPO and RTO

Recovery points and times, by disaster. The mod writes no state at runtime
except the operator config, and every persist goes through `AtomicTextFile`
(fsynced temp, `.bak` copy, rename), so the numbers below are bounded by the
host and the operator, not by the mod.

| Disaster | RPO (data lost) | RTO (time to usable) |
|---|---|---|
| Process or server restart | 0 (config is on disk, written per action) | seconds |
| Torn or corrupt config write | one mutation (`.bak` fallback) | seconds (automatic, at load) |
| `make install` / bad deploy | 0 (install.sh preserves the config) | minutes (rebuild, install) |
| `make uninstall`, `rm -rf Mods/BotMod` | 0 when a snapshot was taken (uninstall.sh takes one and refuses to delete without it); everything since the last snapshot otherwise | one `make restore SNAPSHOT=...` |
| Logical corruption of the config (hand edit, bad value) | the `.bak`, i.e. the previous mutation, unless a snapshot exists | seconds via `.bak`, minutes via snapshot |
| Host or disk loss | everything not in a snapshot: operator config and any unpromoted training run | hours, to rebuild the host: install Steam dedi + Harmony, clone, `make build && make install`, `make restore`, re-apply by hand if no snapshot exists |

## Backup and restore

`scripts/backup-state.sh` snapshots the whole recovery surface (deployed
`botmod.json` + `.bak`, or the `BOTMOD_CONFIG` file when the deployment mounts
its config elsewhere, or the container config `ModApi.PersistConfigField` also
writes, plus the champion weights) into
`$BOTMOD_STATE_BACKUP_DIR/<utc-timestamp>/`, git-ignored by default at
`backups/`. `scripts/restore-state.sh` verifies the snapshot against its
`MANIFEST` (sha256 per file, plus a check that no unlisted file is present)
before it writes anything, and defaults to verify-only. Each run takes its own
directory: a second backup inside the same second lands in
`<utc-timestamp>_2/`, never in the first one's, so a repeated `make backup`
cannot blend two states into one snapshot. `make restore` also runs that
snapshot itself before it overwrites anything, so the state a restore replaces
is still recoverable if the restore fails part way; a fresh host with no
installed mod has nothing to snapshot and says so.

```bash
make backup                                    # snapshot into ./backups/<stamp>
BOTMOD_STATE_BACKUP_DIR=/mnt/backup make backup # off-host destination
make verify-snapshot SNAPSHOT=backups/<stamp>  # verify digests, write nothing
make restore SNAPSHOT=backups/<stamp>          # verify, then restore the config
make backup-status                             # newest snapshot: verifies, and how old
```

`make backup-status` is the check to point a monitor at. The schedule itself
lives outside the repo (cron, systemd, a host backup), so nothing in the tree
can tell whether it is still running; this exits non-zero when the newest
snapshot is missing, does not verify against its own MANIFEST, or is older than
`BOTMOD_BACKUP_MAX_AGE_HOURS` (default 48). A green run means the recovery
point exists, would restore, and is recent enough for the RPO above.

Set `BOTMOD_STATE_BACKUP_DIR` off-host for protection against host loss; the
repo-local default only survives `make uninstall` and stray deletions. The
snapshot is a plain file copy, so copying the directory anywhere else (or
into a host backup) is a valid second copy.

`make uninstall` runs the backup itself and aborts if it cannot write one, so
the destructive path never runs unrecorded. `BOTMOD_SKIP_BACKUP=1` overrides
that, and then the state is gone.

## Disasters and what they cost

- **Bad deploy / reinstall** (`make install`): zero loss. install.sh stages the
  whole payload in a sibling of `Mods/BotMod`, copies the live
  `Config/botmod.json(.bak)` into the staged copy, and swaps it in with a
  single rename, so a failed copy leaves the running install untouched. The
  swap itself renames the old mod dir aside rather than deleting it and puts
  it back if anything fails, so a failed rename does not leave the server with
  no mod dir. `make install` and `make uninstall` hold
  `<dedi>/Mods/.botmod-deploy.lock` for the duration, so a reinstall and an
  uninstall cannot rewrite the same directory at once; a lock whose owning
  process is gone is taken over on the next run.
- **Torn or corrupt config file** (crash/power cut mid-persist, bad manual
  edit): at most one mutation lost. Persists go through `AtomicTextFile`
  (fsynced temp file, previous content kept at a fsynced `.bak`, then move over
  the primary), and `BotConfig.Load` recovers from `.bak` when the primary does
  not parse (logged as `BotConfig restored from backup ...`). The `.bak` is
  flushed to disk before the swap, so the last-known-good survives a power cut
  as a whole file rather than as page-cache bytes.
- **Blank or unparseable config already on the host** (a persist that failed
  after the disk filled, a hand edit): `make backup` does not snapshot the
  blank primary, it snapshots the `.bak` in its place and records the swap as
  `# fallback=<name>=<path>` in the MANIFEST, warning on stderr. A snapshot
  holding a zero-byte config is refused by `make verify-snapshot` and
  `make restore` rather than installed over a good one, so a bad recovery point
  cannot be certified by its own digests.
- **Instance/disk loss**: weights and default config come back from git;
  operator config is host-local, so it is gone unless a snapshot exists. Run
  `make backup` with `BOTMOD_STATE_BACKUP_DIR` pointed off-host, or have the
  host backup cover `Mods/BotMod/Config`.
- **Manual deletion** (`rm -rf Mods/BotMod`, `make uninstall`): `make uninstall`
  snapshots the config first and refuses to delete if that fails. A hand-typed
  `rm -rf` has no such guard; the last snapshot under `backups/` is the
  recovery path.

## Restore onto a fresh host

1. Install the Steam dedicated server and the TFP Harmony mod (build.sh probes
   both; see README Install).
2. Clone this repo, then `make build && make install` (or point
   `SEVENDTD_DS_DIR` elsewhere). This ships default config plus
   `evolved/best.json`.
3. Config: if a snapshot exists, `make verify-snapshot SNAPSHOT=<stamp>` then
   `make restore SNAPSHOT=<stamp>` (or copy `<snap>/botmod.json` to
   `Mods/BotMod/Config/botmod.json` by hand). Otherwise, if a copy of the old
   `botmod.json` exists (host backup, preserved mount, manual export), place it
   at `Mods/BotMod/Config/botmod.json` before starting. With neither, re-apply
   operator state through the dashboard or console: `bot vs ... on/off`,
   `bot team on/off`, `bot teams <n>`, `bot team assign <name> <id>`,
   `bot enable/disable` (all persisted).
4. Start the server and verify against the README Validation block
   (`[BotMod] BotMod v<current> loading...`, DM spawns line, bots alive). A
   restored-from-backup config logs `BotConfig restored from backup`.
5. Neural brain: `bot neural status` should report the loaded weight hash; if
   `UseNeuralBrain=true` was part of the old config it reloads automatically,
   else `bot neural on`.

## Drill

The snapshot path is drilled automatically: `make test-recovery`
(`scripts/test-state-recovery.sh`, also run by `make check` in CI) builds
scratch server roots, snapshots them, verifies, restores onto a second root,
and asserts the restored config is byte-identical to the original. It drives
the failure paths too, so a green run means verification can actually fail: a
tampered file, a listed file missing, an unlisted file added, a missing
MANIFEST, a snapshot with no config in it, a snapshot holding a zero-byte
config, a blank live primary falling back to its `.bak`, a `BOTMOD_CONFIG`
snapshot restored without the override, a container-only snapshot restored
onto a host without that mount, and a `make backup-status` run against a
missing, stale, or corrupt newest snapshot. It drives the deploy paths too: an
install whose swap rename fails (the previous install has to come back, with
its config), a second install refused while the deploy lock is held, a lock
left by a dead process taken over, and an uninstall refused while an install
holds it. The rest of the runbook needs the game install, so it stays
manual:

```bash
make backup
make verify-snapshot SNAPSHOT=backups/<stamp>
SEVENDTD_DS_DIR=<scratch-ds-dir> make restore SNAPSHOT=backups/<stamp>
```

Then, to prove the mod itself reads a restored config, point `SEVENDTD_DS_DIR`
at a scratch dedicated-server install, run the restore onto it, start the
server, and confirm the expected startup log lines.

## Open questions (not answerable from this repo)

- Off-host backup of the dedi host is owned by whoever operates the machine;
  nothing in this repo can protect host-local files from disk loss beyond
  whatever `BOTMOD_STATE_BACKUP_DIR` points at.
- Whether `/mods/BotMod/Config/botmod.json` is a persistent mount in your
  container deployment, and whether `/mods` is where `make restore` should put
  it back. The snapshot and restore scripts handle the path when it exists on
  the host running them (`BOTMOD_CONTAINER_CONFIG` moves it); whether that
  host is the same one that lost the data is an operator decision.
