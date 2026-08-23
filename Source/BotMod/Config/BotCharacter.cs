using System;
using System.Collections.Generic;
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
        // Q3-style decisions (BotWantsToRetreat/Camp helpers) - used by BotBrain
        public bool WantsToRetreat(float healthFrac, float enemyDist, bool hasBetterWeapon) { return healthFrac < 0.35f + SelfPreservation * 0.18f && (enemyDist < 22f || !hasBetterWeapon); }
        // Deterministic overloads: caller supplies a 0..1 roll from the bot's per-slot LCG (zdtd parity).
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
                if (!System.IO.File.Exists(path)) path = "config/characters.json";
                if (System.IO.File.Exists(path))
                {
                    var json = System.IO.File.ReadAllText(path);
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, BotCharacter>>(json);
                    if (loaded != null) Characters = loaded;
                }
                else
                {
                    ModApi.Warn("characters.json not found (looked beside the assembly and under ./config); bots use built-in default characteristics");
                    // Rebuild from pristine defaults: with no file there is nothing
                    // to re-parse, so Characters would keep the instances a previous
                    // Load already shifted by the difficulty lerp below, and every
                    // `bot reload` would drift aim/reaction/aggression further
                    // toward their clamps.
                    Characters = new Dictionary<string, BotCharacter>(StringComparer.OrdinalIgnoreCase);
                }
                // Ensure at least defaults for known names
                foreach (var n in cfg.BotNames)
                {
                    string key = n.Split('_')[0];
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
            catch (Exception ex) { ModApi.Warn("characters.json load failed, using defaults: " + ex.Message); }
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
        /// suffix. Local copy of BotManager.BaseName because Core already
        /// references Config (the reverse would be a dependency cycle).</summary>
        static string BaseKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Grunt";
            if (name.StartsWith("[Bot] ", StringComparison.OrdinalIgnoreCase)) name = name.Substring(6);
            return name.Split('_')[0];
        }
    }
}
