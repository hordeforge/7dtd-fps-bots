namespace BotMod.AI
{
    /// <summary>Registry queries about live bots, owned by the host layer
    /// (BotMod.Core.BotManager) and installed there at type init. AI code must
    /// not reach into Core directly: Core already depends on AI, so an
    /// AI -> Core edge would close a cycle between the two namespaces.</summary>
    public interface IBotRegistry
    {
        bool IsBotEntity(int entityId);
        bool AreAllies(int aId, int bId);
    }

    /// <summary>Install point for <see cref="IBotRegistry"/>. Before Core
    /// installs an implementation the queries answer "no bots", which matches
    /// the pre-GameStartDone registry that is empty by construction.</summary>
    public static class BotRegistry
    {
        static IBotRegistry _impl;
        public static void Install(IBotRegistry impl) { _impl = impl; }
        internal static bool IsBotEntity(int entityId) => _impl != null && _impl.IsBotEntity(entityId);
        internal static bool AreAllies(int aId, int bId) => _impl != null && _impl.AreAllies(aId, bId);
    }
}
