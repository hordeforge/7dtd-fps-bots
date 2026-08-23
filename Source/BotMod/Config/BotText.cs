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

        /// <summary>Base bot name: strip the "[Bot] " tag and the _NN suffix,
        /// NFC-canonicalized. Spawned names look like "[Bot] Grunt_42" ->
        /// "Grunt"; this is the identity key shared by team assignments and
        /// the character table.</summary>
        public static string BaseName(string name)
        {
            string n = Canon(name);
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
