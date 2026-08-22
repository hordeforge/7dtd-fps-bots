// AtomicTextFileTests — proves the durability contract of config persists:
// a completed Write leaves a complete, correct file plus a last-known-good
// .bak, and no staging litter; recovery paths (missing primary, unreadable
// primary) resolve to the .bak. Pure BCL; compiled and run by
// scripts/test-idempotency.sh (needs mcs + mono, not part of `make check`).
//
//   bash scripts/test-idempotency.sh
using System;
using System.IO;
using BotMod.Config;

static class AtomicTextFileTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "botmod-atomictest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static int Main()
    {
        // 1. First write: primary exists with the content, no .bak yet.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            AtomicTextFile.Write(path, "{\"v\":1}");
            Check("first write creates the file", File.ReadAllText(path) == "{\"v\":1}");
            Check("first write creates no backup", !File.Exists(AtomicTextFile.BackupPath(path)));
            Check("no staging tmp left behind", !File.Exists(AtomicTextFile.TmpPath(path)));
        }

        // 2. Rewrite: previous good content preserved at .bak, primary updated,
        //    so a torn/corrupted primary always has a recoverable predecessor.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            AtomicTextFile.Write(path, "{\"v\":1}");
            AtomicTextFile.Write(path, "{\"v\":2}");
            Check("rewrite updates the primary", File.ReadAllText(path) == "{\"v\":2}");
            Check("rewrite snapshots previous content to .bak",
                File.ReadAllText(AtomicTextFile.BackupPath(path)) == "{\"v\":1}");
            Check("rewrite leaves no staging tmp", !File.Exists(AtomicTextFile.TmpPath(path)));
        }

        // 3. Recovery: primary deleted (crash between delete and move during a
        //    persist) -> TryRead resolves the .bak written by earlier writes.
        //    .bak intentionally holds the pre-rewrite content ({\"v\":1}): the
        //    point is that SOME complete good copy survives, not the newest.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            AtomicTextFile.Write(path, "{\"v\":1}");
            AtomicTextFile.Write(path, "{\"v\":2}");
            File.Delete(path);
            string s;
            bool ok = AtomicTextFile.TryRead(path, out s);
            Check("missing primary falls back to .bak", ok && s == "{\"v\":1}");
        }

        // 4. Recovery: primary present but garbage (torn write from an older
        //    non-atomic persist, manual edit gone wrong). TryRead returns the
        //    bytes verbatim; parsing is Load's job, so here assert that the
        //    primary comes back as-is AND the .bak from earlier successful
        //    persists is on disk as Load's fallback candidate.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            AtomicTextFile.Write(path, "{\"v\":1}");
            AtomicTextFile.Write(path, "{\"v\":2}");
            File.WriteAllText(path, "{\"v\":2"); // torn JSON
            string s;
            Check("torn primary still readable via TryRead",
                AtomicTextFile.TryRead(path, out s) && s == "{\"v\":2");
            Check(".bak candidate exists for Load's fallback",
                File.ReadAllText(AtomicTextFile.BackupPath(path)) == "{\"v\":1}");
        }

        // 5. Nothing on disk at all -> TryRead reports failure (Load then uses
        //    defaults, same as before this class existed).
        {
            string dir = TempDir(), path = Path.Combine(dir, "absent.json");
            string s;
            Check("no primary and no .bak reads false", !AtomicTextFile.TryRead(path, out s));
            Check("failed read yields no content", s == null);
        }

        // 6. Content round-trip fidelity: multi-line UTF-8 payload survives.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            string cfg = "{\n  \"Enabled\": true,\n  \"TeamAssignments\": { \"Grunt\": 2 }\n}";
            AtomicTextFile.Write(path, cfg);
            AtomicTextFile.Write(path, cfg + "\n");
            Check("multi-line content round-trips byte-exact",
                File.ReadAllText(path) == cfg + "\n");
            Check(".bak keeps the prior full content",
                File.ReadAllText(AtomicTextFile.BackupPath(path)) == cfg);
        }

        foreach (string dir in Directory.GetDirectories(Path.GetTempPath(), "botmod-atomictest-*"))
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }

        Console.WriteLine(_failures == 0 ? "all atomic text file tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
