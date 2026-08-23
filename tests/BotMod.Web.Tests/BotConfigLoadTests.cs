// BotConfigLoadTests — pins the config-loading contract that protects
// operators from silent misconfiguration:
//   - misspelled top-level keys are reported by BotConfig.UnknownKeys instead
//     of silently keeping the built-in default (Json.NET ignores them),
//   - valid keys never false-positive (binding is case-insensitive),
//   - out-of-range values are clamped by Normalize,
//   - an unreadable primary with a good .bak recovers the .bak (with
//     AtomicTextFile), and a missing file yields defaults,
//   - TeamAssignments keys are canonicalized to NFC at load and at
//     SetTeamAssignment so NFD hand-edited spellings match NFC lookups,
//   - SetVsTarget maps the admin "vs" aliases onto the config flag AND the
//     exact JSON field name PersistConfigField must write (a wrong name
//     silently drops the toggle on the next reload),
//   - WeaponProfile.ForGun classifies gun ids into combat profiles (fire
//     rate, burst, damage, range, pellets) - the numbers every bot shoots
//     with; a misclassification silently retunes all combat,
//   - BotCharacter.WantsToCamp keeps its Q3 boundary semantics.
// BotConfig pulls ModApi -> engine types, so this compiles the FULL mod
// source against the game DLLs; scripts/test-idempotency.sh gates it on a
// game install being present.
using System;
using System.Collections.Generic;
using System.IO;
using BotMod.Config;

static class BotConfigLoadTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "botmod-configtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // SetVsTarget's field output is written verbatim into botmod.json by
    // PersistConfigField, so it must name a real config property or the
    // setting silently drops on the next load (unknown-key warning).
    static bool IsConfigProperty(string name)
    {
        return name != null && typeof(BotConfig).GetProperty(name) != null;
    }

    static int Main()
    {
        // Unknown-key detection: the typo surface.
        var u = BotConfig.UnknownKeys("{ \"TargetBotCount\": 2, \"TagetBotCount\": 9 }");
        Check("misspelled key detected", u.Count == 1 && u[0] == "TagetBotCount");
        u = BotConfig.UnknownKeys("{ \"targetbotcount\": 3, \"BOTHEALTH\": 50 }");
        Check("case variants of real keys are known", u.Count == 0);
        u = BotConfig.UnknownKeys("{}");
        Check("empty object has no unknown keys", u.Count == 0);
        u = BotConfig.UnknownKeys("{ \"TeamAssignments\": {\"Grunt\": 1}, \"UseNeuralBrain\": true }");
        Check("keys nested inside maps are not flagged", u.Count == 0);

        // A config containing only a typo still loads (warn-and-continue, not
        // reject): the misspelled key is ignored, defaults apply elsewhere.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            File.WriteAllText(path, "{ \"TagetBotCount\": 9 }");
            BotConfig cfg = BotConfig.Load(path);
            Check("typo-only config loads with defaults", cfg.TargetBotCount == 6 && cfg.Difficulty == 2);
        }

        // Fail-fast-ish range validation: absurd values are clamped at load.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            File.WriteAllText(path, "{ \"TargetBotCount\": -5, \"Difficulty\": 99, \"HeadshotChance\": 7 }");
            BotConfig cfg = BotConfig.Load(path);
            Check("negative target clamped to 0", cfg.TargetBotCount == 0);
            Check("difficulty clamped to 0-4", cfg.Difficulty == 4);
            Check("probability clamped to 0-1", cfg.HeadshotChance == 1f);
        }

        // The difficulty preset raises VisionRange after the relational
        // clamps run; LoseTargetRange must be re-raised with it or bots lose
        // targets closer than they can see (found by BotConfigFuzzTests on
        // the shipped config: difficulty 4, vision preset 120, lose range 85).
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            File.WriteAllText(path, "{ \"Difficulty\": 4, \"VisionRange\": 70, \"LoseTargetRange\": 85 }");
            BotConfig cfg = BotConfig.Load(path);
            Check("difficulty vision bump keeps lose-range above it",
                cfg.VisionRange >= 80f && cfg.LoseTargetRange >= cfg.VisionRange);
        }

        // A JSON null TeamAssignments map must be repaired by Normalize: the
        // locked helpers index it directly, so a lingering null would make the
        // first admin assignment throw NullReferenceException on a web thread.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            File.WriteAllText(path, "{ \"TeamAssignments\": null }");
            BotConfig cfg = BotConfig.Load(path);
            cfg.SetTeamAssignment("Grunt", 2);
            Check("null TeamAssignments repaired at load", cfg.GetTeamAssignment("Grunt") == 2);
        }

        // Assignments outside the loaded team range drop to free-for-all, so a
        // stale hand-edited config cannot put bots on nonexistent teams.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            AtomicTextFile.Write(path,
                "{ \"BotTeamCount\": 2, \"TeamAssignments\": { \"Grunt\": 9, \"Ranger\": 2 } }");
            BotConfig cfg = BotConfig.Load(path);
            Check("out-of-range assignment dropped to free-for-all",
                cfg.GetTeamAssignment("Grunt") == 0);
            Check("in-range assignment survives load", cfg.GetTeamAssignment("Ranger") == 2);
        }

        // Unicode identity: an NFD spelling in a hand-edited config (macOS
        // editors emit base letter + combining mark) must land on the same
        // stored key as the NFC form derived from spawned bot names, or the
        // assignment silently never applies.
        {
            string nfdKira = "Ki\u0301ra", nfcKira = "K\u00edra";
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            string json = "{ \"BotTeamCount\": 2, \"TeamAssignments\": { \"" + nfdKira + "\": 1 } }";
            File.WriteAllText(path, json, System.Text.Encoding.UTF8);
            BotConfig cfg = BotConfig.Load(path);
            Check("NFD config key found via NFC lookup", cfg.GetTeamAssignment(nfcKira) == 1);

            // Runtime assignment path canonicalizes too.
            cfg.SetTeamAssignment("[Bot] " + nfdKira + "_42", 2);
            Check("SetTeamAssignment stores NFC key", cfg.GetTeamAssignment(nfcKira) == 2);
        }

        // Hot-path canonical lookup: Bot.TeamKey holds BotText.BaseName output
        // (already IdentityKey-stable), and GetTeamId feeds it straight into
        // GetTeamAssignmentCanonical on every DamageEntity event and targeting
        // candidate. Pin that the fast path returns exactly what the
        // normalizing lookup returns for those keys.
        {
            string nfdKira = "Ki\u0301ra";
            var cfg = new BotConfig();
            cfg.BotTeamCount = 4;
            cfg.SetTeamAssignment("Grunt", 3);
            cfg.SetTeamAssignment("[Bot] " + nfdKira + "_7", 2);
            Check("canonical lookup matches normalizing lookup",
                cfg.GetTeamAssignmentCanonical("Grunt") == cfg.GetTeamAssignment("Grunt"));
            string kiraKey = BotText.BaseName("[Bot] " + nfdKira + "_7");
            Check("BaseName key resolves through canonical lookup",
                kiraKey == "K\u00edra" && cfg.GetTeamAssignmentCanonical(kiraKey) == 2);
            Check("canonical lookup of unassigned key is free-for-all",
                cfg.GetTeamAssignmentCanonical("Ranger") == 0);
            Check("empty canonical key is free-for-all", cfg.GetTeamAssignmentCanonical("") == 0);
        }

        // Paste noise in identity keys: a name copied from a web page can
        // carry zero-width characters; the stored key must collapse onto the
        // clean spelling, or the assignment silently never applies.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            string json = "{ \"BotTeamCount\": 2, \"TeamAssignments\": { \"Gru\u200bnt\": 1 } }";
            File.WriteAllText(path, json, System.Text.Encoding.UTF8);
            BotConfig cfg = BotConfig.Load(path);
            Check("ZWSP config key found via clean lookup", cfg.GetTeamAssignment("Grunt") == 1);
        }

        // Null/empty entries in the name/loadout arrays (hand-edited JSON
        // tolerates them) must not survive Normalize: ForGun's mixed pick
        // dereferences a null pool entry (every mixed spawn throws and the
        // auto-respawn loop dies) and PickName mints tagless "_NN" names.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            AtomicTextFile.Write(path,
                "{ \"LoadoutPool\": [\"gunHandgunT1Pistol\", null], \"BotNames\": [\"Grunt\", null] }");
            BotConfig cfg = BotConfig.Load(path);
            cfg.Normalize();
            Check("null pool entry dropped at load",
                cfg.LoadoutPool.Length == 1 && cfg.LoadoutPool[0] == "gunHandgunT1Pistol");
            Check("null name entry dropped at load",
                cfg.BotNames.Length == 1 && cfg.BotNames[0] == "Grunt");
            bool picked = true;
            for (int i = 0; i < 64; i++)
                if (WeaponProfile.ForGun("mixed", cfg).GunId == null) { picked = false; break; }
            Check("mixed pick never yields a null gun after filtering", picked);
        }

        // Everything dropped: an all-null array falls back to the documented
        // defaults instead of an empty list (which would re-crash the picker).
        {
            var cfg = new BotConfig { LoadoutPool = new string[] { null, "" }, BotNames = new string[] { null } };
            cfg.Normalize();
            Check("all-null pool falls back to default rifle",
                cfg.LoadoutPool.Length == 1 && cfg.LoadoutPool[0] == "gunMGT1AK47");
            Check("all-null names fall back to default", cfg.BotNames.Length == 1 && cfg.BotNames[0] == "Bot");
        }


        // Recovery: torn primary + good .bak restores the last-known-good
        // values instead of silently resetting to defaults.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            AtomicTextFile.Write(path, "{ \"TargetBotCount\": 8 }");
            AtomicTextFile.Write(path, "{ \"TargetBotCount\": 12 }");
            File.WriteAllText(path, "{ \"TargetBotCount\": "); // torn JSON
            BotConfig cfg = BotConfig.Load(path);
            Check("torn primary recovers from .bak", cfg.TargetBotCount == 8);
        }

        // Nothing on disk (fresh install): clean defaults, no throw.
        {
            string dir = TempDir();
            BotConfig cfg = BotConfig.Load(Path.Combine(dir, "absent.json"));
            Check("missing file -> defaults", cfg.Enabled && cfg.DedicatedOnly && !cfg.AllowSyntheticAuthBypass);
            Check("auth bypass stays off by default", !cfg.AllowSyntheticAuthBypass);
        }

        // SetVsTarget: the admin alias surface shared by `bot vs` and the web
        // "vs" action. Each accepted alias must flip exactly its flag AND
        // name the JSON field that persists it; the field name is part of the
        // contract because PersistConfigField writes it verbatim into
        // botmod.json - a rename here would silently drop the setting on
        // reload. Unknown targets must be rejected, not guessed.
        {
            var cfg = new BotConfig();
            string f;
            Check("singular alias 'bot' flips BotVsBot",
                cfg.SetVsTarget("bot", false, out f) && !cfg.BotVsBot && IsConfigProperty(f));
            Check("plural alias 'zombies' flips BotVsZombie",
                cfg.SetVsTarget("zombies", false, out f) && !cfg.BotVsZombie && IsConfigProperty(f));
            Check("'human' aliases players",
                cfg.SetVsTarget("human", false, out f) && !cfg.BotVsPlayer && IsConfigProperty(f));
            Check("plural alias 'players' aliases same flag",
                cfg.SetVsTarget("players", true, out f) && cfg.BotVsPlayer && IsConfigProperty(f));
            // Rejection leaves every flag at its value before the call.
            bool vsBot = cfg.BotVsBot, vsZombie = cfg.BotVsZombie, vsPlayer = cfg.BotVsPlayer;
            Check("unknown target rejected without touching flags",
                !cfg.SetVsTarget("trader", true, out f) && f == null
                && cfg.BotVsBot == vsBot && cfg.BotVsZombie == vsZombie && cfg.BotVsPlayer == vsPlayer);
        }

        // WeaponProfile.ForGun: gun-id classification driving fire rate,
        // burst shape, damage, range and pellet count for every bot.
        {
            var cfg = new BotConfig();
            WeaponProfile p;

            p = WeaponProfile.ForGun("gunShotgunT1DoubleBarrel", cfg);
            Check("pump shotgun: single shots, pellet spread, short range",
                p.GunId == "gunShotgunT1DoubleBarrel" && p.BurstMin == 1 && p.BurstMax == 1
                && p.Pellets == 8 && p.Range == 22f && p.MagSize == 2);

            p = WeaponProfile.ForGun("gunShotgunT3AutoShotgun", cfg);
            Check("auto shotgun keeps shotgun class but faster, 6 pellets, bigger mag",
                p.Pellets == 6 && p.MagSize == 16 && p.FireRate < 0.55f && p.Range == 22f);

            p = WeaponProfile.ForGun("gunRifleT3SniperRifle", cfg);
            Check("sniper: long range, high damage, tight spread, no burst",
                p.Range == 90f && p.Damage == 42f && p.SpreadDeg == 0.35f
                && p.BurstMin == 1 && p.BurstMax == 1);

            p = WeaponProfile.ForGun("gunHandgunT3SMG5", cfg);
            Check("smg: long burst, fast fire rate",
                p.BurstMin >= 5 && p.BurstMax >= p.BurstMin && p.FireRate == 0.09f);

            p = WeaponProfile.ForGun("gunMGT1AK47", cfg);
            Check("ak family: mid burst, mid range",
                p.BurstMin == 3 && p.BurstMax == 6 && p.Range == 55f && p.Damage == 16);

            p = WeaponProfile.ForGun("gunHandgunT6Magnum", cfg);
            Check("magnum: small mag, heavy shots",
                p.MagSize == 6 && p.Damage == 34 && p.BurstMax <= 2);

            p = WeaponProfile.ForGun("gunHandgunT1Pistol", cfg);
            Check("unclassified gun falls back to pistol profile",
                p.Damage == 16 && p.Range == 40f && p.MagSize == 15);

            // Classification is case-insensitive on the id but preserves the
            // original casing in GunId (the id goes back to item lookups).
            p = WeaponProfile.ForGun("GUNMGT1AK47", cfg);
            Check("classification case-insensitive, GunId verbatim",
                p.GunId == "GUNMGT1AK47" && p.Range == 55f);

            // "mixed" resolves through LoadoutPool to the exact profile of the
            // picked entry; an empty pool falls back to a working default.
            cfg.LoadoutPool = new[] { "gunRifleT3SniperRifle", "gunHandgunT1Pistol" };
            WeaponProfile mixedPick = WeaponProfile.ForGun("mixed", cfg);
            bool matchesDirect = false;
            foreach (string gun in cfg.LoadoutPool)
            {
                WeaponProfile direct = WeaponProfile.ForGun(gun, cfg);
                if (mixedPick.GunId == direct.GunId)
                    matchesDirect = direct.FireRate == mixedPick.FireRate && direct.Range == mixedPick.Range
                        && direct.Damage == mixedPick.Damage && direct.Pellets == mixedPick.Pellets;
            }
            Check("mixed pick equals the direct profile of a pooled gun", matchesDirect);

            cfg.LoadoutPool = new string[0];
            Check("empty loadout pool falls back to a usable rifle",
                WeaponProfile.ForGun("mixed", cfg).Range > 0f
                && WeaponProfile.ForGun(null, cfg).Damage > 0);
        }

        // WantsToCamp: Q3-style camp roll with strict boundaries. Camps only
        // above the camper threshold, only when healthy (retreating is the
        // low-health path), and strictly below the camper-scaled roll.
        {
            var ch = new BotCharacter { Camper = 0.8f };
            Check("camper at threshold 0.45 never camps", !new BotCharacter { Camper = 0.45f }.WantsToCamp(0.9f, 0f));
            Check("camper just past threshold can camp", new BotCharacter { Camper = 0.46f }.WantsToCamp(0.9f, 0f));
            Check("wounded bot does not camp (retreat instead)", !ch.WantsToCamp(0.5f, 0f));
            Check("health at boundary 0.55 does not camp", !ch.WantsToCamp(0.55f, 0f));
            Check("healthy bot below roll camps", ch.WantsToCamp(0.56f, 0.1f));
            Check("roll equal to camper*0.4 does not camp", !ch.WantsToCamp(0.9f, 0.8f * 0.4f));
            Check("non-camper personality never camps", !new BotCharacter { Camper = 0.2f }.WantsToCamp(1f, 0f));
        }

        foreach (string d in Directory.GetDirectories(Path.GetTempPath(), "botmod-configtest-*"))
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }

        Console.WriteLine(_failures == 0 ? "all bot config load tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
