using BepInEx.Configuration;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// Single source for the live plugin config and the pre-generated release cfg
    /// (scripts/cfggen), so defaults and descriptions are never copied. Unity-free by design.
    /// BepInEx 5 names the config type ConfigFile, not Config.
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
