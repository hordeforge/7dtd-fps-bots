// TeamAssignmentsConcurrencyTests: hammers the locked team-map helpers from
// writer threads while reader threads treat the map the way the game tick
// does: a lookup per damage event plus periodic snapshot/normalize sweeps.
//
// Why: web API handlers run on thread pool threads and mutate
// BotConfig.TeamAssignments (setTeam/teamCount/clearTeams) while the main
// thread reads it on every DamageEntity. Dictionary is not safe for
// concurrent read+write; before the TeamGate lock this raced (bucket
// corruption / KeyNotFound under Mono). This suite pins the locked behavior:
// no exceptions, no out-of-range values, snapshots always consistent.
//
// BotConfig pulls ModApi -> engine types, so this compiles the FULL mod
// source against the game DLLs; scripts/test-idempotency.sh gates it on a
// game install being present.
using System;
using System.Collections.Generic;
using System.Threading;
using BotMod.Config;

static class TeamAssignmentsConcurrencyTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static int Main()
    {
        var cfg = new BotConfig();
        cfg.BotTeamCount = 4;
        var errors = new List<string>();
        int reads = 0;
        var done = new ManualResetEvent(false);
        const int writers = 4, readers = 2, ops = 20000;
        int remaining = writers + readers;

        // Writers mimic concurrent admin surfaces (web POSTs + console).
        for (int w = 0; w < writers; w++)
        {
            int id = w;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    for (int i = 0; i < ops; i++)
                    {
                        cfg.SetTeamAssignment("Grunt_" + ((i + id) % 8), i % (cfg.BotTeamCount + 1));
                        if (i % 1000 == 0) cfg.ClearTeamAssignments();
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add("writer" + id + ": " + ex.Message); }
                finally { if (Interlocked.Decrement(ref remaining) == 0) done.Set(); }
            });
        }
        // Readers mimic the tick: per-event lookup plus snapshot enumeration.
        for (int r = 0; r < readers; r++)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    for (int i = 0; i < ops * 4; i++)
                    {
                        int t = cfg.GetTeamAssignment("Grunt_" + (i % 8));
                        if (t < 0 || t > cfg.BotTeamCount)
                        {
                            lock (errors) errors.Add("read out of range: " + t);
                            break;
                        }
                        if (i % 512 == 0)
                            foreach (KeyValuePair<string, int> kv in cfg.SnapshotTeamAssignments())
                                if (kv.Value < 0 || kv.Value > cfg.BotTeamCount)
                                {
                                    lock (errors) errors.Add("snapshot out of range: " + kv.Key);
                                    break;
                                }
                        Interlocked.Increment(ref reads);
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add("reader: " + ex.Message); }
                finally { if (Interlocked.Decrement(ref remaining) == 0) done.Set(); }
            });
        }
        // A hang here means the TeamGate lock broke (the exact regression this
        // suite pins), so a timeout must fail the run, not limp through the
        // post-storm checks while threads are still stuck.
        bool finished = done.WaitOne(60000);
        Check("hammer finished within timeout", finished);

        Check("set/clear/lookup/snapshot hammer clean (" + reads + " reads)", errors.Count == 0);
        foreach (string e in errors) Console.WriteLine("     " + e);

        // Set/clear semantics still hold after the storm.
        cfg.ClearTeamAssignments();
        cfg.SetTeamAssignment("Grunt", 3);
        Check("assign stores the team", cfg.GetTeamAssignment("Grunt") == 3);
        cfg.SetTeamAssignment("Grunt", 0);
        Check("assign 0 clears to free-for-all", cfg.GetTeamAssignment("Grunt") == 0);
        Check("clear empties the map", cfg.SnapshotTeamAssignments().Count == 0);

        Console.WriteLine(_failures == 0 ? "all team assignments concurrency tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
