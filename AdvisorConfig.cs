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
                    "Admission quota on the hovered territory: this many tiles minimum, plus every tile tied with the last admitted score."),
                TopNeighbour = file.Bind(Section, "TopNeighbour", 1,
                    "Admission quota on every other territory in the placement neighborhood: this many tiles minimum, plus every tile tied with the last admitted score."),
                RangeLevels = file.Bind(Section, "RangeLevels", 2,
                    "Placement-neighborhood hops from the hovered territory, the hovered territory counts as level 1: 3 = hovered + adjacent + adjacent-to-adjacent. Every adjacency edge counts as a hop: sea, cliff, mountain and closed-border edges included."),
            };
        }
    }
}
