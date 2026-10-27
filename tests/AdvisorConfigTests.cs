using System.IO;
using BepInEx;
using BepInEx.Configuration;
using SettlementPlacementAdvisor.composition;
using Xunit;

namespace SettlementPlacementAdvisor.Tests
{
	/// <summary>
	///     T1: the config surface - the tail reserve derivation and the Bind round-trip on a real
	///     ConfigFile, the shape tools/CfgGen writes.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The expected rows are literals derived from AdvisorConfig's contract comments: the quota
	///         is the larger bound quota, the neighborhood is 1 + 8*(RangeLevels - 1) territories, each
	///         territory reserves quota + 8 tie slots, a sub-1 knob is clamped to 1, and the reserve
	///         saturates at MaxTailReserve.
	///     </para>
	/// </remarks>
	public class AdvisorConfigTests
	{
		[Fact]
		public void TailCapacityForTheBindDefaultsIsNinety()
		{
			Assert.Equal(90, AdvisorConfig.TailCapacityFor(2, 1, 2));
		}

		[Fact]
		public void TailCapacityForSingleHopIsOneTerritoryAndItsTieSet()
		{
			Assert.Equal(9, AdvisorConfig.TailCapacityFor(1, 1, 1));
		}

		[Fact]
		public void TailCapacityForClampsEveryKnobToOne()
		{
			Assert.Equal(9, AdvisorConfig.TailCapacityFor(0, 0, 0));
			Assert.Equal(9, AdvisorConfig.TailCapacityFor(-5, -1, -3));
		}

		[Fact]
		public void TailCapacityForTakesTheLargerQuotaAndAddsHopFanOut()
		{
			// (1 + 8*(3-1)) territories * (5 + 8 tie slots), the hovered quota winning.
			Assert.Equal(221, AdvisorConfig.TailCapacityFor(5, 3, 3));
		}

		[Fact]
		public void TailCapacityForSaturatesAtTheMaxReserve()
		{
			Assert.Equal(4096, AdvisorConfig.TailCapacityFor(1, 1, 1000));
		}

		[Fact]
		public void BindRoundTripsWrittenValuesThroughARealConfigFile()
		{
			string path = Path.GetTempFileName();
			try
			{
				var plugin = new BepInPlugin(AdvisorConfig.PluginGuid, AdvisorConfig.PluginName, "1.0.0");

				// First open: a fresh bind writes the defaults.
				var file = new ConfigFile(path, true, plugin);
				AdvisorConfig.Entries entries = AdvisorConfig.Bind(file);
				Assert.Equal(2, entries.TopHovered.Value);
				Assert.Equal(1, entries.TopNeighbour.Value);
				Assert.Equal(2, entries.RangeLevels.Value);

				entries.TopHovered.Value = 4;
				file.Save();

				// Second open of the same file: the written value survives, the untouched knobs
				// keep their defaults.
				var reopened = new ConfigFile(path, true, plugin);
				AdvisorConfig.Entries again = AdvisorConfig.Bind(reopened);
				Assert.Equal(4, again.TopHovered.Value);
				Assert.Equal(1, again.TopNeighbour.Value);
				Assert.Equal(2, again.RangeLevels.Value);
			}
			finally
			{
				File.Delete(path);
			}
		}
	}
}
