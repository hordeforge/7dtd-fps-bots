using System.Text;

namespace BotMod.Config
{
    /// <summary>
    /// Makes untrusted request fields safe for single-line server log entries.
    /// POST /api/bot echoes client-supplied values ("requestId", "action",
    /// player names inside response bodies) into ModApi.Log lines; a crafted
    /// value carrying CR/LF or terminal escape characters could otherwise
    /// forge additional log lines or restructure the audit trail. Clean()
    /// replaces every control character (C0, DEL, C1) with '?' and leaves
    /// printable text, including non-ASCII, untouched.
    ///
    /// Invisible formatting characters are scrubbed the same way: bidi
    /// controls and zero-width characters do not break line-based log tooling,
    /// but they reorder or hide text when the audit trail is read in a
    /// terminal ("admin\u202e..."), so a request-supplied value must not be
    /// able to carry them into the log verbatim. Substitution keeps the string
    /// length stable.
    ///
    /// The invisible-character table itself lives in BotText.IsInvisible (one
    /// definition shared with stored-key canonicalization, so the two cannot
    /// drift); Clean adds only the control-character test on top.
    /// </summary>
    internal static class LogSanitizer
    {
        // char.IsControl spans exactly C0 + DEL + C1 ('\0'..'\x1f', '\x7f'..'\x9f').
        static bool NeedsClean(char c)
        {
            return char.IsControl(c) || BotText.IsInvisible(c);
        }

        public static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            bool dirty = false;
            foreach (char c in value)
            {
                if (NeedsClean(c)) { dirty = true; break; }
            }
            if (!dirty) return value;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(NeedsClean(c) ? '?' : c);
            return sb.ToString();
        }
    }
}
