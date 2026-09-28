// AtomicTextFileTests: proves the durability contract of config persists:
// a completed Write leaves a complete, correct file plus a last-known-good
// .bak, and no staging litter; recovery paths (missing primary, unreadable
// primary) resolve to the .bak. Pure BCL; compiled and run by
// scripts/test-idempotency.sh (needs mcs + mono, not part of `make check`).
//
//   bash scripts/test-idempotency.sh
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BotMod.Foundation;

static class AtomicTextFileTests
{
    static int _failures;
    static readonly List<string> _tempDirs = new List<string>();

    // Run-scoped tag: the end-of-run cleanup must delete THIS process's
    // directories and nothing else. Globbing a fixed "botmod-atomictest-*"
    // pattern in the shared temp root let any other run of this suite (a
    // second worktree or a local run beside CI, same machine) delete a live
    // run's directory mid-test, which surfaced as writers failing with "could
    // not find a part of the path" for a .tmp file that existed a moment
    // earlier. The prefix is deliberately not "botmod-atomictest-*": a run of
    // an older build sharing this temp root still globs that name, and it
    // must not be able to reach this run's directories either.
    static readonly string RunTag = "botmod-atomic-" + Guid.NewGuid().ToString("N") + "-";

    // Every directory this process created is recorded in _tempDirs, so cleanup
    // removes exactly those and nothing else. The temp path is shared with any
    // other run of this suite (a parallel CI job, a second developer on the
    // same box), and the writers hammer the directory for seconds at a time: a
    // sweep over a common prefix deletes a live run's directory out from under
    // its writers, which surfaces as FileNotFoundException on the staging .tmp
    // and as a missing primary - a failure that looks like a torn-write bug in
    // AtomicTextFile. The run tag also keeps another process sweeping the old
    // shared prefix from reaching these directories while their writers are
    // mid-flight.

    // Wall-clock budget for the concurrency storms below, measured rather than
    // hardcoded. Every Write is two fsync'd file writes (the staging .tmp and
    // the .bak snapshot) under one process-wide lock, so the 320 writes of
    // block 7 cost 17-25 s on an idle developer box and ran straight through
    // the old flat 30 s timeout once the rest of the suite and CI's other
    // jobs were competing for the same disk: the run failed with "concurrent
    // writers finished within timeout" while every correctness assertion in
    // the block passed. Calibrating on this machine, this filesystem and this
    // process keeps a real hang (the WriteGate regression these storms exist
    // to catch) failing the run, without a loaded runner reading as a
    // durability bug. Slack absorbs the load variance the calibration itself
    // does not see; the floor keeps a fast disk from budgeting too little, the
    // ceiling keeps a slow one from parking the suite in a wait.
    const double TimeoutSlack = 3.0;
    const int TimeoutFloorMs = 30000;
    const int TimeoutCeilingMs = 180000;
    const int CalibrationWrites = 8;

    static int _perWriteMs = -1;

    // Milliseconds that <paramref name="writes"/> serialized writes are
    // allowed to take on this host, from the cost of one measured here.
    static int BudgetMs(int writes)
    {
        if (_perWriteMs < 0)
        {
            string dir = TempDir(), path = Path.Combine(dir, "calibration.json");
            for (int i = 0; i < 4; i++) AtomicTextFile.Write(path, "{\"warm\":" + i + "}");
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < CalibrationWrites; i++) AtomicTextFile.Write(path, "{\"cal\":" + i + "}");
            sw.Stop();
            // A cost of zero means the clock or the filesystem is not
            // reporting; one millisecond per write keeps the budget finite.
            _perWriteMs = Math.Max(1, (int)Math.Round(sw.Elapsed.TotalMilliseconds / CalibrationWrites));
        }
        long ms = (long)(writes * (double)_perWriteMs * TimeoutSlack);
        if (ms < TimeoutFloorMs) ms = TimeoutFloorMs;
        if (ms > TimeoutCeilingMs) ms = TimeoutCeilingMs;
        return (int)ms;
    }

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    // Per-process scratch root: only the dirs this process created are
    // recorded and only those are deleted at the end. The system temp dir is
    // shared with every other run of this suite on the host (a second gate,
    // another checkout), and a prefix-wide delete tears down a live run's
    // staging file, whose in-flight writers then fail with a vanished .tmp
    // (reported as "concurrent writes complete without errors" plus a torn
    // final primary). Two checkouts of this repo on one machine hit that.
    static string TempDir()
    {
        // Private namespace: the run tag keeps a concurrent run of this suite
        // out of each other's cleanup and keeps any other process sweeping the
        // old shared prefix from deleting these directories while their writers
        // are mid-flight; the per-process id separates the two namespaces.
        string dir = Path.Combine(Path.GetTempPath(),
            RunTag + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        lock (_tempDirs) _tempDirs.Add(dir);
        return dir;
    }

    static void Cleanup()
    {
        lock (_tempDirs)
        {
            foreach (string dir in _tempDirs)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _tempDirs.Clear();
        }
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
            string s, src;
            bool ok = AtomicTextFile.TryRead(path, out s, out src);
            Check("missing primary falls back to .bak", ok && s == "{\"v\":1}");
            // readFrom is how a caller tells the operator which copy served the
            // data, so the fallback has to name the .bak, not the primary it
            // never read. Asserted on both the hit and the miss: a TryRead
            // that always returned null would satisfy the content checks while
            // leaving the recovery report silent.
            Check("fallback read names the .bak it served from", src == AtomicTextFile.BackupPath(path));
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
            string s, src;
            Check("torn primary still readable via TryRead",
                AtomicTextFile.TryRead(path, out s, out src) && s == "{\"v\":2");
            // The primary is present, so it wins over the .bak: the caller
            // must be told it is reading the live file, not the backup.
            Check("present primary is the source, not the .bak", src == path);
            Check(".bak candidate exists for Load's fallback",
                File.ReadAllText(AtomicTextFile.BackupPath(path)) == "{\"v\":1}");
        }

        // 5. Nothing on disk at all -> TryRead reports failure (Load then uses
        //    defaults, same as before this class existed).
        {
            string dir = TempDir(), path = Path.Combine(dir, "absent.json");
            string s, src;
            Check("no primary and no .bak reads false", !AtomicTextFile.TryRead(path, out s, out src));
            Check("failed read yields no content", s == null);
            // Documented pairing: readFrom is null together with a false
            // return, so a caller cannot report a source it never read.
            Check("failed read names no source", src == null);
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
                    catch (Exception ex) { lock (errors) errors.Add(ex.ToString()); }
                    finally { if (System.Threading.Interlocked.Decrement(ref remaining) == 0) done.Set(); }
                });
            }
            // A hang here means the WriteGate serialization broke (the exact
            // regression this suite pins), so a timeout must fail the run,
            // not sail through to assertions on whatever reached the disk.
            int budget = BudgetMs(writers * perWriter);
            bool finished = done.WaitOne(budget);
            Check("concurrent writers finished within timeout (" + budget + " ms, " + _perWriteMs + " ms/write)", finished);
            Check("concurrent writes complete without errors", errors.Count == 0);
            foreach (string e in errors) Console.WriteLine("     " + e);
            string s, src;
            bool read = AtomicTextFile.TryRead(path, out s, out src);
            // The final content must be ONE complete payload from a single
            // write call, never interleaved bytes from two.
            bool complete = false;
            for (int w = 0; w < writers && !complete; w++)
                for (int i = 0; i < perWriter && !complete; i++)
                    complete = read && s == "{\"writer\":" + w + ",\"seq\":" + i + ",\"pad\":\"0123456789\"}";
            Check("final primary is one complete payload (no torn write)", complete);
            Check("no staging tmp left after concurrent writes", !File.Exists(AtomicTextFile.TmpPath(path)));
        }

        // 8. Degraded-write reporting: a failed .bak copy is the one swallowed
        //    failure with no downstream signal. The swap still lands (the new
        //    content is not lost), but the last-known-good is not refreshed, so
        //    a later torn primary has nothing to recover from and Load would
        //    quietly reset every persisted operator setting to defaults. A
        //    read-only .bak stands in for the real cases (backup on a
        //    read-only mount, out of space, no permission in the mod dir).
        {
            string dir = TempDir(), path = Path.Combine(dir, "botmod.json");
            var warns = new List<string>();
            Action<string> previous = AtomicTextFile.Warn;
            AtomicTextFile.Warn = msg => { lock (warns) warns.Add(msg); };
            try
            {
                AtomicTextFile.Write(path, "{\"v\":1}");
                File.WriteAllText(AtomicTextFile.BackupPath(path), "{\"v\":0}");
                File.SetAttributes(AtomicTextFile.BackupPath(path), FileAttributes.ReadOnly);
                AtomicTextFile.Write(path, "{\"v\":2}");
            }
            finally { AtomicTextFile.Warn = previous; }
            File.SetAttributes(AtomicTextFile.BackupPath(path), FileAttributes.Normal);
            Check("write still lands when the backup copy fails",
                File.ReadAllText(path) == "{\"v\":2}");
            string joined;
            lock (warns) joined = string.Join(" | ", warns);
            Check("failed backup copy is reported (" + warns.Count + " warning(s))", warns.Count == 1);
            Check("backup warning names the .bak path", joined.Contains(AtomicTextFile.BackupPath(path)));
            Check("backup warning carries the exception cause",
                joined.Contains("denied") || joined.Contains("Access"));
        }

        // 9. Concurrency: readers must never observe Write's delete-then-move
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
            var doneReaders = new System.Threading.ManualResetEvent(false);
            int reads = 0;
            const int readers = 4;
            int readersLeft = readers;
            const int rewrites = 200;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    for (int i = 0; i < rewrites; i++)
                        AtomicTextFile.Write(path, i % 2 == 0 ? pb : pa);
                }
                catch (Exception ex) { lock (errors) errors.Add("writer: " + ex.ToString()); }
                finally { doneWriters.Set(); }
            });
            for (int r = 0; r < readers; r++)
            {
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        while (!stopReaders.WaitOne(0))
                        {
                            string s, src;
                            if (!AtomicTextFile.TryRead(path, out s, out src))
                                lock (errors) errors.Add("read failed during swap window");
                            else if (s != pa && s != pb)
                                lock (errors) errors.Add("torn or stale read: " + s);
                            System.Threading.Interlocked.Increment(ref reads);
                        }
                    }
                    catch (Exception ex) { lock (errors) errors.Add("reader: " + ex.ToString()); }
                    finally { if (System.Threading.Interlocked.Decrement(ref readersLeft) == 0) doneReaders.Set(); }
                });
            }
            // Readers drain behind whatever writes are still queued on
            // WriteGate when the stop signal lands, so they get the storm's
            // budget too rather than a second flat constant.
            int budget = BudgetMs(rewrites);
            bool finished = doneWriters.WaitOne(budget);
            stopReaders.Set();
            Check("writer finished within timeout", finished);
            // Join the readers before inspecting their findings: they keep
            // appending to errors and reads after the stop signal, so reading
            // either collection early reports a partial, order-dependent result.
            Check("readers stopped within timeout", doneReaders.WaitOne(budget));
            string final, readFrom;
            bool ok = AtomicTextFile.TryRead(path, out final, out readFrom);
            Check("final primary is the last written payload",
                ok && final == pa); // 200 rewrites ending on an odd index rewrite pa last
            // Floor, not just "> 0": a reader that gets scheduled once and
            // exits would satisfy that while the swap window went unobserved,
            // which is the whole thing this block exists to check. Runs land
            // in the hundreds-to-thousands on a 200-rewrite storm, so 100 is
            // far below any real run and far above "the hammer never ran".
            Check("readers completed a meaningful number of reads (" + reads + ")",
                reads >= 100);
            Check("reads during concurrent writes all saw a complete payload (" + reads + " reads)",
                errors.Count == 0);
            foreach (string e in errors) Console.WriteLine("     " + e);
        }

        // Only this run's directories, and a concurrent remover is not a
        // failure: an already-gone directory is the desired end state.
        Cleanup();

        Console.WriteLine(_failures == 0 ? "all atomic text file tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
