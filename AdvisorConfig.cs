using BepInEx.Configuration;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// THE ADVISOR CONFIG: the three advisor knobs (see Plugin). THE BINDS HERE
    /// ARE THE SINGLE SOURCE for the plugin's live config (BaseUnityPlugin.Config)
    /// AND for the pre-generated cfg shipped in releases (scripts/cfggen):
    /// defaults and descriptions are generated from this call set, never copied.
    /// Unity-free by design: scripts/cfggen compiles this file headless.
    /// BepInEx 5 has no Config class: the type is ConfigFile.
    /// </summary>
    internal static class AdvisorConfig
    {
        public const string PluginGuid = "com.jabulba.settlementplacementadvisor";
        public const string PluginName = "SettlementPlacementAdvisor";
        public const string Section = "Advisor";

        public sealed class Entries
        {
            public ConfigEntry<int> TopHovered;
            public ConfigEntry<int> TopNeighbour;
            public ConfigEntry<int> RangeLevels;
        }

        public static Entries Bind(ConfigFile file)
        {
            return new Entries
            {
                TopHovered = file.Bind(Section, "TopHovered", 2,
                    "Recommended tiles shown on the hovered territory; count is mandatory, below-minimum-FIMS tiles are included. The tile under the mouse ghost counts visually on top of this."),
                TopNeighbour = file.Bind(Section, "TopNeighbour", 1,
                    "Recommended tiles shown on every other territory in range; count is mandatory."),
                RangeLevels = file.Bind(Section, "RangeLevels", 2,
                    "Adjacency range from the hovered territory, center counts as 1: 3 = hovered + adjacent + adjacent-to-adjacent. Land adjacency only: sea, cliff and mountain edges never count as a range step."),
            };
        }
    }
}
