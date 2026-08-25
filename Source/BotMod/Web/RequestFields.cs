using System;
using System.Collections.Generic;
using System.Globalization;

namespace BotMod.Web
{
    /// <summary>Triage result of one POST /api/bot body-field read.</summary>
    internal enum FieldRead
    {
        /// <summary>Key missing or JSON null: caller applies its documented default.</summary>
        Absent,
        /// <summary>Value parsed successfully.</summary>
        Ok,
        /// <summary>Value present but not convertible to the field's type: the
        /// caller rejects the request with a named 400 instead of guessing.</summary>
        Invalid,
    }

    /// <summary>
    /// Typed readers for the untrusted POST /api/bot body (a dictionary the
    /// stock webserver deserialized from arbitrary JSON). One definition of
    /// absent vs present-but-garbage, so every action can reject malformed
    /// values with a named error code instead of silently substituting a
    /// default that executes something the client did not ask for.
    ///
    /// Numbers must be JSON numbers or invariant digit text; booleans must be
    /// JSON true/false or case-insensitive "true"/"false". Anything else,
    /// including an empty string, is <see cref="FieldRead.Invalid"/>. Readers
    /// are pure and never throw on any input shape; range clamping stays with
    /// the callers (shared with the console command's setters).
    /// </summary>
    internal static class RequestFields
    {
        /// <summary>Optional integer field. Absent leaves <paramref name="value"/>
        /// at 0; the caller supplies its own default for that case. Invariant
        /// parse: JSON numbers are protocol tokens, not host-locale text.</summary>
        public static FieldRead OptInt(IDictionary<string, object> body, string key, out int value)
        {
            string raw = Raw(body, key);
            if (raw == null)
            {
                value = 0;
                return FieldRead.Absent;
            }
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                value = 0;
                return FieldRead.Invalid;
            }
            return FieldRead.Ok;
        }

        /// <summary>Boolean flag field whose absence is itself invalid for the
        /// toggles that require it (the caller maps Absent and Invalid to the
        /// same named error). Accepts JSON true/false and the equivalent
        /// case-insensitive text forms, nothing else.</summary>
        public static FieldRead RequireBool(IDictionary<string, object> body, string key, out bool value)
        {
            string raw = Raw(body, key);
            if (raw == null)
            {
                value = false;
                return FieldRead.Absent;
            }
            // bool.TryParse is culture-independent and case-insensitive, and
            // Convert.ToString(bool, InvariantCulture) yields "True"/"False",
            // so the JSON literal and its text form share this one path.
            if (!bool.TryParse(raw, out value))
            {
                value = false;
                return FieldRead.Invalid;
            }
            return FieldRead.Ok;
        }

        /// <summary>Field as invariant text, or null when the key is missing or
        /// JSON null. Non-string scalars convert losslessly enough for the
        /// typed parsers above; fractional doubles fail the integer parse and
        /// land in Invalid, which is the point.</summary>
        static string Raw(IDictionary<string, object> body, string key)
        {
            if (body == null || !body.TryGetValue(key, out object v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}
