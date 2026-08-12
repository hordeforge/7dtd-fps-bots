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
        public static BotCharacter Lerp(BotCharacter a, BotCharacter b, float tt)
        {
            System.Func<float,float,float> l = (x,y) => x + (y - x) * Math.Max(0f, Math.Min(1f, tt));
            return new BotCharacter
            {
                Name = a.Name,
                AttackSkill = l(a.AttackSkill, b.AttackSkill),
                ViewFactor = l(a.ViewFactor, b.ViewFactor),
                ViewMaxChange = l(a.ViewMaxChange, b.ViewMaxChange),
                ReactionTime = l(a.ReactionTime, b.ReactionTime),
                AimAccuracy = l(a.AimAccuracy, b.AimAccuracy),
                AimSkill = l(a.AimSkill, b.AimSkill),
                AimAccuracyWeapon = a.AimAccuracyWeapon, // keep base map
                AimSkillWeapon = a.AimSkillWeapon,
                Croucher = l(a.Croucher, b.Croucher),
                Jumper = l(a.Jumper, b.Jumper),
                Walker = l(a.Walker, b.Walker),
                Aggression = l(a.Aggression, b.Aggression),
                SelfPreservation = l(a.SelfPreservation, b.SelfPreservation),
                Vengefulness = l(a.Vengefulness, b.Vengefulness),
                Camper = l(a.Camper, b.Camper),
                EasyFragger = l(a.EasyFragger, b.EasyFragger),
                Alertness = l(a.Alertness, b.Alertness),
                FireThrottle = l(a.FireThrottle, b.FireThrottle),
            };
        }
        public float GetAimAccuracy(string weaponKey)
        {
            if (AimAccuracyWeapon != null && AimAccuracyWeapon.TryGetValue(weaponKey.ToLowerInvariant(), out float v)) return v;
            return AimAccuracy;
        }
        public float GetAimSkill(string weaponKey)
        {
            if (AimSkillWeapon != null && AimSkillWeapon.TryGetValue(weaponKey.ToLowerInvariant(), out float v)) return v;
            return AimSkill;
        }
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
                // Ensure at least defaults for known names
                foreach (var n in cfg.BotNames) if (!Characters.ContainsKey(n.Split('_')[0])) Characters[n.Split('_')[0]] = BotCharacter.Defaults(n.Split('_')[0]);
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
            catch (Exception ex) { ModApi.Log("characters.json load failed: " + ex.Message); }
        }
        public static BotCharacter ForName(string name)
        {
            string key = (name ?? "Grunt").Split('_')[0];
            if (Characters.TryGetValue(key, out var c)) return c;
            if (Characters.TryGetValue("Grunt", out var g)) return g;
            return BotCharacter.Defaults(name);
        }
    }
}
