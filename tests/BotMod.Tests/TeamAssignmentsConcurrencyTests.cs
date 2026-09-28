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
        int okWrites = 0;
        int sawAssigned = 0;
        var done = new ManualResetEvent(false);
        const int writers = 4, readers = 2, ops = 20000;
        int remaining = writers + readers;

        // Writers mimic concurrent admin surfaces (web POSTs + console). The
        // returned result is the API's only error channel, and discarding it
        // meant a SetTeamAssignment that dropped every write reported nothing.
        for (int w = 0; w < writers; w++)
        {
            int id = w;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    for (int i = 0; i < ops; i++)
                    {
                        BotConfig.TeamAssignResult res = cfg.SetTeamAssignment("Grunt_" + ((i + id) % 8), i % (cfg.BotTeamCount + 1));
                        if (res != BotConfig.TeamAssignResult.Ok)
                        {
                            lock (errors) errors.Add("writer" + id + ": assignment refused (" + res + ")");
                            break;
                        }
                        if (Interlocked.Increment(ref okWrites) % 20000 == 0) cfg.ClearTeamAssignments();
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add("writer" + id + ": " + ex.Message); }
                finally { if (Interlocked.Decrement(ref remaining) == 0) done.Set(); }
            });
        }
        // Readers mimic the tick: per-event lookup plus snapshot enumeration.
        // The canonical accessor is the one BotManager.AreAllies actually calls
        // on every damage event, so the storm has to go through it too.
        for (int r = 0; r < readers; r++)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    for (int i = 0; i < ops * 4; i++)
                    {
                        // AreAllies passes the already-canonical key, so
                        // canonicalize the way Bot.TeamKey does before asking;
                        // the raw admin spelling carries the "_NN" suffix
                        // that GetTeamAssignment strips.
                        int t = cfg.GetTeamAssignment("Grunt_" + (i % 8));
                        if (t != cfg.GetTeamAssignmentCanonical(BotText.BaseName("Grunt_" + (i % 8))))
                        {
                            lock (errors) errors.Add("canonical lookup disagrees with the general one");
                            break;
                        }
                        if (t > 0) Interlocked.Increment(ref sawAssigned);
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

        // The storm has to have done real work. Without these, a
        // SetTeamAssignment that dropped every write, or one that stored
        // nothing a reader could ever see, reported a clean hammer.
        Check("storm accepted every assignment (" + okWrites + " writes)", okWrites == writers * ops);
        Check("readers observed live assignments (" + sawAssigned + " hits)", sawAssigned > 0);

        // Set/clear semantics still hold after the storm.
        cfg.ClearTeamAssignments();
        Check("clear empties the map", cfg.SnapshotTeamAssignments().Count == 0);
        Check("clear makes lookups answer free-for-all", cfg.GetTeamAssignment("Grunt") == 0);

        cfg.SetTeamAssignment("Grunt", 3);
        Check("assign stores the team", cfg.GetTeamAssignment("Grunt") == 3);
        Check("assign stores the team in the snapshot",
            cfg.SnapshotTeamAssignments().ContainsKey("Grunt"));
        cfg.SetTeamAssignment("Grunt", 0);
        Check("assign 0 clears to free-for-all", cfg.GetTeamAssignment("Grunt") == 0);
        // A lookup of 0 cannot tell "removed" from "stored as 0", so assert
        // the key is gone: leaving it behind would grow the map written whole
        // into botmod.json on every assignment.
        Check("assign 0 removes the key rather than storing 0",
            !cfg.SnapshotTeamAssignments().ContainsKey("Grunt"));
        Check("clear empties the map", cfg.SnapshotTeamAssignments().Count == 0);

        // The refusal results are the API's only error channel and had no
        // coverage: every caller must report anything but Ok.
        Check("empty and null names are refused",
            cfg.SetTeamAssignment("", 1) == BotConfig.TeamAssignResult.NoName
            && cfg.SetTeamAssignment(null, 1) == BotConfig.TeamAssignResult.NoName
            && cfg.SnapshotTeamAssignments().Count == 0);
        Check("an over-long name is refused",
            cfg.SetTeamAssignment(new string('g', 65), 1) == BotConfig.TeamAssignResult.NameTooLong
            && cfg.SnapshotTeamAssignments().Count == 0);
        Check("a name at the limit is accepted", cfg.SetTeamAssignment(new string('g', 64), 1) == BotConfig.TeamAssignResult.Ok);
        // Before the cap is filled: at capacity this is AtCapacity, not Ok.
        Check("a full spawned name lands on the same canonical key",
            cfg.SetTeamAssignment("[Bot] K\u00edra_42", 2) == BotConfig.TeamAssignResult.Ok
            && cfg.GetTeamAssignment("K\u00edra") == 2
            && cfg.GetTeamAssignmentCanonical("K\u00edra") == 2
            && cfg.SnapshotTeamAssignments().ContainsKey("K\u00edra"));
        cfg.ClearTeamAssignments();
        for (int i = 0; i < 300; i++) cfg.SetTeamAssignment("cap" + i, 1);
        Check("the map is capped",
            cfg.SnapshotTeamAssignments().Count == BotConfig.MaxTeamAssignments);
        Check("a new key past the cap is refused",
            cfg.SetTeamAssignment("one_too_many", 1) == BotConfig.TeamAssignResult.AtCapacity);
        Check("an existing key past the cap is still updatable",
            cfg.SetTeamAssignment("cap0", 2) == BotConfig.TeamAssignResult.Ok && cfg.GetTeamAssignment("cap0") == 2);
        Check("a full spawned name lands on the same canonical key",
            cfg.SetTeamAssignment("[Bot] K\u00edra_42", 2) == BotConfig.TeamAssignResult.Ok
            && cfg.GetTeamAssignment("K\u00edra") == 2
            && cfg.SnapshotTeamAssignments().ContainsKey("K\u00edra"));

        Console.WriteLine(_failures == 0 ? "all team assignments concurrency tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
