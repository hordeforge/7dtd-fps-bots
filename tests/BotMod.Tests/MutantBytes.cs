// MutantBytes: the byte-level and scalar generators the JSON config fuzzers
// share, so one fuzzer's mutation schedule cannot drift from another's. Kept in
// its own file because each suite compiles an explicit source list (see
// scripts/test-idempotency.sh) rather than a directory; needs Newtonsoft.Json
// for the container shapes in ExtremeValues.
using System;
using Newtonsoft.Json.Linq;

static class MutantBytes
{
    /// <summary>Truncate, flip, delete or pad a seed document, so every
    /// structure-aware fuzzer starts from the same distribution.</summary>
    internal static byte[] Mutate(byte[] src, Random rng)
    {
        byte[] m = (byte[])src.Clone();
        switch (rng.Next(4))
        {
            case 0: // truncate
                Array.Resize(ref m, rng.Next(0, m.Length));
                return m;
            case 1: // flip bytes
                for (int n = rng.Next(1, 8); n > 0 && m.Length > 0; n--)
                    m[rng.Next(m.Length)] = (byte)rng.Next(256);
                return m;
            case 2: // delete a chunk
                if (m.Length < 4) return m;
                int cutAt = rng.Next(m.Length - 1);
                int cutLen = Math.Min(rng.Next(1, 40), m.Length - cutAt);
                byte[] shorter = new byte[m.Length - cutLen];
                Array.Copy(m, shorter, cutAt);
                Array.Copy(m, cutAt + cutLen, shorter, cutAt, m.Length - cutAt - cutLen);
                return shorter;
            default: // insert junk
                int at = rng.Next(m.Length + 1);
                int junkLen = rng.Next(1, 20);
                byte[] longer = new byte[m.Length + junkLen];
                Array.Copy(m, longer, at);
                for (int j = 0; j < junkLen; j++) longer[at + j] = (byte)rng.Next(256);
                Array.Copy(m, at, longer, at + junkLen, m.Length - at);
                return longer;
        }
    }

    /// <summary>Values a field setter is unlikely to see from a real operator
    /// but must not crash on: type confusion, sentinels and containers.</summary>
    internal static readonly object[] ExtremeValues =
    {
        int.MinValue, int.MaxValue, 0, -1, 999999999,
        3e38f, -3e38f, 0.5d, 1e300d,
        "not-a-number", "", true, false, null,
        new JArray { 1, 2 }, new JObject { ["nested"] = 9 }
    };
}
