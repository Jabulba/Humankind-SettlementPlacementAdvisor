using System;
using BepInEx.Configuration;

namespace SettlementPlacementAdvisor.composition
{
	/// <summary>
	///     Single source for the live plugin config and the pre-generated release cfg (tools/CfgGen), so
	///     defaults and descriptions are never copied. Unity-free by design; BepInEx 5 names the config
	///     type ConfigFile, not Config. Plugin.Awake binds the file and snapshots the values into
	///     SpaRuntime, so no simulation-thread path reads a ConfigEntry.
	/// </summary>
	internal static class AdvisorConfig
	{
		public const string PluginGuid = "com.jabulba.settlementplacementadvisor";
		public const string PluginName = "SettlementPlacementAdvisor";
		public const string Section = "Advisor";

		/// <summary>
		///     Bind default: two recommendations on the hovered territory, plus the tile under the cursor.
		/// </summary>
		public const int TopHovered = 2;

		/// <summary>
		///     Bind default: one recommendation on each immediately adjacent territory.
		/// </summary>
		public const int TopNeighbour = 1;

		/// <summary>
		///     Bind default: the placement neighborhood is the hovered territory plus one adjacency hop.
		/// </summary>
		public const int RangeLevels = 2;

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
				TopHovered = file.Bind(Section, "TopHovered", TopHovered,
					"How many tiles the mod recommends on the territory under your cursor. Tiles that tie with the last recommendation are shown too, so the number you see can be higher. Values below 1 count as 1."),
				TopNeighbour = file.Bind(Section, "TopNeighbour", TopNeighbour,
					"How many tiles the mod recommends on each other territory the search reaches. Tiles that tie with the last recommendation are shown too, so the number you see can be higher. Values below 1 count as 1."),
				RangeLevels = file.Bind(Section, "RangeLevels", RangeLevels,
					"How far the search spreads from the territory under your cursor. 1 recommends on that territory only, 2 adds the territories touching it, 3 adds the territories touching those. Territories count as touching wherever the map connects them, across sea, cliffs, mountains and the map edge alike. Values below 1 count as 1."),
			};
		}

		/// <summary>Territories a scan reaches per hop beyond level 1, the neighborhood budget the
		/// tail reserve sizes for. A hex territory neighbours about six, so eight is the fan-out
		/// with headroom. The hop count stays unclamped above the floor: a neighborhood past the
		/// reserve is covered by grow-on-demand, never by capping the scan.</summary>
		private const int FanOutTerritoriesPerHop = 8;

		/// <summary>Slots reserved per territory over its admission quota for the top tie set. Ties
		/// are uncapped, so this is an allowance, not a cap: admission is never truncated to it, and
		/// grow-on-demand covers a bigger tie set.</summary>
		private const int TieHeadroomPerTerritory = 8;

		/// <summary>The largest reserve worth pre-allocating: a wide hop count saturates here and
		/// grow-on-demand takes the slots the scan actually publishes.</summary>
		public const int MaxTailReserve = 4096;

		/// <summary>
		///     The tail reserve the writer takes at an activation: the worst-case admission-set size for the
		///     bound knobs. The neighborhood is the hovered territory plus RangeLevels - 1 hops at the
		///     fan-out budget, every territory in it admits its quota plus the tie headroom, and the worst
		///     case takes the larger quota for both. Quotas and hop count are clamped on the floors
		///     CursorContext.For applies, so the reserve follows the scan the same config runs. It is a
		///     reserve, never a cap: an admission set past it grows the window, and the release scrubs the
		///     whole grown region.
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
