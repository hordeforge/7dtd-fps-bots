using System;
using System.Text;

namespace BotMod.Foundation
{
    /// <summary>
    /// Canonical text handling for identity-bearing names (bot names, player
    /// lookups, team-assignment keys). One normalization policy for every
    /// surface that stores or compares such text: NFC on both sides of a
    /// comparison and at ingestion into any stored key, because the same
    /// visible name arrives in different byte forms depending on origin
    /// (Steam/game names are usually NFC; hand-edited JSON or macOS-sourced
    /// input is often NFD), and byte-level or case-only folding cannot bridge
    /// "Kíra" vs "Kira" + combining acute.
    ///
    /// Case-insensitive matching is ordinal (InvariantCulture-based simple
    /// folding), never host-culture ToLower: under a tr-TR server locale
    /// "I".ToLower() yields dotless "ı" and every lookup containing an I
    /// would silently miss.
    ///
    /// Pure BCL: compiled and unit-tested headless by scripts/test-idempotency.sh.
    /// </summary>
    public static class BotText
    {
        /// <summary>Canonical NFC form of s. Empty/null pass through as "".</summary>
        public static string Canon(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.IsNormalized(NormalizationForm.FormC) ? s : s.Normalize(NormalizationForm.FormC);
        }

        /// <summary>Length of s in characters (Unicode scalar values), not in
        /// UTF-16 code units: string.Length counts a surrogate pair twice, so a
        /// key of 100 emoji measures 200 and is rejected by a 128 limit meant
        /// for 128 characters. Unpaired surrogates cannot be decoded to a
        /// scalar and count as one each, so a malformed string still has a
        /// length. For every limit expressed in characters, this is the unit;
        /// the two differ only above the BMP.</summary>
        public static int CharCount(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
                n++;
            }
            return n;
        }

        // Control characters (C0, DEL, C1) and invisible formatting characters
        // (zero-width + LRM/RLM, bidi embedding/override controls, line and
        // paragraph separators, word joiner + invisible operators, BOM,
        // variation selectors): none of them carry meaning in a stored
        // identifier, but a value pasted from a web page can carry them
        // silently, and a key like "Grunt" + U+200B never equals "Grunt", so
        // the assignment it stores would silently never apply. U+2028/U+2029
        // are the two that survive char.IsControl (they are separators, not
        // controls) while still ending a line in log and JSON consumers, so a
        // requestId carrying one would still be able to forge a second log
        // line. Single source of truth: LogSanitizer.Clean delegates here for
        // its log-line scrub of the same ranges.
        internal static bool IsInvisible(char c)
        {
            return c < ' ' || (c >= '\x7f' && c <= '\x9f')
                || (c >= '\u200b' && c <= '\u200f')   // zero-width + LRM/RLM (+U+200D ZWJ)
                || (c >= '\u202a' && c <= '\u202e')   // bidi embedding/override controls
                || c == '\u2028' || c == '\u2029'     // line/paragraph separators
                || (c >= '\u2060' && c <= '\u2064')   // word joiner + invisible operators
                || c == '\ufeff'                      // BOM / zero-width no-break space
                || (c >= '\ufe00' && c <= '\ufe0f');  // variation selectors
        }

        /// <summary>s without control and invisible-format characters. Strips
        /// rather than substitutes so paste noise collapses onto the intended
        /// name instead of forming a near-miss variant.</summary>
        public static string WithoutInvisible(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool dirty = false;
            foreach (char c in s)
                if (IsInvisible(c)) { dirty = true; break; }
            if (!dirty) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (!IsInvisible(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Stored-key form of arbitrary input text: control and
        /// invisible-format characters removed, then NFC. Every surface that
        /// writes or queries a name-keyed map (team assignments, character
        /// table) goes through this so one spelling cannot fork into near-miss
        /// variants.
        ///
        /// Strip first, normalize second, and the result is a fixed point:
        /// NFC never introduces a character this method strips, so a second
        /// pass has nothing left to do. Canon first and strip second does not
        /// have that property, and the difference is observable: an invisible
        /// character sitting between two combining marks (a pasted name
        /// carrying a soft hyphen, a ZWNJ between an accented vowel and a
        /// macron) blocks their composition, so canon-then-strip left two
        /// uncombined marks in the key while a second call over the same name
        /// composed them into one. The key a team assignment was stored under
        /// then never matched the key derived from that name at lookup time.
        /// Deliberately NOT used by NameMatches: player names may legitimately
        /// contain U+200D inside emoji sequences, and matching must see them.</summary>
        public static string IdentityKey(string s)
        {
            return Canon(WithoutInvisible(s));
        }

        /// <summary>Base bot name: strip the "[Bot] " tag and the _NN suffix,
        /// canonicalized through IdentityKey (NFC, no control/invisible
        /// characters). Spawned names look like "[Bot] Grunt_42" ->
        /// "Grunt"; this is the identity key shared by team assignments and
        /// the character table. The underscore is reserved in the name
        /// grammar, so a configured name carrying one keys on what precedes
        /// it (BotSpawner's default BotNames avoid it, "TankJr" not
        /// "Tank_Jr").
        ///
        /// IndexOf rather than Split('_')[0]: this runs on the per-damage-event
        /// ally check and on every character lookup, and Split allocates the
        /// whole string array behind the discarded tail.</summary>
        public static string BaseName(string name)
        {
            string n = IdentityKey(name);
            if (n.StartsWith("[Bot] ", StringComparison.OrdinalIgnoreCase)) n = n.Substring(6);
            int underscore = n.IndexOf('_');
            return underscore < 0 ? n : n.Substring(0, underscore);
        }

        /// <summary>Case-insensitive, normalization-insensitive name match:
        /// true when the NFC forms match ordinally ignoring ASCII/Unicode
        /// simple case, as exact or substring hit. Both sides are user- or
        /// admin-supplied, so neither can be assumed to be in one Unicode
        /// normalization form.</summary>
        public static bool NameMatches(string name, string ident)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(ident)) return false;
            return Canon(name).IndexOf(Canon(ident), StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
