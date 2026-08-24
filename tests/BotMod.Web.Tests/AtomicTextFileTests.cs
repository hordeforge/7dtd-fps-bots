// AtomicTextFileTests — proves the durability contract of config persists:
// a completed Write leaves a complete, correct file plus a last-known-good
// .bak, and no staging litter; recovery paths (missing primary, unreadable
// primary) resolve to the .bak. Pure BCL; compiled and run by
// scripts/test-idempotency.sh (needs mcs + mono, not part of `make check`).
//
//   bash scripts/test-idempotency.sh
using System;
using System.Collections.Generic;
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

        // 7. Concurrency: overlapping writers must serialize. Before the
        //    WriteGate fix, two threads could interleave the fixed .tmp staging:
        //    FileShare.None collisions threw IOException out of Write, and a
        //    half-written tmp could be moved onto the primary (torn JSON).
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            var errors = new List<string>();
            var done = new System.Threading.ManualResetEvent(false);
            const int writers = 8, perWriter = 40;
            int remaining = writers;
            for (int w = 0; w < writers; w++)
            {
                int id = w;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        for (int i = 0; i < perWriter; i++)
                            AtomicTextFile.Write(path, "{\"writer\":" + id + ",\"seq\":" + i + ",\"pad\":\"0123456789\"}");
                    }
                    catch (Exception ex) { lock (errors) errors.Add(ex.Message); }
                    finally { if (System.Threading.Interlocked.Decrement(ref remaining) == 0) done.Set(); }
                });
            }
            // A hang here means the WriteGate serialization broke (the exact
            // regression this suite pins), so a timeout must fail the run,
            // not sail through to assertions on whatever reached the disk.
            bool finished = done.WaitOne(30000);
            Check("concurrent writers finished within timeout", finished);
            Check("concurrent writes complete without errors", errors.Count == 0);
            foreach (string e in errors) Console.WriteLine("     " + e);
            string s;
            bool read = AtomicTextFile.TryRead(path, out s);
            // The final content must be ONE complete payload from a single
            // write call, never interleaved bytes from two.
            bool complete = false;
            for (int w = 0; w < writers && !complete; w++)
                for (int i = 0; i < perWriter && !complete; i++)
                    complete = read && s == "{\"writer\":" + w + ",\"seq\":" + i + ",\"pad\":\"0123456789\"}";
            Check("final primary is one complete payload (no torn write)", complete);
            Check("no staging tmp left after concurrent writes", !File.Exists(AtomicTextFile.TmpPath(path)));
        }

        // 8. Concurrency: readers must never observe Write's delete-then-move
        //    swap mid-flight. Without the WriteGate around TryRead, a reader
        //    could pass File.Exists(primary) just before the Delete and hit
        //    FileNotFoundException, then find the .bak momentarily being
        //    overwritten by the next Write's Copy -> IOException -> TryRead
        //    returns false even though a complete file exists, which Load
        //    would turn into "restore from defaults" (silent operator-state
        //    loss). One writer alternates two full payloads while readers
        //    hammer TryRead: every read must succeed with one of the exact
        //    payloads, never false, never partial.
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            const string pa = "{\"phase\":\"a\",\"pad\":\"0123456789\"}";
            const string pb = "{\"phase\":\"b\",\"pad\":\"0123456789\"}";
            AtomicTextFile.Write(path, pa);
            var errors = new List<string>();
            var doneWriters = new System.Threading.ManualResetEvent(false);
            var stopReaders = new System.Threading.ManualResetEvent(false);
            int reads = 0;
            const int rewrites = 200;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    for (int i = 0; i < rewrites; i++)
                        AtomicTextFile.Write(path, i % 2 == 0 ? pb : pa);
                }
                catch (Exception ex) { lock (errors) errors.Add("writer: " + ex.Message); }
                finally { doneWriters.Set(); }
            });
            for (int r = 0; r < 4; r++)
            {
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        while (!stopReaders.WaitOne(0))
                        {
                            string s;
                            if (!AtomicTextFile.TryRead(path, out s))
                                lock (errors) errors.Add("read failed during swap window");
                            else if (s != pa && s != pb)
                                lock (errors) errors.Add("torn or stale read: " + s);
                            System.Threading.Interlocked.Increment(ref reads);
                        }
                    }
                    catch (Exception ex) { lock (errors) errors.Add("reader: " + ex.Message); }
                });
            }
            bool finished = doneWriters.WaitOne(30000);
            stopReaders.Set();
            Check("writer finished within timeout", finished);
            string final;
            bool ok = AtomicTextFile.TryRead(path, out final);
            Check("final primary is the last written payload",
                ok && final == pa); // 200 rewrites ending on an odd index rewrite pa last
            Check("reads during concurrent writes all saw a complete payload (" + reads + " reads)",
                errors.Count == 0);
            foreach (string e in errors) Console.WriteLine("     " + e);
        }

        foreach (string dir in Directory.GetDirectories(Path.GetTempPath(), "botmod-atomictest-*"))
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }

        Console.WriteLine(_failures == 0 ? "all atomic text file tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
