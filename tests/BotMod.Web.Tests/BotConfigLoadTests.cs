// BotConfigLoadTests — pins the config-loading contract that protects
// operators from silent misconfiguration:
//   - misspelled top-level keys are reported by BotConfig.UnknownKeys instead
//     of silently keeping the built-in default (Json.NET ignores them),
//   - valid keys never false-positive (binding is case-insensitive),
//   - out-of-range values are clamped by Normalize,
//   - an unreadable primary with a good .bak recovers the .bak (with
//     AtomicTextFile), and a missing file yields defaults.
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
            File.WriteAllText(path,
                "{ \"BotTeamCount\": 2, \"TeamAssignments\": { \"Grunt\": 9, \"Ranger\": 2 } }");
            BotConfig cfg = BotConfig.Load(path);
            Check("out-of-range assignment dropped to free-for-all",
                cfg.GetTeamAssignment("Grunt") == 0);
            Check("in-range assignment survives load", cfg.GetTeamAssignment("Ranger") == 2);
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
