namespace BotMod.Core
{
    /// <summary>Single source of truth for the mod version: the AssemblyInfo
    /// attributes and the startup log line read it from here, and
    /// scripts/build.sh fails the build when Source/BotMod/ModInfo.xml
    /// disagrees (the engine's mod listing cannot reference this constant).
    /// Bump both together in one commit; see CHANGELOG.md.</summary>
    internal static class BotModVersion
    {
        public const string Number = "0.4.0";
    }
}
