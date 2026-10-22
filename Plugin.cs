using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// Settlement-placement advisor composition root: recommended-tile stars with FIMS pins.
    /// Rules: README.md. Module vocabulary: GLOSSARY.md.
    /// </summary>
    [BepInPlugin(AdvisorConfig.PluginGuid, AdvisorConfig.PluginName, ModVersion.Value)]
    public class Plugin : BaseUnityPlugin
    {
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
            Log("Ready: advisor patches active; star rendering wires on the first settlement placement refresh.");
        }
    }
}
