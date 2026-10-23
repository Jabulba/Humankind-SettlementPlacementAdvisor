using System;
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

        /// <summary>Territories a scan reaches per hop beyond level 1, the neighborhood budget the
        /// tail reserve sizes for. A hex territory neighbours about six, so eight is the fan-out
        /// with headroom. The hop count stays unclamped: a neighborhood past the reserve is covered
        /// by grow-on-demand, never by capping the scan.</summary>
        public const int FanOutTerritoriesPerHop = 8;

        /// <summary>Slots reserved per territory over its admission quota for the top tie set. Ties
        /// are uncapped (SPEC section 5 row 1), so this is an allowance, not a cap: admission is
        /// never truncated to it, and grow-on-demand covers a bigger tie set.</summary>
        public const int TieHeadroomPerTerritory = 8;

        /// <summary>The largest reserve worth pre-allocating: a wide hop count saturates here and
        /// grow-on-demand takes the slots the scan actually publishes.</summary>
        public const int MaxTailReserve = 4096;

        /// <summary>
        /// The tail reserve the writer takes at an activation: the worst-case admission-set size for
        /// the configured knobs (SPEC section 6 row 1). The neighborhood is the hovered territory
        /// plus RangeLevels - 1 hops at the fan-out budget, every territory in it admits its quota
        /// plus the tie headroom, and the worst case takes the larger quota for both. Clamped on the
        /// floors CursorContext.For applies, so the reserve follows the scan the same config runs.
        /// It is a reserve, never a cap: an admission set past it grows the window, and the release
        /// scrubs the whole grown region.
        /// </summary>
        public static int TailCapacityFor(int topHovered, int topNeighbour, int rangeLevels)
        {
            long quota = Math.Max(Math.Max(1, topHovered), Math.Max(1, topNeighbour));
            long territories = 1 + (long)FanOutTerritoriesPerHop * (Math.Max(1, rangeLevels) - 1);
            long worstCase = territories * (quota + TieHeadroomPerTerritory);
            return worstCase > MaxTailReserve ? MaxTailReserve : (int)worstCase;
        }
    }
}
