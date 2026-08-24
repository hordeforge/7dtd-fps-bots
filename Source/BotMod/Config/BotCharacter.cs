using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace BotMod.Config
{
    // Direct port of Q3's 80 characteristic slots (chars.h). Only the subset used by
    // BotAimAtEnemy/BotCheckAttack/BotChangeViewAngles + BotWantsTo* is required,
    // but we keep the full table so character files match Q3 layout.
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
        public bool ChallengeAim { get; set; } = false; // Q3 bot_challenge cvar: true=clamped smooth, false=spring

        public static BotCharacter Defaults(string name = "Grunt") => new BotCharacter { Name = name };

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

        // Q3-style camp decision (BotWantsToCamp helper) - used by BotBrain
        // Deterministic overload: caller supplies a 0..1 roll from the bot's per-slot LCG (zdtd parity).
        public bool WantsToCamp(float healthFrac, float roll01) { return Camper > 0.45f && healthFrac > 0.55f && roll01 < Camper * 0.4f; }
    }

    // Loads config/characters.json which mirrors Q3 bots/*.c skill blocks. Fallback is defaults lerped by Difficulty.
    public static class BotCharacterDB
    {
        public static Dictionary<string, BotCharacter> Characters { get; private set; } = new Dictionary<string, BotCharacter>(StringComparer.OrdinalIgnoreCase);
        public static void Load(BotConfig cfg)
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(BotCharacterDB).Assembly.Location) ?? ".", "Config", "characters.json");
                if (!System.IO.File.Exists(path)) path = System.IO.Path.Combine(".", "config", "characters.json");
                if (!System.IO.File.Exists(path)) path = System.IO.Path.Combine("config", "characters.json");
                if (System.IO.File.Exists(path))
                {
                    // Explicit UTF-8: characters.json is our own artifact and is
                    // written UTF-8; never depend on the platform default codepage.
                    var json = System.IO.File.ReadAllText(path, Encoding.UTF8);
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, BotCharacter>>(json);
                    if (loaded != null)
                    {
                        // Sanitize at ingestion, then canonicalize keys
                        // (IdentityKey = NFC + no control/invisible
                        // characters): file values are operator-authored text
                        // that may carry NaN/Infinity literals or out-of-range
                        // traits; file keys may be NFD or carry paste noise.
                        // Keep one sane, canonical form on both sides of the
                        // lookup.
                        var canon = new Dictionary<string, BotCharacter>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in loaded)
                        {
                            kv.Value.Normalize();
                            canon[BotText.IdentityKey(kv.Key)] = kv.Value;
                        }
                        Characters = canon;
                    }
                }
                else
                {
                    BotConfig.Warn("characters.json not found (looked beside the assembly and under ./config); bots use built-in default characteristics");
                    // Rebuild from pristine defaults: with no file there is nothing
                    // to re-parse, so Characters would keep the instances a previous
                    // Load already shifted by the difficulty lerp below, and every
                    // `bot reload` would drift aim/reaction/aggression further
                    // toward their clamps.
                    Characters = new Dictionary<string, BotCharacter>(StringComparer.OrdinalIgnoreCase);
                }
                // Ensure at least defaults for known names (BaseName is NFC, same
                // canonical form as the keys above).
                foreach (var n in cfg.BotNames)
                {
                    string key = BotText.BaseName(n);
                    if (!Characters.ContainsKey(key)) Characters[key] = BotCharacter.Defaults(key);
                }
                // Apply difficulty lerp if characters have multiple skills (stored as skill 1 vs 5) - here we just scale by cfg.Difficulty
                float diffSkill = cfg.Difficulty / 4f;
                foreach (var kv in new List<KeyValuePair<string,BotCharacter>>(Characters))
                {
                    var ch = kv.Value;
                    // Difficulty gently overrides core aim/reaction/aggro
                    ch.AimAccuracy = Math.Max(0.2f, Math.Min(1f, ch.AimAccuracy + diffSkill * 0.25f - 0.12f));
                    ch.AimSkill = Math.Max(0.2f, Math.Min(1f, ch.AimSkill + diffSkill * 0.25f - 0.12f));
                    ch.ReactionTime = Math.Max(0.05f, ch.ReactionTime - diffSkill * 0.25f);
                    ch.Alertness = Math.Max(0.1f, Math.Min(1f, ch.Alertness + diffSkill * 0.3f - 0.15f));
                    ch.Aggression = Math.Max(0f, Math.Min(1f, ch.Aggression + diffSkill * 0.2f - 0.1f));
                }
            }
            catch (Exception ex) { BotConfig.Warn("characters.json load failed, using defaults: " + ex.Message); }
        }
        public static BotCharacter ForName(string name)
        {
            // Identity key must match BotManager.BaseName: spawned names look like
            // "[Bot] Grunt_42" -> "Grunt". Splitting the raw name first yields
            // "[Bot] Grunt" and misses every non-Grunt entry in characters.json.
            string key = BaseKey(name);
            if (Characters.TryGetValue(key, out var c)) return c;
            if (Characters.TryGetValue("Grunt", out var g)) return g;
            return BotCharacter.Defaults(key);
        }
        /// <summary>Base key for a bot name: strip the "[Bot] " tag, drop the _NN
        /// suffix. Shared canonicalization in BotText.BaseName (Core references
        /// Config, not the other way, so the helper lives here).</summary>
        static string BaseKey(string name)
        {
            string key = BotText.BaseName(name);
            return key.Length == 0 ? "Grunt" : key;
        }
    }
}
