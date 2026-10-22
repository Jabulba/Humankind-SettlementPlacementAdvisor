using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// DEBUG-harness stand-in for Plugin. Config binds go through production AdvisorConfig, so
    /// the harness runs on shipped defaults with no game involved.
    /// </summary>
    internal sealed class Plugin
    {
        internal static Plugin Instance;

        internal static ConfigEntry<int> CfgTopHovered;
        internal static ConfigEntry<int> CfgTopNeighbour;
        internal static ConfigEntry<int> CfgRangeLevels;

        internal Advisor Advisor;
        internal EvaluationWriter Writer;
        internal StarRenderer Stars;

        static Plugin()
        {
            var file = new ConfigFile(Path.Combine(Path.GetTempPath(), "noopcircuit.cfg"), false,
                new BepInPlugin(AdvisorConfig.PluginGuid, AdvisorConfig.PluginName, "0.0.0-harness"));
            file.SaveOnConfigSet = false;
            var entries = AdvisorConfig.Bind(file);
            CfgTopHovered = entries.TopHovered;
            CfgTopNeighbour = entries.TopNeighbour;
            CfgRangeLevels = entries.RangeLevels;
        }

        internal void Log(string message)
        {
            System.Console.WriteLine("[shim] " + message);
        }
    }
}
