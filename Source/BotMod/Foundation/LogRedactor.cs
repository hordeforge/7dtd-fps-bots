using System.Text;

namespace BotMod.Foundation
{
    /// <summary>
    /// Replaces personal-data values in a response body before that body is
    /// echoed into a server log line. LogSanitizer answers "can this value
    /// forge a log line"; this answers "does this value identify a person",
    /// which is a different question: the audit line
    /// <c>web api action=... ok in Nms &lt;body&gt;</c> used to carry
    /// spawnNear's <c>"player"</c> field, the online player's name, into a file
    /// operators keep and ship to log aggregators. The client still gets the
    /// name in its response; only the log copy is redacted.
    ///
    /// Redaction is by JSON key, not by value, so it does not depend on
    /// recognizing a name. The outcome fields of the same body (spawned,
    /// found, entityId) stay, so the line still records what the action did.
    /// Entity id is the reference every other log line already uses for a
    /// player (see the synthetic-auth audit line in Patches/BotPatches.cs), so
    /// dropping the name costs no correlation.
    ///
    /// Scans lexically rather than parsing: the bodies are produced by
    /// WebApi.RespondJson, and a scrub that threw or mis-tokenized would put
    /// the name back into the log, so every path returns a usable string.
    /// </summary>
    internal static class LogRedactor
    {
        /// <summary>What a redacted value is replaced with. Reads as a value,
        /// not as a deleted field, so the log shows the field existed.</summary>
        internal const string Placeholder = "<player>";

        // Response keys whose value is a connected player's name. Kept as an
        // explicit list, not a pattern: a key only belongs here when code
        // proves the value identifies a person, and every entry needs a field
        // and a send site behind it.
        static readonly string[] PersonalKeys = { "player" };

        /// <summary>Whether a decoded JSON key names personal data.</summary>
        internal static bool IsPersonalKey(string key)
        {
            for (int i = 0; i < PersonalKeys.Length; i++)
                if (string.Equals(key, PersonalKeys[i], System.StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Body with every personal-data value replaced by
        /// <see cref="Placeholder"/>. Null and empty yield empty; any input
        /// yields a string.</summary>
        public static string Redact(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var sb = new StringBuilder(json.Length);
            int i = 0;
            while (i < json.Length)
            {
                if (json[i] != '"') { sb.Append(json[i]); i++; continue; }
                int end = StringEnd(json, i);
                string raw = json.Substring(i, end - i);
                sb.Append(raw);
                i = end;
                // A string is a key only when a colon follows it; a string
                // value is left alone here and reached as a value instead.
                int colon = i;
                while (colon < json.Length && char.IsWhiteSpace(json[colon])) colon++;
                if (colon >= json.Length || json[colon] != ':') continue;
                if (!IsPersonalKey(Unquote(raw))) continue;
                int valueStart = colon + 1;
                while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;
                sb.Append(json, i, valueStart - i);
                sb.Append('"').Append(Placeholder).Append('"');
                i = ValueEnd(json, valueStart);
            }
            return sb.ToString();
        }

        /// <summary>Index just past the closing quote of the string starting at
        /// <paramref name="start"/>, skipping backslash escapes. An unterminated
        /// string ends at the end of input rather than looping.</summary>
        static int StringEnd(string s, int start)
        {
            for (int i = start + 1; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return i + 1;
            }
            return s.Length;
        }

        /// <summary>Index just past the value token at <paramref name="start"/>:
        /// a string, a bracketed object or array (nesting and string contents
        /// aware), or a bare literal up to the next separator.</summary>
        static int ValueEnd(string s, int start)
        {
            if (start >= s.Length) return s.Length;
            char c = s[start];
            if (c == '"') return StringEnd(s, start);
            if (c == '{' || c == '[')
            {
                int depth = 0;
                for (int i = start; i < s.Length; i++)
                {
                    if (s[i] == '"') { i = StringEnd(s, i) - 1; continue; }
                    if (s[i] == '{' || s[i] == '[') depth++;
                    else if (s[i] == '}' || s[i] == ']') { depth--; if (depth == 0) return i + 1; }
                }
                return s.Length;
            }
            int j = start;
            while (j < s.Length && s[j] != ',' && s[j] != '}' && s[j] != ']' && !char.IsWhiteSpace(s[j])) j++;
            return j;
        }

        /// <summary>Decode a JSON string token (quotes included) to its text
        /// form, so a key written with an escape is compared by what it means.
        /// An unpaired \u escape yields U+FFFD rather than throwing.</summary>
        static string Unquote(string token)
        {
            if (token.Length < 2) return token;
            var sb = new StringBuilder(token.Length - 2);
            for (int i = 1; i < token.Length - 1; i++)
            {
                char c = token[i];
                if (c != '\\') { sb.Append(c); continue; }
                i++;
                if (i >= token.Length - 1) { sb.Append('\\'); break; }
                char e = token[i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < token.Length - 1
                            && int.TryParse(token.Substring(i + 1, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out int cp))
                        { sb.Append((char)cp); i += 4; }
                        else sb.Append('�');
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }
    }
}
