#!/usr/bin/env bash
# Prove the backup/restore path, not just that the scripts parse.
#
# A snapshot nobody has ever restored is a hypothesis. This suite runs the
# whole round trip against scratch trees: backup from a fake server root,
# verify the digests, restore onto a second fake root, and confirm the file
# that comes back is byte-identical to the one that went in. It also drives
# the failure paths that make verification meaningful: a tampered file, a
# missing MANIFEST, an unlisted file, an empty backup, a zero-byte config, a
# restore whose mounted-config target does not exist on this host, and a
# backup schedule that stopped running.
#
# No game install and no network: every path is a temp tree, so this runs in
# CI as part of `make check`.
#
# Usage: bash scripts/test-state-recovery.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BACKUP="$ROOT/scripts/backup-state.sh"
RESTORE="$ROOT/scripts/restore-state.sh"
STATUS="$ROOT/scripts/backup-status.sh"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0
case_name=""

# check <name>: start a case, echoing its name on first use.
check() {
  if [[ "$case_name" != "$1" ]]; then
    case_name="$1"
    echo "==> $case_name"
  fi
}

ok() { echo "ok - $1"; }
fail() { echo "FAIL - $1" >&2; failures=$((failures + 1)); }

# expect_ok <label> <command...>: the command must succeed.
expect_ok() {
  local label="$1"; shift
  if "$@" > "$work/out" 2>&1; then ok "$label"; else fail "$label (command failed: $*)"; sed 's/^/    /' "$work/out" >&2; fi
}

# expect_fail <label> <command...>: the command must fail, and say why.
expect_fail() {
  local label="$1"; shift
  if "$@" > "$work/out" 2>&1; then
    fail "$label (command succeeded but had to fail: $*)"
  else
    ok "$label"
  fi
}

# fresh_server <name>: a scratch dedicated-server root with a deployed mod dir
# whose operator config holds one persisted mutation.
fresh_server() {
  local dir="$work/$1"
  mkdir -p "$dir/Mods/BotMod/Config"
  cat > "$dir/Mods/BotMod/Config/botmod.json" <<'JSON'
{
  "TargetBotCount": 24,
  "UseNeuralBrain": true
}
JSON
  printf '{"TargetBotCount": 20}\n' > "$dir/Mods/BotMod/Config/botmod.json.bak"
  printf '%s' "$dir"
}

# The snapshot directory backup-state.sh just created under $1. Timestamps are
# UTC to the second and sort lexicographically, so the last one is the newest.
newest_snapshot() { find "$1" -mindepth 1 -maxdepth 1 -type d | sort | tail -1; }

check "backup covers the deployed config, its .bak, and the champion weights"
src="$(fresh_server src)"
SEVENDTD_DS_DIR="$src" BOTMOD_STATE_BACKUP_DIR="$work/snapshots" \
  BOTMOD_CONTAINER_CONFIG="$work/absent" bash "$BACKUP" > /dev/null
snap="$(newest_snapshot "$work/snapshots")"
for f in botmod.json botmod.json.bak evolved/best.json MANIFEST; do
  if [[ -f "$snap/$f" ]]; then ok "$f present"; else fail "$f missing from snapshot"; fi
done

check "verify passes on an intact snapshot and writes nothing"
expect_ok "verify intact" \
  env SEVENDTD_DS_DIR="$src" bash "$RESTORE" "$snap"
if grep -q "Dry run" "$work/out"; then ok "dry run reported"; else fail "verify was not a dry run"; fi
expect_ok "dry run left the source config alone" \
  env SEVENDTD_DS_DIR="$src" bash "$RESTORE" "$snap" > /dev/null
if [[ -f "$src/Mods/BotMod/Config/botmod.json" ]]; then ok "source config still there"; else fail "verify mutated the source config"; fi

check "restore onto a fresh server root reproduces the config byte for byte"
dst="$(fresh_server dst)"
rm -f "$dst/Mods/BotMod/Config/botmod.json" "$dst/Mods/BotMod/Config/botmod.json.bak"
rm -rf "$dst/Mods/BotMod"
expect_ok "restore --apply" \
  env SEVENDTD_DS_DIR="$dst" BOTMOD_CONTAINER_CONFIG="$work/absent" \
  BOTMOD_STATE_BACKUP_DIR="$work/pre-restore" bash "$RESTORE" "$snap" --apply
restored="$dst/Mods/BotMod/Config/botmod.json"
if [[ -f "$restored" ]] && cmp -s "$src/Mods/BotMod/Config/botmod.json" "$restored"; then
  ok "restored config matches the source"
else
  fail "restored config differs from the source"
fi
if [[ -f "$dst/Mods/BotMod/Config/botmod.json.bak" ]]; then ok ".bak restored too"; else fail ".bak not restored"; fi

check "verification fails on a tampered file"
tampered="$work/tampered"
cp -r "$snap" "$tampered"
printf 'corrupted\n' >> "$tampered/botmod.json"
expect_fail "tampered config rejected" \
  env SEVENDTD_DS_DIR="$src" bash "$RESTORE" "$tampered"
if grep -q "CORRUPT" "$work/out"; then ok "CORRUPT reported"; else fail "tampered snapshot was not reported as corrupt"; fi

check "verification fails on a file missing from the snapshot"
missing="$work/missing-file"
cp -r "$snap" "$missing"
rm -f "$missing/botmod.json"
expect_fail "absent file rejected" \
  env SEVENDTD_DS_DIR="$src" bash "$RESTORE" "$missing"
if grep -q "MISSING" "$work/out"; then ok "MISSING reported"; else fail "truncated snapshot was not reported as missing"; fi

check "verification fails on an unlisted file, so a snapshot cannot grow unnoticed"
extra="$work/unlisted"
cp -r "$snap" "$extra"
printf 'stray\n' > "$extra/surprise.json"
expect_fail "unlisted file rejected" \
  env SEVENDTD_DS_DIR="$src" bash "$RESTORE" "$extra"

check "a snapshot with no MANIFEST is refused"
noManifest="$work/no-manifest"
cp -r "$snap" "$noManifest"
rm -f "$noManifest/MANIFEST"
expect_fail "MANIFEST-less snapshot rejected" \
  env SEVENDTD_DS_DIR="$src" bash "$RESTORE" "$noManifest"

check "a server root with no config still snapshots the champion weights and names them"
empty="$work/empty-server"
mkdir -p "$empty/Mods/BotMod"
expect_ok "backup succeeds on champion weights alone" \
  env SEVENDTD_DS_DIR="$empty" BOTMOD_STATE_BACKUP_DIR="$work/snapshots-empty" \
  BOTMOD_CONTAINER_CONFIG="$work/absent" bash "$BACKUP"
esnap="$(newest_snapshot "$work/snapshots-empty")"
if [[ -f "$esnap/evolved/best.json" ]] && [[ ! -f "$esnap/botmod.json" ]]; then
  ok "weights-only snapshot"
else
  fail "weights-only snapshot is not what landed in $esnap"
fi
expect_ok "verify weights-only snapshot" \
  env SEVENDTD_DS_DIR="$empty" bash "$RESTORE" "$esnap"
expect_fail "restore of a snapshot with no config is refused, not reported as done" \
  env SEVENDTD_DS_DIR="$empty" BOTMOD_STATE_BACKUP_DIR="$work/pre-restore-noconfig" \
  bash "$RESTORE" "$esnap" --apply

check "the container config the mod writes outside the server root is backed up and restored"
container="$(fresh_server container)/../container-config"
mkdir -p "$container"
printf '{"TargetBotCount": 42}\n' > "$container/botmod.json"
cont="$(fresh_server cont)"
SEVENDTD_DS_DIR="$cont" BOTMOD_STATE_BACKUP_DIR="$work/snapshots-container" \
  BOTMOD_CONTAINER_CONFIG="$container" bash "$BACKUP" > /dev/null
csnap="$(newest_snapshot "$work/snapshots-container")"
if [[ -f "$csnap/botmod.container.json" ]]; then
  ok "container config present in the snapshot"
else
  fail "container config missing from the snapshot"
fi
if grep -q "^# container-config=" "$csnap/MANIFEST"; then ok "snapshot records the container path"; else fail "container path not recorded in MANIFEST"; fi
cont_dst="$work/container-restore-host"
mkdir -p "$cont_dst"
expect_ok "restore onto the host that mounts it" \
  env SEVENDTD_DS_DIR="$cont_dst" BOTMOD_CONTAINER_CONFIG="$container" \
  BOTMOD_STATE_BACKUP_DIR="$work/pre-restore-container" bash "$RESTORE" "$csnap" --apply
if [[ -f "$container/botmod.json" ]]; then ok "container config still readable after restore"; else fail "restore removed the container config"; fi

check "restore --apply snapshots the live config before overwriting it"
# The host that will be overwritten still holds a config the operator set
# after the snapshot above was taken. Restoring over it without a copy is how
# a half-finished restore turns into lost operator state.
overwrite_target="$(fresh_server overwrite)"
printf '{"TargetBotCount": 77}\n' > "$overwrite_target/Mods/BotMod/Config/botmod.json"
expect_ok "restore onto a host that already has a config" \
  env SEVENDTD_DS_DIR="$overwrite_target" BOTMOD_CONTAINER_CONFIG="$work/absent" \
  BOTMOD_STATE_BACKUP_DIR="$work/pre-restore-live" bash "$RESTORE" "$snap" --apply
if grep -q "snapshotted before the restore" "$work/out"; then
  ok "pre-restore snapshot announced"
else
  fail "restore said nothing about snapshotting the state it overwrites"
fi
pre="$(newest_snapshot "$work/pre-restore-live")"
if [[ -n "$pre" ]] && grep -q '"TargetBotCount": 77' "$pre/botmod.json"; then
  ok "the overwritten config is recoverable from the pre-restore snapshot"
else
  fail "no pre-restore snapshot holding the live config under $work/pre-restore-live"
fi
if grep -q '"TargetBotCount": 24' "$overwrite_target/Mods/BotMod/Config/botmod.json"; then
  ok "the snapshot's config was restored over it"
else
  fail "the restore did not take effect on $overwrite_target"
fi

check "a container-only snapshot on a host without that mount is refused, not silently dropped"
# A server root with no mod-dir config plus a container config: the container
# copy is then the only operator state in the snapshot, so losing the mount
# means losing everything, which has to be a refusal.
conly_server="$work/container-only-server"
mkdir -p "$conly_server/Mods/BotMod"
conly_container="$work/container-only-config"
mkdir -p "$conly_container"
printf '{"TargetBotCount": 42}\n' > "$conly_container/botmod.json"
SEVENDTD_DS_DIR="$conly_server" BOTMOD_STATE_BACKUP_DIR="$work/snapshots-container-only" \
  BOTMOD_CONTAINER_CONFIG="$conly_container" bash "$BACKUP" > /dev/null
conly_snap="$(newest_snapshot "$work/snapshots-container-only")"
if [[ -f "$conly_snap/botmod.container.json" && ! -f "$conly_snap/botmod.json" ]]; then
  ok "container-only snapshot"
else
  fail "expected a container-only snapshot at $conly_snap"
fi
hostless="$work/hostless"
mkdir -p "$hostless"
expect_fail "container-only restore refused" \
  env SEVENDTD_DS_DIR="$hostless" BOTMOD_CONTAINER_CONFIG="$work/absent" \
  BOTMOD_STATE_BACKUP_DIR="$work/pre-restore-container-only" bash "$RESTORE" "$conly_snap" --apply
if [[ -f "$hostless/Mods" ]]; then fail "refused restore still wrote into the host"; else ok "refused restore wrote nothing"; fi

check "a BOTMOD_CONFIG snapshot is refused without the override, since the server would ignore it"
mounted="$(fresh_server mounted)/../mounted-config.json"
printf '{"TargetBotCount": 7}\n' > "$mounted"
msnap_src="$(fresh_server mounted)"
SEVENDTD_DS_DIR="$msnap_src" BOTMOD_STATE_BACKUP_DIR="$work/snapshots-mounted" \
  BOTMOD_CONFIG="$mounted" bash "$BACKUP" > /dev/null
msnap="$(newest_snapshot "$work/snapshots-mounted")"
if [[ -f "$msnap/botmod.config-path.json" ]]; then ok "mounted config in the snapshot"; else fail "mounted config missing from the snapshot"; fi
expect_fail "restore without BOTMOD_CONFIG refused" \
  env SEVENDTD_DS_DIR="$msnap_src" BOTMOD_STATE_BACKUP_DIR="$work/pre-restore-mounted" \
  bash "$RESTORE" "$msnap" --apply
expect_ok "restore with BOTMOD_CONFIG" \
  env SEVENDTD_DS_DIR="$msnap_src" BOTMOD_CONFIG="$mounted" \
  BOTMOD_STATE_BACKUP_DIR="$work/pre-restore-mounted" bash "$RESTORE" "$msnap" --apply
if [[ -f "$mounted" ]]; then ok "mounted config still in place"; else fail "mounted config disappeared"; fi

check "a blank live config falls back to the .bak, so the snapshot is not the one unrestorable state"
blank_src="$work/blank-server"
mkdir -p "$blank_src/Mods/BotMod/Config"
: > "$blank_src/Mods/BotMod/Config/botmod.json"
printf '{"TargetBotCount": 31}\n' > "$blank_src/Mods/BotMod/Config/botmod.json.bak"
SEVENDTD_DS_DIR="$blank_src" BOTMOD_STATE_BACKUP_DIR="$work/snapshots-blank" \
  BOTMOD_CONTAINER_CONFIG="$work/absent" bash "$BACKUP" > "$work/out" 2>&1
bsnap="$(newest_snapshot "$work/snapshots-blank")"
if [[ -s "$bsnap/botmod.json" ]] && grep -q '"TargetBotCount": 31' "$bsnap/botmod.json"; then
  ok "snapshot holds the last-known-good, not the empty primary"
else
  fail "blank primary was snapshotted as-is; restore would install an unparseable config"
  sed 's/^/    /' "$work/out" >&2
fi
if grep -q "^# fallback=botmod.json=" "$bsnap/MANIFEST"; then
  ok "MANIFEST records the substituted copy"
else
  fail "MANIFEST does not record that the primary was substituted"
fi
expect_ok "verify passes on the substituted snapshot" \
  env SEVENDTD_DS_DIR="$blank_src" bash "$RESTORE" "$bsnap"

check "a snapshot holding a zero-byte config is refused, not installed over a good one"
emptycfg="$work/empty-config-snapshot"
cp -r "$bsnap" "$emptycfg"
: > "$emptycfg/botmod.json"
(
  cd "$emptycfg"
  source "$ROOT/scripts/digest.sh"
  find . -type f ! -name MANIFEST | sed 's|^\./||' | sort | while IFS= read -r f; do
    "${SHA[@]}" "$f"
  done > "$work/manifest.blank"
)
mv "$work/manifest.blank" "$emptycfg/MANIFEST"
emptycfg_host="$work/empty-config-host"
mkdir -p "$emptycfg_host"
expect_fail "zero-byte config snapshot rejected" \
  env SEVENDTD_DS_DIR="$emptycfg_host" bash "$RESTORE" "$emptycfg" --apply
if grep -q "EMPTY" "$work/out"; then ok "EMPTY reported"; else fail "the zero-byte config was not named"; fi
if [[ -e "$emptycfg_host/Mods" ]]; then fail "refused restore still wrote into the host"; else ok "refused restore wrote nothing"; fi

check "backup-status reports a stopped schedule instead of a stale snapshot reading as healthy"
expect_fail "no backup directory is not healthy" \
  env BOTMOD_STATE_BACKUP_DIR="$work/never-created" bash "$STATUS"
expect_ok "a fresh snapshot is healthy" \
  env BOTMOD_STATE_BACKUP_DIR="$work/snapshots" bash "$STATUS"
# touch -t is POSIX and takes a fixed stamp, so this does not depend on the
# host date syntax; the age is read from the snapshot directory, so moving it
# back makes the snapshot look as old as the mtime says.
touch -t 202001010000 "$snap"
expect_fail "a snapshot older than the threshold is not healthy" \
  env BOTMOD_STATE_BACKUP_DIR="$work/snapshots" BOTMOD_BACKUP_MAX_AGE_HOURS=1 bash "$STATUS"
if grep -q "STALE" "$work/out"; then ok "STALE reported"; else fail "an old snapshot was not reported as stale"; fi
# The tampered copy has to fail the health check even when it is the newest:
# a green status must mean the recovery point would actually restore.
cp -r "$snap" "$work/snapshots-tampered"
printf 'corrupted\n' >> "$work/snapshots-tampered/botmod.json"
expect_fail "a corrupt snapshot is not healthy" \
  env BOTMOD_STATE_BACKUP_DIR="$work/snapshots-tampered" BOTMOD_BACKUP_MAX_AGE_HOURS=999999 bash "$STATUS"
if grep -q "UNVERIFIABLE" "$work/out"; then ok "UNVERIFIABLE reported"; else fail "a corrupt snapshot was reported as healthy"; fi
expect_fail "a non-numeric age threshold is a command-line error" \
  env BOTMOD_STATE_BACKUP_DIR="$work/snapshots" BOTMOD_BACKUP_MAX_AGE_HOURS=soon bash "$STATUS"

check "install.sh verifies MANIFEST.sha256 before it swaps the payload in"
ids="$work/ds-install"
# install.sh resolves its payload as <script-root>/dist/BotMod, so run a copy
# of the script under the scratch root instead of writing into the repo's dist.
payload="$work/install-root/dist/BotMod"
mkdir -p "$work/install-root/scripts"
cp "$ROOT/scripts/install.sh" "$ROOT/scripts/server-dir.sh" "$ROOT/scripts/digest.sh" \
  "$ROOT/scripts/deploy-lock.sh" "$ROOT/scripts/required-payload.sh" "$work/install-root/scripts/"
install_script="$work/install-root/scripts/install.sh"
mkdir -p "$ids/7DaysToDieServer_Data/Managed" "$ids/Mods/0_TFP_Harmony" "$ids/Mods/BotMod/Config"
touch "$ids/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll" "$ids/Mods/0_TFP_Harmony/0Harmony.dll"
# The complete set install.sh requires, taken from the list it reads, so a new
# required file cannot make this fixture quietly incomplete.
# shellcheck source=scripts/required-payload.sh
source "$ROOT/scripts/required-payload.sh"
for f in "${REQUIRED_PAYLOAD[@]}"; do
  mkdir -p "$payload/$(dirname "$f")"
  printf 'x\n' > "$payload/$f"
done
printf '{"TargetBotCount": 9}\n' > "$ids/Mods/BotMod/Config/botmod.json"
# The digest the release zip carries, written by whatever tool this host has
# (digest.sh picks sha256sum on Linux, shasum on macOS); install.sh's gate has
# to read back the one its own pick wrote.
(
  cd "$payload"
  source "$ROOT/scripts/digest.sh"
  find . -type f ! -name MANIFEST.sha256 | LC_ALL=C sort | while IFS= read -r f; do
    "${SHA[@]}" "${f#./}"
  done > "$work/manifest.new"
)
mv "$work/manifest.new" "$payload/MANIFEST.sha256"
expect_ok "payload matching its MANIFEST installs" \
  env SEVENDTD_DS_DIR="$ids" bash "$install_script"
if grep -q '"TargetBotCount": 9' "$ids/Mods/BotMod/Config/botmod.json"; then
  ok "operator config preserved across the reinstall"
else
  fail "operator config lost across the reinstall"
fi
# A payload built in place has no MANIFEST.sha256 to check, so the required-file
# gate is the only thing standing between a half-built dist/BotMod and a live
# install. It reads the list scripts/package.sh refuses to archive, so both
# halves of the gate fail on the same missing file.
mv "$payload/WebMod/styling.css" "$work/styling.css.away"
find "$ids/Mods/BotMod" -type f | LC_ALL=C sort > "$work/live.before"
expect_fail "payload missing a required file refused" \
  env SEVENDTD_DS_DIR="$ids" bash "$install_script"
if grep -q "styling.css" "$work/out"; then
  ok "the refusal names the missing file"
else
  fail "the refusal did not name the missing file"
fi
find "$ids/Mods/BotMod" -type f | LC_ALL=C sort > "$work/live.after"
if diff -q "$work/live.before" "$work/live.after" >/dev/null; then
  ok "the refused install changed nothing"
else
  fail "a payload missing a required file was installed anyway"
fi
mv "$work/styling.css.away" "$payload/WebMod/styling.css"

printf 'tampered\n' > "$payload/ModInfo.xml"
expect_fail "payload failing its MANIFEST refused" \
  env SEVENDTD_DS_DIR="$ids" bash "$install_script"
if grep -qx 'x' "$ids/Mods/BotMod/ModInfo.xml"; then
  ok "refused install left the live payload alone"
else
  fail "refused install replaced the live payload"
fi

check "install.sh puts the running install back when the swap itself fails"
# Undo the tamper above so the payload passes its manifest again; the manifest
# was never touched, only the file it covers.
printf 'x\n' > "$payload/ModInfo.xml"
# A `mv` that fails on the payload swap and delegates every other rename, so
# the first rename (the running install going aside) succeeds and the second
# does not: the one window the rollback exists to cover.
mkdir -p "$work/fakebin"
cat > "$work/fakebin/mv" <<'SH'
#!/usr/bin/env bash
case "${1:-}" in
  */.BotMod.staging.*)
    echo "simulated rename failure" >&2
    exit 1
    ;;
esac
exec env PATH=/usr/bin:/bin mv "$@"
SH
chmod +x "$work/fakebin/mv"
printf '{"TargetBotCount": 9}\n' > "$ids/Mods/BotMod/Config/botmod.json"
expect_fail "a failed swap is reported" \
  env SEVENDTD_DS_DIR="$ids" PATH="$work/fakebin:$PATH" bash "$install_script"
if [[ -f "$ids/Mods/BotMod/BotMod.dll" ]]; then
  ok "the running install is back in place"
else
  fail "a failed swap left the server with no mod dir"
fi
if grep -q '"TargetBotCount": 9' "$ids/Mods/BotMod/Config/botmod.json"; then
  ok "the operator config survived the failed swap"
else
  fail "the failed swap lost the operator config"
fi
if compgen -G "$ids/Mods/.BotMod.previous.*" > /dev/null; then
  fail "the previous install was left beside the mod dir"
else
  ok "no previous-install leftovers"
fi
if [[ -d "$ids/Mods/.botmod-deploy.lock" ]]; then
  fail "a failed install left its lock behind"
else
  ok "lock released after the failure"
fi

check "a second install is refused while one is running, and a dead lock is taken over"
mkdir -p "$ids/Mods/.botmod-deploy.lock"
echo $$ > "$ids/Mods/.botmod-deploy.lock/pid"
printf '{"TargetBotCount": 5}\n' > "$ids/Mods/BotMod/Config/botmod.json"
expect_fail "install refused while the lock is live" \
  env SEVENDTD_DS_DIR="$ids" bash "$install_script"
if grep -q '"TargetBotCount": 5' "$ids/Mods/BotMod/Config/botmod.json"; then
  ok "the refused install changed nothing"
else
  fail "the refused install still wrote to the server"
fi
# A lock whose owner is gone (a killed run) must not wedge the server forever.
echo 999999 > "$ids/Mods/.botmod-deploy.lock/pid"
expect_ok "stale lock taken over" \
  env SEVENDTD_DS_DIR="$ids" bash "$install_script"
if [[ -d "$ids/Mods/.botmod-deploy.lock" ]]; then
  fail "the taken-over lock was not released"
else
  ok "lock released after the successful install"
fi

check "uninstall takes the same lock, so it cannot delete a mod an install is swapping"
mkdir -p "$ids/Mods/.botmod-deploy.lock"
echo $$ > "$ids/Mods/.botmod-deploy.lock/pid"
expect_fail "uninstall refused while an install holds the lock" \
  env SEVENDTD_DS_DIR="$ids" BOTMOD_CONTAINER_CONFIG="$work/absent" \
  BOTMOD_STATE_BACKUP_DIR="$work/pre-uninstall" bash "$ROOT/scripts/uninstall.sh"
if [[ -d "$ids/Mods/BotMod" ]]; then
  ok "the refused uninstall left the mod installed"
else
  fail "the refused uninstall removed the mod anyway"
fi
rm -rf "$ids/Mods/.botmod-deploy.lock"
expect_ok "uninstall runs once the lock is free" \
  env SEVENDTD_DS_DIR="$ids" BOTMOD_CONTAINER_CONFIG="$work/absent" \
  BOTMOD_STATE_BACKUP_DIR="$work/pre-uninstall" bash "$ROOT/scripts/uninstall.sh"
if [[ -d "$ids/Mods/BotMod" ]]; then
  fail "uninstall left the mod dir behind"
else
  ok "mod removed"
fi
if [[ -d "$ids/Mods/.botmod-deploy.lock" ]]; then
  fail "uninstall left the deploy lock behind"
else
  ok "lock released after the uninstall"
fi

if [[ "$failures" -gt 0 ]]; then
  echo "$failures check(s) failed" >&2
  exit 1
fi
echo "state recovery: all checks passed"
