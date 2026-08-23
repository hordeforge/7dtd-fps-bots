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
    /// </summary>
    internal static class LogSanitizer
    {
        // U+200B-U+200F zero-width space/joiners + LRM/RLM marks,
        // U+202A-U+202E embedding/override bidi controls,
        // U+2060-U+2064 word joiner + invisible operators,
        // U+FEFF zero-width no-break space / byte-order mark.
        static bool IsInvisibleFormat(char c)
        {
            return (c >= '\u200b' && c <= '\u200f')
                || (c >= '\u202a' && c <= '\u202e')
                || (c >= '\u2060' && c <= '\u2064')
                || c == '\ufeff';
        }

        static bool NeedsClean(char c)
        {
            return c < ' ' || (c >= '\x7f' && c <= '\x9f') || IsInvisibleFormat(c);
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
