using System;
using System.Text;

namespace BotMod.Config
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

        // Control characters (C0, DEL, C1) and invisible formatting characters
        // (same ranges LogSanitizer scrubs from log lines, plus variation
        // selectors): none of them carry meaning in a stored identifier, but a
        // value pasted from a web page can carry them silently, and a key like
        // "Grunt" + U+200B never equals "Grunt", so the assignment it stores
        // would silently never apply.
        internal static bool IsInvisible(char c)
        {
            return c < ' ' || (c >= '\x7f' && c <= '\x9f')
                || (c >= '\u200b' && c <= '\u200f')   // zero-width + LRM/RLM (+U+200D ZWJ)
                || (c >= '\u202a' && c <= '\u202e')   // bidi embedding/override controls
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

        /// <summary>Stored-key form of arbitrary input text: NFC, control and
        /// invisible-format characters removed. Every surface that writes or
        /// queries a name-keyed map (team assignments, character table) goes
        /// through this so one spelling cannot fork into near-miss variants.
        /// Deliberately NOT used by NameMatches: player names may legitimately
        /// contain U+200D inside emoji sequences, and matching must see them.</summary>
        public static string IdentityKey(string s)
        {
            return WithoutInvisible(Canon(s));
        }

        /// <summary>Base bot name: strip the "[Bot] " tag and the _NN suffix,
        /// canonicalized through IdentityKey (NFC, no control/invisible
        /// characters). Spawned names look like "[Bot] Grunt_42" ->
        /// "Grunt"; this is the identity key shared by team assignments and
        /// the character table.</summary>
        public static string BaseName(string name)
        {
            string n = IdentityKey(name);
            if (n.StartsWith("[Bot] ", StringComparison.OrdinalIgnoreCase)) n = n.Substring(6);
            return n.Split('_')[0];
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
