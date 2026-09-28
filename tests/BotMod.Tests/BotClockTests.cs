// BotClockTests: pins the injected-time contract every bot deadline now rides
// (Bot, BotManager, BotBrain, ModApi read BotClock instead of UnityEngine.Time).
// The two properties that matter are that an unbound clock fails loudly instead
// of reporting a time nobody chose, and that a bound clock is a pure function of
// the source a harness feeds it, so a tick schedule replays identically. A
// BotClock that quietly fell back to a real clock, or cached its first read,
// would reintroduce wall-clock time into the run with nothing to catch it.
//
// Pure BCL; compiled and run by scripts/test-idempotency.sh:
//
//   bash scripts/test-idempotency.sh botclock
using System;
using System.Text;
using BotMod.Foundation;

static class BotClockTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    /// <summary>A virtual clock stepped by the harness, mirroring the engine
    /// contract BotClock wraps: the caller advances time, and Now/Delta are
    /// pure readers of that state. The frame schedule is the input, so two runs
    /// of the same schedule are byte-identical by construction and the
    /// transcript below can be diffed.</summary>
    static float _now;
    static float _delta;

    static float ReadNow() { return _now; }
    static float ReadDelta() { return _delta; }

    static void Step(float delta) { _delta = delta; _now += delta; }

    /// <summary>One scripted run of a bot-shaped timer loop over the virtual
    /// clock. Mirrors what Bot.Tick does with the clock: every deadline is a
    /// now-relative assignment, and the frame advances by the clock's delta.</summary>
    static string Transcript(float[] schedule)
    {
        _now = 0f;
        _delta = 0f;
        var sb = new StringBuilder();
        float nextScan = 0f;
        float wanderAt = 9f;
        float moved = 0f;
        for (int frame = 0; frame < schedule.Length; frame++)
        {
            Step(schedule[frame]);
            float now = BotClock.Now;
            if (now >= nextScan)
            {
                nextScan = now + 0.35f;
                sb.Append('S').Append(nextScan.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            }
            if (now >= wanderAt)
            {
                wanderAt = now + 9f;
                sb.Append('|').Append('W').Append(wanderAt.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            }
            moved += BotClock.Delta;
            sb.Append('.').Append(moved.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    static int Main()
    {
        // 1. Unbound reads throw. A zero fallback would park every deadline in
        //    the past and read as a healthy idle bot instead of a broken clock.
        {
            bool nowThrew = false, deltaThrew = false;
            try { float now = BotClock.Now; GC.KeepAlive(now); } catch (InvalidOperationException) { nowThrew = true; }
            try { float delta = BotClock.Delta; GC.KeepAlive(delta); } catch (InvalidOperationException) { deltaThrew = true; }
            Check("an unbound Now throws", nowThrew);
            Check("an unbound Delta throws", deltaThrew);
        }

        // 2. A null source is rejected at bind time, not at the first read.
        {
            bool nullNow = false, nullDelta = false;
            try { BotClock.Bind(null, () => 0f); } catch (ArgumentNullException) { nullNow = true; }
            try { BotClock.Bind(() => 0f, null); } catch (ArgumentNullException) { nullDelta = true; }
            Check("Bind rejects a null now source", nullNow);
            Check("Bind rejects a null delta source", nullDelta);
        }

        // 3. A bound clock reports exactly what the source reports, on every
        //    read: no sampling, no caching of the first value.
        {
            int nowCalls = 0, deltaCalls = 0;
            BotClock.Bind(
                () => { nowCalls++; return nowCalls * 0.5f; },
                () => { deltaCalls++; return 0.05f; });
            float a = BotClock.Now, b = BotClock.Now;
            Check("Now reads through on every call",
                nowCalls == 2 && a == 0.5f && b == 1f);
            Check("Delta reads through on every call",
                BotClock.Delta == 0.05f && BotClock.Delta == 0.05f && deltaCalls == 2);
        }

        // 4. The replay property the harness depends on: the same frame
        //    schedule produces the same transcript twice, and a different
        //    schedule (a perturbed delta) does not.
        {
            var schedule = new float[600];
            for (int i = 0; i < schedule.Length; i++) schedule[i] = 0.05f;
            var jittered = new float[600];
            for (int i = 0; i < jittered.Length; i++) jittered[i] = i % 7 == 0 ? 0.051f : 0.05f;

            BotClock.Bind(ReadNow, ReadDelta);
            string first = Transcript(schedule);
            string second = Transcript(schedule);
            Check("the same tick schedule replays byte-for-byte", first == second);
            Check("a perturbed delta diverges the transcript", Transcript(jittered) != first);
            Check("the scripted run actually exercised the timers", first.Length > 100);
        }

        if (_failures == 0) { Console.WriteLine("all bot clock tests passed"); return 0; }
        Console.WriteLine(_failures + " bot clock test(s) FAILED");
        return 1;
    }
}
