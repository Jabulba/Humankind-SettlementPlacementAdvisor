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
    [BepInPlugin(AdvisorConfig.PluginGuid, AdvisorConfig.PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
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

            var cfg = AdvisorConfig.Bind(Config);
            CfgTopHovered = cfg.TopHovered;
            CfgTopNeighbour = cfg.TopNeighbour;
            CfgRangeLevels = cfg.RangeLevels;

            GameApi.Resolve();
            if (!GameApi.ReadyStatic)
            {
                Log("Disabled, unresolved game members: " + GameApi.ResolveError);
                return;
            }

            Stars = new StarRenderer(new UnityPickCanvas());
            Writer = new EvaluationWriter();
            Advisor = new Advisor(GameApi.Shared, new PickComputation(GameApi.Shared), Writer, Stars);

            var harmony = new Harmony(AdvisorConfig.PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log("Ready: advisor patches active; star rendering wires on the first camp placement refresh.");
        }
    }
}
