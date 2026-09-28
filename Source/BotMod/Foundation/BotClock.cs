using System;

namespace BotMod.Foundation
{
    /// <summary>Every sim-time read in the mod comes through here, so a run is a
    /// function of the tick stream a caller feeds it rather than of wall-clock
    /// time. Production binds UnityEngine.Time (ModApi.InitMod); a test or a
    /// replay harness binds a virtual clock and steps it, which is what makes
    /// the bot timers (reaction windows, path-recalc cadence, wander, stuck
    /// detection) replayable from a seed instead of from the machine.
    /// Engine-free by construction: Foundation references nothing else in the
    /// mod, so the binding to UnityEngine lives in the caller.
    /// Unbound is an error, not a zero: a fake time of 0 parks every timer in
    /// the past and reads as a healthy idle bot.</summary>
    internal static class BotClock
    {
        static Func<float> _now;
        static Func<float> _delta;

        /// <summary>Install the time source. Main-thread only, once per load:
        /// the mod reads these on the game thread only, so no barrier is needed.
        /// Both delegates are read on every call site (the tick path is short
        /// enough that the indirection is cheaper than a cache-invalidation
        /// scheme would be to reason about).</summary>
        public static void Bind(Func<float> now, Func<float> delta)
        {
            if (now == null) throw new ArgumentNullException("now");
            if (delta == null) throw new ArgumentNullException("delta");
            _now = now;
            _delta = delta;
        }

        /// <summary>Seconds since the world started, as UnityEngine.Time.time
        /// reports it. The frame counter every bot deadline is expressed in.</summary>
        public static float Now
        {
            get
            {
                if (_now == null) throw new InvalidOperationException("BotClock is not bound; call BotClock.Bind before the tick loop");
                return _now();
            }
        }

        /// <summary>Seconds elapsed in the current frame. The tick budget the
        /// movement step scales against.</summary>
        public static float Delta
        {
            get
            {
                if (_delta == null) throw new InvalidOperationException("BotClock is not bound; call BotClock.Bind before the tick loop");
                return _delta();
            }
        }
    }
}
