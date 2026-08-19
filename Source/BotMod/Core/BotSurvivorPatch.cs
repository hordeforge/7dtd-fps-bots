using System;
using System.IO;
using System.Xml;

namespace BotMod.Core
{
    /// <summary>
    /// Best-effort hot-inject of npcSurvivorTemplate/npcSurvivorRanged into
    /// EntityClass.list at runtime, so BotSpawner can pick generated UMA player
    /// models (AvatarUMAController) that actually render held guns. Vanilla XML
    /// has them inside <!-- --> so EntityClass.FromString("npcSurvivorRanged")
    /// is -1 on stock dedi.
    /// </summary>
    public static class BotSurvivorPatch
    {
        static bool _done;
        public static void EnsureSurvivorClasses()
        {
            if (_done) return; _done = true;
            try
            {
                if (EntityClass.FromString("npcSurvivorRanged") >= 0) return; // already active (modded XML)
                InjectFromSnippets();
                if (EntityClass.FromString("npcSurvivorRanged") >= 0)
                    ModApi.Log("BotSurvivorPatch: npcSurvivorRanged injected at runtime.");
                else
                    ModApi.Log("BotSurvivorPatch: survivor still unavailable — bots stay soldier-pool.");
            }
            catch (Exception ex) { ModApi.Log("EnsureSurvivorClasses: " + ex.Message); }
        }

        static void InjectFromSnippets()
        {
            // Minimal survivor template — enough for BotSpawner + weapon hold.
            // Full template in Data/Config/entityclasses.xml is ~40 lines; we
            // synthesize only the nodes that matter for spawning/anim/DM.
            // This uses reflection to call EntityClass.AddClass or the XML reload,
            // falling back gracefully if the API shifts.
            try
            {
                var t = typeof(EntityClass);
                // Old pattern: EntityClass.AddClass(XmlNode)
                var addNode = t.GetMethod("AddClass", new[] { typeof(XmlElement) });
                if (addNode == null) addNode = t.GetMethod("AddClass", new[] { typeof(XmlNode) });
                if (addNode != null)
                {
                    string xml = @"<entity_class name=""npcSurvivorBot"" parent=""zombieSoldier"" >
  <property name=""Class"" value=""EntityAlive"" />
  <property name=""Mesh"" value=""Player/Male/player_maleRagdoll"" />
  <property name=""Prefab"" value=""NPC"" />
  <property name=""AvatarController"" value=""AvatarUMAController"" />
  <property name=""ModelType"" value=""NpcUMA"" />
  <property name=""EntityType"" value=""Player"" />
  <property name=""Faction"" value=""whiteriver"" />
  <property name=""Parent"" value=""Players"" />
  <property name=""PhysicsBody"" value=""Player"" />
  <property name=""HasRagdoll"" value=""true"" />
  <property name=""MaxHealth"" value=""100"" />
  <property name=""MoveSpeed"" value=""1.0"" />
  <property name=""MoveSpeedAggro"" value=""1.15"" />
  <property name=""IsEnemyEntity"" value=""true"" />
  <property name=""TimeStayAfterDeath"" value=""30"" />
  <property name=""LootList"" value=""cntDropBag"" />
  <property name=""SoundHurt"" value=""Player_Male/player1painlg"" />
  <property name=""SoundDeath"" value=""Player_Male/player1death"" />
</entity_class>";
                    var doc = new XmlDocument();
                    doc.LoadXml(xml);
                    var el = doc.DocumentElement;
                    object ret = addNode.Invoke(null, new object[] { el });
                    int id = Convert.ToInt32(ret);
                    ModApi.Log("Injected npcSurvivorBot id=" + id + " (UMA player mesh)");
                    return;
                }
                ModApi.Log("BotSurvivorPatch: EntityClass.AddClass not found — no inject.");
            }
            catch (Exception ex) { throw new Exception("InjectFromSnippets: " + ex.Message, ex); }
        }
    }
}
