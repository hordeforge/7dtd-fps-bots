using System.Text;

namespace BotMod.Web
{
    /// <summary>
    /// Makes untrusted request fields safe for single-line server log entries.
    /// POST /api/bot echoes client-supplied values ("requestId", "action",
    /// player names inside response bodies) into ModApi.Log lines; a crafted
    /// value carrying CR/LF or terminal escape characters could otherwise
    /// forge additional log lines or restructure the audit trail. Clean()
    /// replaces every control character (C0, DEL, C1) with '?' and leaves
    /// printable text, including non-ASCII, untouched.
    /// </summary>
    internal static class LogSanitizer
    {
        public static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            bool dirty = false;
            foreach (char c in value)
            {
                if (c < ' ' || (c >= '\x7f' && c <= '\x9f')) { dirty = true; break; }
            }
            if (!dirty) return value;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(c < ' ' || (c >= '\x7f' && c <= '\x9f') ? '?' : c);
            return sb.ToString();
        }
    }
}
