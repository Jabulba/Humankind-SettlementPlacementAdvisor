using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// DEBUG-harness stand-in for the BepInEx plugin class: the advisor modules reach
    /// Plugin.Instance and the three config entries. The binds go through the production
    /// AdvisorConfig call set (the same seam scripts/cfggen uses), so the harness runs on
    /// the shipped defaults, and no game is involved.
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
