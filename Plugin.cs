using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// Settlement Placement Advisor: while the camp (outpost) placement cursor is active,
    /// shows recommended-tile stars for the hovered territory (top 2) and for every
    /// territory in the adjacency range (top 1 each, RangeLevels = 2 by default), with FIMS
    /// pins, mandatory counts that ignore the vanilla minimum-FIMS gate. No orders: it only
    /// writes the advisor's tail slots of the placement evaluation array.
    /// Rules: README.md; module vocabulary: GLOSSARY.md.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.jabulba.settlementplacementadvisor";
        public const string PluginName = "SettlementPlacementAdvisor";
        public const string PluginVersion = "0.1.0";

        internal static ConfigEntry<int> CfgTopHovered;
        internal static ConfigEntry<int> CfgTopNeighbour;
        internal static ConfigEntry<int> CfgRangeLevels;

        internal static Plugin Instance;

        internal Advisor Advisor;
        internal EvaluationWriter Writer;
        internal StarRenderer Stars;

        internal void Log(string message)
        {
            Logger.LogInfo("[SettlementPlacementAdvisor] " + message);
        }

        private void Awake()
        {
            Instance = this;

            CfgTopHovered = Config.Bind("Advisor", "TopHovered", 2,
                "Recommended tiles shown on the hovered territory; count is mandatory, below-minimum-FIMS tiles are included. The tile under the mouse ghost counts visually on top of this.");
            CfgTopNeighbour = Config.Bind("Advisor", "TopNeighbour", 1,
                "Recommended tiles shown on every other territory in range; count is mandatory.");
            CfgRangeLevels = Config.Bind("Advisor", "RangeLevels", 2,
                "Adjacency range from the hovered territory, center counts as 1: 3 = hovered + adjacent + adjacent-to-adjacent. Land adjacency only: sea, cliff and mountain edges never count as a range step.");

            GameApi.Resolve();
            if (!GameApi.ReadyStatic)
            {
                Log("Disabled, unresolved game members: " + GameApi.ResolveError);
                return;
            }

            Stars = new StarRenderer(new UnityPickCanvas());
            Writer = new EvaluationWriter();
            Advisor = new Advisor(GameApi.Shared, new PickComputation(GameApi.Shared), Writer, Stars);

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log("Ready: advisor patches active; star rendering wires on the first camp placement refresh.");
        }
    }
}
