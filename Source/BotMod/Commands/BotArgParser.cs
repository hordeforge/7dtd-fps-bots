using System;
using System.Collections.Generic;
using System.Globalization;

namespace BotMod.Commands
{
    /// <summary>Strict parser for the positional tail shared by `bot spawn`
    /// and `bot player`: an optional count, optional x z coordinates (spawn
    /// only), and a trailing weapon id ("gun..." prefix or "mixed").
    ///
    /// Grammar (tokens after the subcommand):
    ///   spawn:  [] | [count] | [x z] | [count x z], each + optional [weapon]
    ///   player: [] | [count] | [weapon] | [count weapon]  (after <nameOrId>)
    ///
    /// The previous scanner classified tokens independently and silently
    /// dropped whatever it could not place: "bot spawn 163 818" ate 163 as a
    /// count (spawning up to MaxSpawn bots) and ignored 818, so the documented
    /// coordinate form spawned at a random position instead; "bot spawn 2 abc"
    /// just spawned 2 bots. Every leftover token is now a named usage error,
    /// and with exactly two numeric tokens they are coordinates (count stays
    /// 1), matching `bot spawn [count] [x z]`.
    ///
    /// Numbers parse in the invariant culture: coordinates are typed
    /// dot-decimal regardless of host locale ("1200.5", not "1200,5").
    ///
    /// Pure BCL (no engine types): headless tests exercise it via
    /// scripts/test-idempotency.sh.
    /// </summary>
    public static class BotArgParser
    {
        public const int MinSpawnCount = 1;
        public const int MaxSpawnCount = 16;

        public const string SpawnUsage =
            "Usage: bot spawn [count] [x z] [weapon]  e.g. bot spawn 4 / bot spawn -1200.5 300 / bot spawn 2 gunMGT1AK47";
        public const string PlayerUsage =
            "Usage: bot player <nameOrId> [count] [weapon]  e.g. bot player Kira / bot player 171 3 gunShotgunT1DoubleBarrel";

        /// <summary>Weapon ids are game item names starting with "gun" (case
        /// insensitive) or the literal "mixed" (= random pick from LoadoutPool).
        /// Anything else is not treated as a weapon token.</summary>
        public static bool LooksLikeWeapon(string token)
        {
            return token != null &&
                (token.Equals("mixed", StringComparison.OrdinalIgnoreCase) ||
                 token.StartsWith("gun", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Parses "[count] [x z] [weapon]" for `bot spawn`.
        /// Returns false with a user-facing error naming the offending token.</summary>
        public static bool TryParseSpawn(IReadOnlyList<string> args, int start,
            out int count, out float x, out float z, out bool hasPos, out string weapon, out string error)
        {
            hasPos = false; x = 0f; z = 0f;
            if (!ParseTail(args, start, true, out count, out x, out z, out hasPos, out weapon, out error))
            {
                error = error + "\n  " + SpawnUsage;
                return false;
            }
            return true;
        }

        /// <summary>Parses "[count] [weapon]" after the player identifier of
        /// `bot player`. Coordinates are not part of this grammar.</summary>
        public static bool TryParsePlayer(IReadOnlyList<string> args, int start,
            out int count, out string weapon, out string error)
        {
            if (!ParseTail(args, start, false, out count, out _, out _, out _, out weapon, out error))
            {
                error = error + "\n  " + PlayerUsage;
                return false;
            }
            return true;
        }

        static bool ParseTail(IReadOnlyList<string> args, int start, bool allowCoords,
            out int count, out float x, out float z, out bool hasPos, out string weapon, out string error)
        {
            count = MinSpawnCount; x = 0f; z = 0f; hasPos = false; weapon = null; error = null;

            // Trailing weapon token first so it cannot be mistaken for data.
            int end = args.Count;
            if (end > start && LooksLikeWeapon(args[end - 1])) { weapon = args[end - 1]; end--; }

            // A start past the end means "no tail tokens" (same as mid == 0),
            // not a negative span that would fall into the coordinate branch
            // and index out of range.
            int mid = Math.Max(0, end - start);
            if (mid == 0) return true;
            if (!allowCoords && mid > 1)
            {
                error = Unrecognized(args[start + 1]); return false;
            }
            if (mid == 1)
            {
                string t = args[start];
                if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c))
                {
                    error = Unrecognized(t); return false;
                }
                count = ClampCount(c); return true;
            }
            if (!allowCoords || mid > 3)
            {
                error = mid > 3 ? "Too many arguments, starting at '" + args[start + 3] + "'." : Unrecognized(args[start]);
                return false;
            }
            // Two or three remaining tokens: [x z] or [count x z].
            int coordStart = mid == 3 ? start + 1 : start;
            if (mid == 3 && !int.TryParse(args[start], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                error = Unrecognized(args[start]); return false;
            }
            if (!TryCoord(args[coordStart], out x)) { error = Unrecognized(args[coordStart]); return false; }
            if (!TryCoord(args[coordStart + 1], out z)) { error = Unrecognized(args[coordStart + 1]); return false; }
            if (mid == 3) count = ClampCount(n);
            hasPos = true;
            return true;
        }

        static string Unrecognized(string token)
        {
            return "Unrecognized argument '" + token + "'.";
        }

        static bool TryCoord(string token, out float v)
        {
            // Reject non-finite spellings: mono's TryParse accepts "NaN" and
            // "Infinity" as floats (desktop .NET does not), and a bot placed
            // at a non-finite coordinate is unusable downstream.
            return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                && !float.IsNaN(v) && !float.IsInfinity(v);
        }

        static int ClampCount(int c)
        {
            return Math.Max(MinSpawnCount, Math.Min(MaxSpawnCount, c));
        }
    }
}
