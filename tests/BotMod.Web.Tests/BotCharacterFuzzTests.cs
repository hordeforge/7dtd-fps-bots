// BotCharacterFuzzTests: fuzzing of the operator-maintained
// config/characters.json loader (BotCharacterDB.Load). Like botmod.json, that
// file is hand-editable text parsed at every mod startup; its traits feed the
// neural observation vector, the per-engagement aim-bias rotation and the
// camp/retreat gates, and its keys join spawned bot names to entries.
// Mutants (byte-level and structure-aware JSON tampering of the shipped
// config/characters.json) must either fail cleanly behind Warn with the
// previous/default table intact or land every entry inside the
// Normalize+difficulty-lerp contract: all float traits finite and in range,
// per-weapon tables sanitized entry by entry, keys in canonical IdentityKey
// form, and ForName usable for arbitrary lookups. Includes regressions for
// the depth DoS path (deeply nested junk must not overflow the stack), bare
// NaN/Infinity literals through the full ingestion path, $type injection,
// hostile key shapes, and load determinism. Complements the deterministic
// pins in BotCharacterArithTests the way BotConfigFuzzTests complements
// BotConfigLoadTests.
//
// Needs Newtonsoft.Json.dll from the game install (same gate as the config
// fuzzer); compiles only the engine-free Config sources. Run locally:
//
//   bash scripts/test-idempotency.sh
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

static class BotCharacterFuzzTests
{
    const int Mutants = 500;

    static int _failures;
    static int _warns;
    static int _docs;

    static void Check(bool ok, string detail)
    {
        if (!ok)
        {
            _failures++;
            Console.WriteLine("FAIL " + detail);
        }
    }

    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "botmod-charfuzz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The consumer math (the aim-bias window derived from ingested
    /// accuracy), restated independently: any loaded accuracy must yield a
    /// finite window inside its documented band.</summary>
    static bool AimWindowSane(float acc)
    {
        float w = Math.Max(0.03f, (1f - acc) * 0.45f);
        return !float.IsNaN(w) && !float.IsInfinity(w) && w >= 0.03f && w <= 0.45f;
    }

    /// <summary>The post-load contract, restated independently: whatever came
    /// off disk, every table entry is finite, in range and lookup-ready.</summary>
    static void CheckLoadedContract(string ctx)
    {
        Dictionary<string, BotMod.Config.BotCharacter> table = BotMod.Config.BotCharacterDB.Characters;
        Check(table != null, ctx + ": Characters null after Load");
        if (table == null) return;

        foreach (KeyValuePair<string, BotMod.Config.BotCharacter> kv in table)
        {
            BotMod.Config.BotCharacter ch = kv.Value;
            Check(ch != null, ctx + ": null entry under key '" + kv.Key + "'");
            if (ch == null) continue;

            // Keys are stored post-IdentityKey; the transform is idempotent,
            // so canonical form shows up as exact self-equality.
            Check(kv.Key == BotMod.Config.BotText.IdentityKey(kv.Key),
                ctx + ": non-canonical key survived: '" + kv.Key + "'");

            Check(ch.Name != null, ctx + ": null Name under key '" + kv.Key + "'");
            float[] unit =
            {
                ch.AttackSkill, ch.ViewFactor, ch.AimAccuracy, ch.AimSkill,
                ch.Croucher, ch.Jumper, ch.Walker, ch.WeaponJumping,
                ch.Aggression, ch.SelfPreservation, ch.Vengefulness, ch.Camper,
                ch.EasyFragger, ch.Alertness, ch.FireThrottle, ch.ChatInsult
            };
            for (int i = 0; i < unit.Length; i++)
                Check(!float.IsNaN(unit[i]) && !float.IsInfinity(unit[i]) && unit[i] >= 0f && unit[i] <= 1f,
                    ctx + ": unit trait #" + i + " out of range under '" + kv.Key + "': " + unit[i]);

            // Magnitude traits: finite positive; the difficulty lerp floors
            // reaction time onto its documented 0.05 rail.
            Check(!float.IsNaN(ch.ViewMaxChange) && !float.IsInfinity(ch.ViewMaxChange) && ch.ViewMaxChange > 0f,
                ctx + ": ViewMaxChange not finite-positive under '" + kv.Key + "': " + ch.ViewMaxChange);
            Check(!float.IsNaN(ch.ReactionTime) && !float.IsInfinity(ch.ReactionTime) && ch.ReactionTime >= 0.05f,
                ctx + ": ReactionTime below the lerp floor under '" + kv.Key + "': " + ch.ReactionTime);

            // Difficulty-lerp rails for the four overridden core traits.
            Check(ch.AimAccuracy >= 0.2f && ch.AimAccuracy <= 1f, ctx + ": AimAccuracy off the lerp rails under '" + kv.Key + "': " + ch.AimAccuracy);
            Check(ch.AimSkill >= 0.2f && ch.AimSkill <= 1f, ctx + ": AimSkill off the lerp rails under '" + kv.Key + "': " + ch.AimSkill);
            Check(ch.Alertness >= 0.1f && ch.Alertness <= 1f, ctx + ": Alertness off the lerp rails under '" + kv.Key + "': " + ch.Alertness);

            // Per-weapon tables: sanitized entry by entry or absent entirely.
            foreach (Dictionary<string, float> t in new[] { ch.AimAccuracyWeapon, ch.AimSkillWeapon })
            {
                if (t == null) continue;
                foreach (KeyValuePair<string, float> w in t)
                    Check(!float.IsNaN(w.Value) && !float.IsInfinity(w.Value) && w.Value >= 0f && w.Value <= 1f,
                        ctx + ": per-weapon trait out of range under '" + kv.Key + "/" + w.Key + "': " + w.Value);
            }

            Check(AimWindowSane(ch.AimAccuracy), ctx + ": aim-bias window not sane for accuracy under '" + kv.Key + "': " + ch.AimAccuracy);
        }

        // The lookup chain must serve any name shape with a usable entry.
        foreach (string probe in new[] { "Grunt", "[Bot] Grunt_42", "", "\u0000ctl", "Zed_9", "[Bot] K\u00edra_7" })
        {
            BotMod.Config.BotCharacter c = BotMod.Config.BotCharacterDB.ForName(probe);
            Check(c != null, ctx + ": ForName('" + probe + "') returned null");
            if (c == null) continue;
            Check(AimWindowSane(c.AimAccuracy) && !float.IsNaN(c.ReactionTime) && c.ReactionTime > 0f,
                ctx + ": ForName('" + probe + "') served unusable traits");
        }
    }

    /// <summary>Write mutant, Load it, pin the no-throw/sane-table contract.
    /// Warn output is counted, not printed.</summary>
    static void FuzzLoad(byte[] content, int difficulty, string ctx)
    {
        _docs++;
        BotMod.Config.BotConfig.Warn = _ => _warns++;
        string dir = TempDir();
        string oldCwd = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "config"));
            File.WriteAllBytes(Path.Combine(dir, "config", "characters.json"), content);
            Environment.CurrentDirectory = dir;
            BotMod.Config.BotCharacterDB.Load(new BotMod.Config.BotConfig { Difficulty = difficulty });
        }
        catch (Exception ex)
        {
            Check(false, ctx + ": Load threw " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            Environment.CurrentDirectory = oldCwd;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
        BotMod.Config.BotConfig.Warn = null;
        CheckLoadedContract(ctx);
    }

    // ---- mutant generation ----

    static byte[] MutateBytes(byte[] src, Random rng)
    {
        byte[] m = (byte[])src.Clone();
        switch (rng.Next(4))
        {
            case 0: // truncate
                Array.Resize(ref m, rng.Next(0, m.Length));
                return m;
            case 1: // flip bytes
                for (int n = rng.Next(1, 8); n > 0 && m.Length > 0; n--)
                    m[rng.Next(m.Length)] = (byte)rng.Next(256);
                return m;
            case 2: // delete a chunk
                if (m.Length < 4) return m;
                int cutAt = rng.Next(m.Length - 1);
                int cutLen = Math.Min(rng.Next(1, 40), m.Length - cutAt);
                byte[] shorter = new byte[m.Length - cutLen];
                Array.Copy(m, shorter, cutAt);
                Array.Copy(m, cutAt + cutLen, shorter, cutAt, m.Length - cutAt - cutLen);
                return shorter;
            default: // insert junk
                int at = rng.Next(m.Length + 1);
                int junkLen = rng.Next(1, 20);
                byte[] longer = new byte[m.Length + junkLen];
                Array.Copy(m, longer, at);
                for (int j = 0; j < junkLen; j++) longer[at + j] = (byte)rng.Next(256);
                Array.Copy(m, at, longer, at + junkLen, m.Length - at);
                return longer;
        }
    }

    static readonly object[] ExtremeValues =
    {
        int.MinValue, int.MaxValue, 0, -1, 999999999,
        3e38f, -3e38f, 0.5d, 1e300d,
        "not-a-number", "", true, false, null,
        new JArray { 1, 2 }, new JObject { ["nested"] = 9 }
    };

    static readonly string[] AnyFloatFields =
    {
        "AttackSkill", "ViewFactor", "ViewMaxChange", "ReactionTime",
        "AimAccuracy", "AimSkill", "Croucher", "Jumper", "Walker",
        "WeaponJumping", "Aggression", "SelfPreservation", "Vengefulness",
        "Camper", "EasyFragger", "Alertness", "FireThrottle", "ChatInsult"
    };

    static readonly string[] HostileKeys =
    {
        "Gru\u200bnt", "Ki\u0301ra", "\u0000ctl\u0007", "\ufeffBOM",
        "", new string('k', 4096), "\ud83d\ude00-astral"
    };

    static JToken AsToken(object v)
    {
        return v as JToken ?? new JValue(v);
    }

    /// <summary>Structure-aware tamper: returns mutated JSON text, or null when
    /// the strategy needs the base to parse and it does not.</summary>
    static string MutateStructure(string json, Random rng)
    {
        JObject obj;
        try { obj = JObject.Parse(json); }
        catch (Exception) { return null; }
        var names = obj.Properties().Select(p => p.Name).ToList();
        if (names.Count == 0) return null;
        string entryName = names[rng.Next(names.Count)];

        switch (rng.Next(8))
        {
            case 0: // a float trait gets an extreme or wrong-typed value
                obj[entryName][AnyFloatFields[rng.Next(AnyFloatFields.Length)]] = AsToken(ExtremeValues[rng.Next(ExtremeValues.Length)]);
                break;
            case 1: // per-weapon tables go hostile
                {
                    string table = rng.Next(2) == 0 ? "AimAccuracyWeapon" : "AimSkillWeapon";
                    switch (rng.Next(5))
                    {
                        case 0: obj[entryName][table] = null; break;
                        case 1: obj[entryName][table] = new JArray("shotgun", 1); break;
                        case 2: obj[entryName][table] = "shotgun"; break;
                        case 3: obj[entryName][table] = new JObject { ["\u0000ctl \u200bzwsp"] = -5 }; break;
                        default: obj[entryName][table] = new JObject { ["railgun"] = 1e300d, ["bfg10k"] = "high" }; break;
                    }
                    break;
                }
            case 2: // an entry value replaced wholesale
                switch (rng.Next(6))
                {
                    case 0: obj[entryName] = null; break;
                    case 1: obj[entryName] = "Grunt"; break;
                    case 2: obj[entryName] = new JArray(0.5, 0.75); break;
                    case 3: obj[entryName] = new JObject { ["AimAccuracy"] = new JObject { ["nested"] = true } }; break;
                    case 4: obj[entryName] = new JObject(); break;
                    default: obj[entryName] = new JObject { ["TagetBotTrai\u0007t"] = 1, ["\u00fcml\u00e4ut"] = new JObject() }; break;
                }
                break;
            case 3: // entry key goes hostile: paste noise, NFD, control chars
                JObject body;
                try { body = (JObject)obj[entryName]; }
                catch (Exception) { return null; }
                var rebuilt = new JObject();
                foreach (JProperty p in body.Properties()) rebuilt[p.Name] = p.Value;
                obj.Remove(entryName);
                obj[HostileKeys[rng.Next(HostileKeys.Length)]] = rebuilt;
                break;
            case 4: // duplicate key under different casing: last-wins binding
                obj[entryName.ToUpperInvariant()] = new JObject { ["Aggression"] = 0.11 };
                break;
            case 5: // Name field goes hostile
                obj[entryName]["Name"] = AsToken(new object[] { null, 42, "", "\u0000\u202egrunted", new string('n', 4096) }[rng.Next(5)]);
                break;
            case 6: // bool-typed ChallengeAim gets junk
                obj[entryName]["ChallengeAim"] = AsToken(ExtremeValues[rng.Next(ExtremeValues.Length)]);
                break;
            default: // whole-document shape attacks
                switch (rng.Next(4))
                {
                    case 0: return "[\"" + entryName + "\"]";
                    case 1: return "\"str\"";
                    case 2: obj["$type"] = "System.Diagnostics.Process, System"; break;
                    default: obj[entryName] = new JObject { ["AimAccuracy"] = new JArray(new JArray(new JArray(1))) }; break;
                }
                break;
        }
        return obj.ToString(Newtonsoft.Json.Formatting.None);
    }

    // ---- driver ----

    static int Main(string[] args)
    {
        string root = args != null && args.Length > 0 ? args[0] : ".";
        string goldenPath = null;
        foreach (string cand in new[]
        {
            Path.Combine(root, "config", "characters.json"),
            Path.Combine("config", "characters.json"),
            "../config/characters.json"
        })
            if (File.Exists(cand)) { goldenPath = cand; break; }
        if (goldenPath == null)
        {
            Console.WriteLine("character fuzz: config/characters.json not found under '" + root + "', skipping");
            return 0;
        }

        byte[] goldenBytes = File.ReadAllBytes(goldenPath);
        string goldenText = Encoding.UTF8.GetString(goldenBytes);
        var rng = new Random(20260826);

        // Deterministic regressions first.

        // 1. The shipped file loads clean into the full contract.
        FuzzLoad(goldenBytes, 2, "golden");
        Check(_warns == 0, "golden: shipped characters.json raised " + _warns + " warning(s), expected clean load");

        // 2. Depth DoS: deeply nested junk inside a trait must fail cleanly
        //    (reader depth limit -> Warn -> usable table), never overflow the
        //    stack or hang.
        {
            string deep = "{\"Grunt\": {\"AimAccuracy\": " + new string('[', 512) + new string(']', 512) + "}}";
            FuzzLoad(Encoding.UTF8.GetBytes(deep), 2, "depth-512");
        }

        // 3. Not an object at all: arrays and scalars as the root document.
        string[] rootDocs = { "[1,2,3]", "\"str\"", "42", "true", "null", "{", "", "   ", "\ufeff{\"Grunt\":{}}" };
        for (int d = 0; d < rootDocs.Length; d++)
            FuzzLoad(Encoding.UTF8.GetBytes(rootDocs[d]), 2,
                "root-doc<" + (rootDocs[d].Length == 0 ? "empty" : rootDocs[d]) + ">");

        // 4. Bare NaN/Infinity literals through the FULL ingestion path (not
        //    just bare Normalize): non-finite traits must come out as the
        //    documented defaults so the neural obs vector stays finite.
        {
            FuzzLoad(Encoding.UTF8.GetBytes("{\"Grunt\": {\"Camper\": NaN, \"AimAccuracy\": Infinity, \"ReactionTime\": -Infinity}}"),
                0, "nan-literals");
            BotMod.Config.BotCharacter g = BotMod.Config.BotCharacterDB.ForName("Grunt");
            Check(g.Camper == 0.2f, "nan-literals: NaN camper did not become default 0.2 (" + g.Camper + ")");
            Check(!float.IsInfinity(g.AimAccuracy) && g.AimAccuracy >= 0.2f && g.AimAccuracy <= 1f,
                "nan-literals: infinite aim accuracy not re-railed (" + g.AimAccuracy + ")");
            Check(g.ReactionTime == 0.35f, "nan-literals: -inf reaction time did not become default 0.35 at difficulty 0 (" + g.ReactionTime + ")");
        }

        // 5. Hostile keys collapse onto their canonical spelling and remain
        //    reachable through lookups (spellings outside the shipped roster,
        //    so ensure-defaults cannot mask the pin).
        {
            FuzzLoad(Encoding.UTF8.GetBytes("{ \"Sora\u0301\": {\"Aggression\": 0.9}, \"Ze\u200bdb\": {\"Aggression\": 0.1} }"),
                0, "hostile-keys");
            var table = BotMod.Config.BotCharacterDB.Characters;
            Check(table.ContainsKey("Sor\u00e1") && !table.ContainsKey("Sora\u0301"),
                "hostile-keys: NFD key not stored under its NFC spelling");
            Check(table.ContainsKey("Zedb"), "hostile-keys: ZWSP key did not collapse onto the clean spelling");
            BotMod.Config.BotCharacter zedb;
            if (table.TryGetValue("Zedb", out zedb))
                Check(ReferenceEquals(BotMod.Config.BotCharacterDB.ForName("[Bot] Ze\u200bdb_7"), zedb),
                    "hostile-keys: spawned-name form does not bridge to the collapsed table entry");
        }

        // 6. Duplicate keys differing only in casing: deserialization binds
        //    last-wins into the OrdinalIgnoreCase table, exactly one entry.
        {
            FuzzLoad(Encoding.UTF8.GetBytes("{ \"Grunt\": {\"Camper\": 0.1}, \"GRUNT\": {\"Camper\": 0.9} }"),
                0, "dup-casing");
            var table = BotMod.Config.BotCharacterDB.Characters;
            int grunts = 0;
            foreach (string k in table.Keys) if (string.Equals(k, "Grunt", StringComparison.OrdinalIgnoreCase)) grunts++;
            Check(grunts == 1, "dup-casing: expected exactly one Grunt entry, found " + grunts);
            Check(BotMod.Config.BotCharacterDB.ForName("Grunt").Camper == 0.9f,
                "dup-casing: last-wins binding violated (" + BotMod.Config.BotCharacterDB.ForName("Grunt").Camper + ")");
        }

        // 7. Load determinism: the same bytes twice give the same core state.
        {
            string dir = TempDir();
            Directory.CreateDirectory(Path.Combine(dir, "config"));
            File.WriteAllBytes(Path.Combine(dir, "config", "det.json"), goldenBytes);
            string oldCwd = Environment.CurrentDirectory;
            Environment.CurrentDirectory = dir;
            try
            {
                BotMod.Config.BotConfig.Warn = _ => { };
                BotMod.Config.BotCharacterDB.Load(new BotMod.Config.BotConfig { Difficulty = 3 });
                var first = BotMod.Config.BotCharacterDB.Characters;
                var snap = new List<KeyValuePair<string, float>>();
                foreach (var kv in first) snap.Add(new KeyValuePair<string, float>(kv.Key, kv.Value.Aggression));
                BotMod.Config.BotCharacterDB.Load(new BotMod.Config.BotConfig { Difficulty = 3 });
                var second = BotMod.Config.BotCharacterDB.Characters;
                Check(first.Count == second.Count, "determinism: reload changed entry count " + first.Count + " -> " + second.Count);
                bool same = first.Count == second.Count;
                for (int i = 0; i < snap.Count && same; i++)
                {
                    BotMod.Config.BotCharacter c;
                    same = second.TryGetValue(snap[i].Key, out c) && c.Aggression == snap[i].Value;
                }
                Check(same, "determinism: reload shifted entry state");
            }
            finally
            {
                Environment.CurrentDirectory = oldCwd;
                BotMod.Config.BotConfig.Warn = null;
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }

        // Randomized mutants.
        for (int i = 0; i < Mutants; i++)
        {
            byte[] content;
            double roll = rng.NextDouble();
            if (roll < 0.40) content = MutateBytes(goldenBytes, rng);
            else
            {
                string mutated = MutateStructure(goldenText, rng);
                if (mutated == null) content = MutateBytes(goldenBytes, rng);
                else content = Encoding.UTF8.GetBytes(mutated);
            }
            FuzzLoad(content, rng.Next(5), "mutant-" + i);
        }

        Console.WriteLine("character fuzz: " + _docs + " documents, " + _warns + " warnings surfaced");
        Console.WriteLine(_failures == 0 ? "all bot character fuzz checks passed" : _failures + " fuzz check(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
