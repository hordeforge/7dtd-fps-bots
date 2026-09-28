using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BotMod.Foundation;

namespace BotMod.Config
{
    // Direct port of Q3's 80 characteristic slots (chars.h). Six of them
    // drive behavior: AimAccuracy and AimSkill (Bot), Aggression and
    // SelfPreservation (Bot), Camper (BotBrain), and the WantsToCamp gate
    // (Bot). The rest are carried so character files match the Q3 layout.
    public sealed class BotCharacter
    {
        // 0 name, 1 gender, 2 attack_skill, 3 weaponweights, 4 view_factor, 5 view_maxchange,
        // 6 reactiontime, 7 aim_accuracy, 8..15 per-weapon accuracy, 16 aim_skill, 17..20 per-weapon skill,
        // 21..35 chat, 36 croucher, 37 jumper, 38 weaponjumping, 39 grapple_user, 40 itemweights,
        // 41 aggression, 42 selfpreservation, 43 vengefulness, 44 camper, 45 easy_fragger, 46 alertness, 47 firethrottle, ...
        public string Name { get; set; } = "Grunt";
        public float AttackSkill { get; set; } = 0.7f;
        public float ViewFactor { get; set; } = 0.35f;
        public float ViewMaxChange { get; set; } = 600f;
        public float ReactionTime { get; set; } = 0.35f;
        public float AimAccuracy { get; set; } = 0.75f;
        public float AimSkill { get; set; } = 0.75f;
        public Dictionary<string,float> AimAccuracyWeapon { get; set; }
        public Dictionary<string,float> AimSkillWeapon { get; set; }
        public float Croucher { get; set; } = 0.2f;
        public float Jumper { get; set; } = 0.5f;
        public float Walker { get; set; } = 0.2f;
        public float WeaponJumping { get; set; } = 0f;
        public float Aggression { get; set; } = 0.6f;
        public float SelfPreservation { get; set; } = 0.5f;
        public float Vengefulness { get; set; } = 0.6f;
        public float Camper { get; set; } = 0.2f;
        public float EasyFragger { get; set; } = 0.3f;
        public float Alertness { get; set; } = 0.5f;
        public float FireThrottle { get; set; } = 0.7f;
        public float ChatInsult { get; set; } = 0.3f;
        public bool ChallengeAim { get; set; } = false; // Q3 bot_challenge cvar parity, carried for the characters.json layout; no code path reads it

        public static BotCharacter Defaults(string name) => new BotCharacter { Name = name };

        /// <summary>Clamp every characteristic into its documented range,
        /// replacing non-finite values with the built-in defaults. Run on
        /// every entry deserialized from characters.json: that file is
        /// operator-authored hand-edited text, and Newtonsoft parses bare
        /// NaN/Infinity/-Infinity number literals straight into float
        /// properties. These floats feed the neural observation vector, the
        /// per-engagement aim-bias window ((1-AimAccuracy)*0.45 rotated into
        /// the aim direction each shot) and the camp/retreat gates, so one
        /// NaN literal would poison the whole forward pass (every
        /// sigmoid/tanh of NaN stays NaN, so the fire gate silently holds
        /// fire forever) and rotate aim by NaN. Same boundary convention as
        /// BotConfig.Normalize.</summary>
        public void Normalize()
        {
            Name = Name ?? "Grunt";
            // Probability/skill traits: [0, 1]
            AttackSkill = Clamp01(AttackSkill, 0.7f);
            AimAccuracy = Clamp01(AimAccuracy, 0.75f);
            AimSkill = Clamp01(AimSkill, 0.75f);
            Croucher = Clamp01(Croucher, 0.2f);
            Jumper = Clamp01(Jumper, 0.5f);
            Walker = Clamp01(Walker, 0.2f);
            WeaponJumping = Clamp01(WeaponJumping, 0f);
            Aggression = Clamp01(Aggression, 0.6f);
            SelfPreservation = Clamp01(SelfPreservation, 0.5f);
            Vengefulness = Clamp01(Vengefulness, 0.6f);
            Camper = Clamp01(Camper, 0.2f);
            EasyFragger = Clamp01(EasyFragger, 0.3f);
            Alertness = Clamp01(Alertness, 0.5f);
            FireThrottle = Clamp01(FireThrottle, 0.7f);
            ChatInsult = Clamp01(ChatInsult, 0.3f);
            ViewFactor = Clamp01(ViewFactor, 0.35f);
            // Magnitude traits: finite and positive
            ViewMaxChange = FinitePositive(ViewMaxChange, 600f);
            ReactionTime = FinitePositive(ReactionTime, 0.35f);
            if (AimAccuracyWeapon != null)
                foreach (string k in new List<string>(AimAccuracyWeapon.Keys))
                    AimAccuracyWeapon[k] = Clamp01(AimAccuracyWeapon[k], 0.75f);
            if (AimSkillWeapon != null)
                foreach (string k in new List<string>(AimSkillWeapon.Keys))
                    AimSkillWeapon[k] = Clamp01(AimSkillWeapon[k], 0.75f);
        }

        /// <summary>v clamped to [0,1]; NaN/Infinite v replaced by fallback.</summary>
        static float Clamp01(float v, float fallback)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return fallback;
            return Math.Max(0f, Math.Min(1f, v));
        }

        /// <summary>v clamped to > 0; NaN/Infinite/non-positive v replaced by fallback.</summary>
        static float FinitePositive(float v, float fallback)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || v <= 0f) return fallback;
            return v;
        }

        // Q3-style camp decision, consumed by BotBrain.
        // Deterministic overload: caller supplies a 0..1 roll from the bot's per-slot LCG (zdtd parity).
        public bool WantsToCamp(float healthFrac, float roll01) { return Camper > 0.45f && healthFrac > 0.55f && roll01 < Camper * 0.4f; }
    }

    // Loads config/characters.json which mirrors Q3 bots/*.c skill blocks. Fallback is defaults lerped by Difficulty.
    public static class BotCharacterDB
    {
        static Dictionary<string, BotCharacter> _characters = new Dictionary<string, BotCharacter>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Fully built character table. Load builds a private
        /// instance and publishes it with one reference store, so a concurrent
        /// ForName (the game tick reads this per bot) sees either the whole
        /// previous table or the whole new one, never a half-filled map or a
        /// character whose traits are half lerped. The field is volatile for
        /// the same reason the ledger's sink is: a plain static field lets a
        /// reader cache the old reference across the store indefinitely.</summary>
        public static Dictionary<string, BotCharacter> Characters
        {
            get { return System.Threading.Volatile.Read(ref _characters); }
            private set { System.Threading.Volatile.Write(ref _characters, value); }
        }

        public static void Load(BotConfig cfg)
        {
            // characters.json sits beside the botmod.json that is actually
            // being read, so a BOTMOD_CONFIG pointing at a mounted config
            // directory brings its characters with it. Without that the two
            // halves of the config would come from different trees and a
            // deployment could never override the character traits.
            var candidates = new List<string>();
            string configPath = BotConfig.ConfigPath();
            string configDir = string.IsNullOrEmpty(configPath) ? null : System.IO.Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(configDir)) candidates.Add(System.IO.Path.Combine(configDir, "characters.json"));
            string modDir = System.IO.Path.GetDirectoryName(typeof(BotCharacterDB).Assembly.Location) ?? ".";
            candidates.Add(System.IO.Path.Combine(modDir, "Config", "characters.json"));
            // Same second choice BotConfig.DefaultPathBesideAssembly makes for
            // botmod.json: an install that keeps its config at the mod root
            // rather than in Config/ must resolve both halves from there, or
            // the traits come from a Config/characters.json the same install
            // does not use.
            candidates.Add(System.IO.Path.Combine(modDir, "characters.json"));
            candidates.Add(System.IO.Path.Combine(".", "config", "characters.json"));
            string path = null;
            // Built privately and published once at the end: the default
            // minting and the difficulty lerp below mutate the map, and a
            // reader holding the published reference would otherwise observe
            // that half-finished table. Starting empty also means every failure
            // mode (missing, unparseable, null body) rebuilds from pristine
            // defaults; reusing the published instances would let every `bot
            // reload` with a broken file drift aim/reaction/aggression further
            // toward their clamps.
            var next = new Dictionary<string, BotCharacter>(StringComparer.OrdinalIgnoreCase);
            // Names cfg.BotNames asks for that the ingested file did not
            // carry. Reported once per load, below: a name with no block is
            // the same silent-default surface as a misspelled trait, one
            // layer up (a typo'd BotNames entry).
            var unmatched = new List<string>();
            foreach (string candidate in candidates)
            {
                if (System.IO.File.Exists(candidate)) { path = candidate; break; }
            }
            if (path == null)
            {
                BotConfig.Warn("characters.json not found (looked beside the config and under the mod dir); bots use built-in default characteristics");
            }
            else
            {
                try
                {
                    // Explicit UTF-8: characters.json is our own artifact and is
                    // written UTF-8; never depend on the platform default codepage.
                    var json = System.IO.File.ReadAllText(path, Encoding.UTF8);
                    var parsed = JsonConvert.DeserializeObject<Dictionary<string, BotCharacter>>(json);
                    if (parsed == null)
                    {
                        // Deserialization can yield a null dictionary without
                        // throwing (a hand-edited file holding bare JSON
                        // "null"): surface it like every other unreadable file
                        // instead of silently keeping whatever the previous
                        // load left behind with no signal at all.
                        BotConfig.Warn("characters.json parse failed (" + path + "): JSON body deserialized to null");
                    }
                    else
                    {
                        // Json.NET silently ignores keys that bind no property,
                        // so a typo'd trait ("Acuraccy") keeps the built-in
                        // default with no signal at all. Surface every unknown
                        // key per entry, same contract as botmod.json's
                        // top-level unknown-key warning.
                        foreach (KeyValuePair<string, string> kv in UnknownEntryKeys(json))
                            BotConfig.Warn("Unknown character trait '" + kv.Value + "' for '" + kv.Key + "' in " + path + " (typo? trait ignored, built-in default applies)");
                        // Sanitize at ingestion, then canonicalize keys
                        // (IdentityKey = NFC + no control/invisible
                        // characters): file values are operator-authored text
                        // that may carry NaN/Infinity literals or out-of-range
                        // traits; file keys may be NFD or carry paste noise.
                        // Keep one sane, canonical form on both sides of the
                        // lookup. A null entry value (hand-edited
                        // "{\"Grunt\": null}") carries no data: drop it instead
                        // of letting Normalize's dereference fail the whole
                        // file behind a generic parse warning.
                        foreach (var kv in parsed)
                        {
                            if (kv.Value == null) continue;
                            kv.Value.Normalize();
                            next[BotText.IdentityKey(kv.Key)] = kv.Value;
                        }
                    }
                }
                catch (Exception ex) { BotConfig.Warn("characters.json parse failed (" + path + "): " + ex.Message); }
            }
            // Entries the file actually carried. A file that yielded none
            // (missing, unparseable, or "{"Grunt": null}" dropping its only
            // entry) has already been reported once and defaults every name by
            // design, so it is not a per-name mismatch.
            int ingested = next.Count;
            // Ensure at least defaults for known names (BaseName is NFC, same
            // canonical form as the keys above).
            foreach (var n in cfg.BotNames)
            {
                string key = BotText.BaseName(n);
                if (next.ContainsKey(key)) continue;
                if (ingested > 0) unmatched.Add(key);
                next[key] = BotCharacter.Defaults(key);
            }
            if (unmatched.Count > 0) WarnUnmatchedNames(unmatched, path);
            // Apply difficulty lerp if characters have multiple skills (stored as skill 1 vs 5) - here we just scale by cfg.Difficulty
            float diffSkill = cfg.Difficulty / 4f;
            foreach (var kv in new List<KeyValuePair<string,BotCharacter>>(next))
            {
                var ch = kv.Value;
                // Difficulty gently overrides core aim/reaction/aggro
                ch.AimAccuracy = Math.Max(0.2f, Math.Min(1f, ch.AimAccuracy + diffSkill * 0.25f - 0.12f));
                ch.AimSkill = Math.Max(0.2f, Math.Min(1f, ch.AimSkill + diffSkill * 0.25f - 0.12f));
                ch.ReactionTime = Math.Max(0.05f, ch.ReactionTime - diffSkill * 0.25f);
                ch.Alertness = Math.Max(0.1f, Math.Min(1f, ch.Alertness + diffSkill * 0.3f - 0.15f));
                ch.Aggression = Math.Max(0f, Math.Min(1f, ch.Aggression + diffSkill * 0.2f - 0.1f));
            }
            Characters = next;
        }

        /// <summary>How many names one warning line spells out. BotNames is
        /// operator text with no upper bound, so the report names the first
        /// MaxUnmatchedNamesReported and the remainder's count, which says the
        /// file does not cover the config without letting it grow the line
        /// without limit. The names reach the log through BotText.BaseName,
        /// which strips control and invisible characters, so a hand-edited name
        /// cannot carry terminal escapes into the log.</summary>
        internal const int MaxUnmatchedNamesReported = 8;

        static void WarnUnmatchedNames(List<string> unmatched, string path)
        {
            int shown = Math.Min(unmatched.Count, MaxUnmatchedNamesReported);
            var sb = new StringBuilder();
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(unmatched[i]);
            }
            if (unmatched.Count > shown) sb.Append(", +").Append(unmatched.Count - shown).Append(" more");
            BotConfig.Warn("BotNames has " + unmatched.Count + " name(s) with no block in " + path
                + " (" + sb + "); those bots use built-in default characteristics");
        }

        /// <summary>(Entry name, unknown trait key) pairs for every character
        /// object inside <paramref name="json"/>. Comparison mirrors how
        /// JsonConvert binds members (exact first, then case-insensitive), so
        /// valid traits never false-positive. Entries whose value is not an
        /// object contribute nothing, and a body that does not re-parse yields
        /// an empty list: both are reported by the load path itself.</summary>
        internal static List<KeyValuePair<string, string>> UnknownEntryKeys(string json)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertyInfo p in typeof(BotCharacter).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                known.Add(p.Name);
            var unknown = new List<KeyValuePair<string, string>>();
            JObject root;
            try { root = JObject.Parse(json); }
            catch (Exception) { return unknown; }
            foreach (KeyValuePair<string, JToken> entry in root)
            {
                if (!(entry.Value is JObject body)) continue;
                foreach (JProperty p in body.Properties())
                    if (!known.Contains(p.Name)) unknown.Add(new KeyValuePair<string, string>(entry.Key, p.Name));
            }
            return unknown;
        }
        public static BotCharacter ForName(string name)
        {
            // Identity key must match BotText.BaseName: spawned names look like
            // "[Bot] Grunt_42" -> "Grunt". Splitting the raw name first yields
            // "[Bot] Grunt" and misses every non-Grunt entry in characters.json.
            string key = BotText.BaseName(name);
            if (key.Length == 0) key = "Grunt";
            // One read of the published table: three separate property reads
            // could straddle a `bot reload` and pair a miss on the new table
            // with the "Grunt" fallback from the old one, so one call would
            // return a character from a table that is no longer live.
            var table = Characters;
            if (table.TryGetValue(key, out var c)) return c;
            if (table.TryGetValue("Grunt", out var g)) return g;
            return BotCharacter.Defaults(key);
        }
    }
}
