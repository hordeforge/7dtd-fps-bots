using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace BotMod.Foundation
{
    /// <summary>One spawnpoint coordinate triple from a world spawnpoints.xml.</summary>
    public struct SpawnPoint
    {
        public float X;
        public float Y;
        public float Z;
    }

    /// <summary>
    /// Reader for the DM spawnpoint list of a world file (spawnpoints.xml,
    /// consumed by BotSpawner). Engine-free and side-effect free: it takes the
    /// document text and answers coordinates, so the untrusted half of the
    /// path (a shared, semi-trusted world file) is testable and fuzzable
    /// without a running server.
    ///
    /// The parse is hardened the same way on every entry: DTD processing off
    /// and the resolver null, so a crafted document cannot expand external
    /// entities (file:// read, http:// SSRF) or blow up on entity expansion.
    /// The format has no DTD.
    ///
    /// Coordinates are invariant (the file is machine data with dot decimals;
    /// a comma-decimal host locale would otherwise reject every spawnpoint and
    /// silently drop DM spawn selection) and must be finite: "NaN" and
    /// "Infinity" parse as floats, and a spawnpoint at NaN would put a bot at
    /// an undefined position, which propagates into every later combat tick
    /// instead of being one rejected line. A document that does not parse
    /// throws, which the caller reports; a document that parses but holds no
    /// usable spawnpoint answers an empty list.
    /// </summary>
    public static class SpawnPointXml
    {
        /// <summary>Every usable spawnpoint in <paramref name="xmlText"/>, in
        /// document order.</summary>
        public static List<SpawnPoint> Parse(string xmlText)
        {
            var list = new List<SpawnPoint>();
            if (string.IsNullOrEmpty(xmlText)) return list;

            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            var doc = new XmlDocument { XmlResolver = null };
            using (var textReader = new StringReader(xmlText))
            using (var reader = XmlReader.Create(textReader, settings))
                doc.Load(reader);

            foreach (XmlNode n in doc.SelectNodes("//spawnpoint"))
            {
                var posAttr = n.Attributes["position"];
                if (posAttr == null) continue;
                string[] parts = posAttr.Value.Split(',');
                if (parts.Length < 3) continue;
                if (!TryCoordinate(parts[0], out float x)) continue;
                if (!TryCoordinate(parts[1], out float y)) continue;
                if (!TryCoordinate(parts[2], out float z)) continue;
                list.Add(new SpawnPoint { X = x, Y = y, Z = z });
            }
            return list;
        }

        /// <summary>One invariant float, rejecting the non-finite spellings
        /// float.TryParse accepts.</summary>
        static bool TryCoordinate(string text, out float value)
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = 0f;
                return false;
            }
            return true;
        }
    }
}
