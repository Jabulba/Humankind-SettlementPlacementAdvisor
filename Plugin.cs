using System;
using System.IO;
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

        /// <summary>
        /// The load failure, written where it survives the hard exit: through the logger, which Unity
        /// flushes per line into its own Player.log, plus a file this mod owns under the BepInEx root.
        /// BepInEx's LogOutput.log is out of reach at this point: its DiskLogListener flushes on a 2
        /// second timer, so the exit drops buffered lines, and it holds that file open with
        /// FileShare.Read, so an append from here is refused.
        /// </summary>
        private void LogLoadFailure(string message)
        {
            Logger.LogError(message);
            try
            {
                LoadGate.AppendFailureLine(
                    Path.Combine(Paths.BepInExRootPath, AdvisorConfig.PluginName + "-load-failure.log"),
                    AdvisorConfig.PluginName + " " + ModVersion.Value + ": " + message);
            }
            catch (Exception e)
            {
                Logger.LogError("load-failure file not written: " + e.Message);
            }
        }

        private void Awake()
        {
            Instance = this;

            var cfg = AdvisorConfig.Bind(Config);
            CfgTopHovered = cfg.TopHovered;
            CfgTopNeighbour = cfg.TopNeighbour;
            CfgRangeLevels = cfg.RangeLevels;

            GameApi.Resolve();
            LoadGate.EnforceResolvedSurface(LogLoadFailure, LoadGate.CloseGame);

            Log(LoadGate.ReadyLine(GameApi.RequiredNames, AdvisorConfig.TailCapacityFor(
                CfgTopHovered.Value, CfgTopNeighbour.Value, CfgRangeLevels.Value)));
            Stars = new StarRenderer(new UnityPickCanvas());
            Writer = new EvaluationWriter();
            Advisor = new Advisor(GameApi.Shared, new PickComputation(GameApi.Shared), Writer, Stars);

            var harmony = new Harmony(AdvisorConfig.PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log("Ready: advisor patches active; star rendering wires on the first settlement placement refresh.");
        }
    }
}
