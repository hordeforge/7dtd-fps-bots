namespace BotMod.Config
{
    /// <summary>
    /// Deterministic 32-bit LCG (zdtd parity: state -> state*1103515245+12345,
    /// top 24 bits as the 0..1 float). One copy of the tap constants so the
    /// per-bot combat rolls (Bot), the spawn picks (BotSpawner), the mixed
    /// loadout counter (WeaponProfile.ForGun) and the one-shot wander hash
    /// (BotBrain.WanderHash01) cannot drift apart; tools/ga mirrors the same
    /// constants in Python by contract (docs/research/00).
    /// Struct, not class: every holder owns its stream; no shared mutable state.
    /// </summary>
    internal struct Lcg
    {
        uint _state;

        Lcg(uint state) { _state = state; }

        /// <summary>Seed a stream. Callers use their documented mixes
        /// (entityId*2654435761 + salt etc.), so no canonical seed lives here.</summary>
        public static Lcg Seeded(uint seed) { return new Lcg(seed); }

        /// <summary>Advance the stream and return the raw 32-bit state.</summary>
        public uint Next()
        {
            _state = _state * 1103515245u + 12345u;
            return _state;
        }

        /// <summary>Uniform [0,1) from the top 24 bits.</summary>
        public float Next01() { return (Next() >> 8 & 0x00ffffffu) / 16777216f; }

        /// <summary>Uniform [-1,1).</summary>
        public float NextSymmetric() { return 2f * Next01() - 1f; }

        /// <summary>Uniform index in [0, count); count <= 0 yields 0.</summary>
        public int Index(int count)
        {
            if (count <= 0) return 0;
            return (int)((Next() >> 8 & 0x00ffffffu) % (uint)count);
        }

        /// <summary>Uniform integer in [lo, hi); hi <= lo yields lo.</summary>
        public int Range(int lo, int hi)
        {
            if (hi <= lo) return lo;
            return lo + Index(hi - lo);
        }
    }
}
