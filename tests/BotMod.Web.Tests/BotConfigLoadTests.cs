// BotConfigLoadTests — pins the config-loading contract that protects
// operators from silent misconfiguration:
//   - misspelled top-level keys are reported by BotConfig.UnknownKeys instead
//     of silently keeping the built-in default (Json.NET ignores them),
//   - valid keys never false-positive (binding is case-insensitive),
//   - out-of-range values are clamped by Normalize,
//   - an unreadable primary with a good .bak recovers the .bak (with
//     AtomicTextFile), and a missing file yields defaults,
//   - TeamAssignments keys are canonicalized to NFC at load and at
//     SetTeamAssignment so NFD hand-edited spellings match NFC lookups.
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

        foreach (string d in Directory.GetDirectories(Path.GetTempPath(), "botmod-configtest-*"))
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }

        Console.WriteLine(_failures == 0 ? "all bot config load tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
