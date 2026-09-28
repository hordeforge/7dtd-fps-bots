using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BotMod.Foundation;

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

        /// <summary>Free-text field, or null when absent. Like every reader
        /// here, the conversion is invariant: a client that sends a JSON number
        /// where a name belongs gets the token it sent, never the host
        /// culture's spelling of it (a de-DE server would otherwise hand the
        /// ledger key "1234,5" for 1234.5, and the same request re-sent as
        /// "1234.5" would miss the entry it claims).</summary>
        public static string OptString(IDictionary<string, object> body, string key)
        {
            return Raw(body, key);
        }

        /// <summary>Canonical text of the whole request body, used to bind an
        /// idempotency key to the operation it was issued for. Keys are sorted
        /// ordinal so field order in the JSON text does not change the result
        /// (a byte-for-byte hash of the raw body would reject an otherwise
        /// identical retry that re-serialized the fields in another order);
        /// "requestId" itself is excluded because it is the ledger key, not
        /// part of the operation. Values go through the same invariant
        /// conversion as the readers above, so a JSON number and its digit text
        /// fingerprint alike (they mean the same field value to every action).
        /// A retry that changes any field yields a different fingerprint, which
        /// the ledger reports as a key reuse instead of replaying the previous
        /// response. Never throws; nested values render as their invariant
        /// text, which no current action accepts, so a nested body is rejected
        /// as INVALID_* by the action itself before it can matter.
        ///
        /// Field names and text values are NFC-canonicalized (BotText.Canon)
        /// before they go into the text, because the ledger compares
        /// fingerprints ordinally and the rest of the mod compares names in NFC
        /// (BotText.NameMatches, BotCharacterDB, team assignments). Without it
        /// one retry pair could disagree with itself: the same spawnNear for
        /// "Kíra" re-sent by a client whose editor stored the name decomposed
        /// ("K" + U+0301) produced a different fingerprint, so the ledger
        /// answered 409 REQUEST_ID_REUSED and the client retried a spawn the
        /// server had already run. Case still separates: Canon is
        /// normalization, not folding, so "on" and "On" remain different
        /// bodies.</summary>
        public static string Fingerprint(IDictionary<string, object> body, string excludeKey)
        {
            if (body == null) return "";
            var keys = new List<string>();
            foreach (var kv in body)
            {
                if (kv.Key == excludeKey) continue;
                keys.Add(kv.Key);
            }
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                // Length-prefixed pairs, not "key=value" lines: a value may
                // contain the separator, so {a:"b",c:"d"} and {a:"b\nc=d"}
                // render the same text and would share a fingerprint. The
                // ledger treats a matching fingerprint as the same request and
                // replays the recorded response, so a collision here answers
                // one operation with another operation's result.
                Append(sb, BotText.Canon(keys[i]));
                sb.Append('=');
                Append(sb, BotText.Canon(Raw(body, keys[i]) ?? "null"));
            }
            return sb.ToString();
        }

        static void Append(StringBuilder sb, string field)
        {
            sb.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field);
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
