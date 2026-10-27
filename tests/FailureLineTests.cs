using SettlementPlacementAdvisor.composition;
using Xunit;

namespace SettlementPlacementAdvisor.Tests
{
	/// <summary>
	///     The player-facing load failure line a stopping binding pass ends in, asserted on Plugin's builder.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Plugin.cs compiles into this assembly, and only the pure string method is called: Awake is
	///         Unity-bound and stays the manual in-game smoke gate.
	///     </para>
	/// </remarks>
	public class FailureLineTests
	{
		private const string Row = "Sandbox.SimulationEvaluator";

		private const string Built = "1.31.4851.741546";

		[Fact]
		public void TheLineIsOneLineNamingTheRowAndBothGameBuilds()
		{
			string line = Plugin.FailureLine(Row, Built, "1.0.31.4900");

			Assert.DoesNotContain("\n", line);
			Assert.Contains("binding pass failed at " + Row, line);
			Assert.Contains(Built, line);
			Assert.Contains("1.0.31.4900", line);
		}

		[Fact]
		public void ADifferingGameBuildPointsAtAModUpdate()
		{
			string line = Plugin.FailureLine(Row, Built, "1.0.31.4900");

			Assert.Contains("you are running version 1.0.31.4900", line);
			Assert.Contains("Update the mod to a version that supports your game version", line);
			Assert.DoesNotContain("other mods", line);
		}

		[Fact]
		public void AMatchingGameBuildPointsAtTheOtherModsThePlayerRuns()
		{
			string line = Plugin.FailureLine(Row, Built, Built);

			Assert.Contains("built for your current version of the game, " + Built, line);
			Assert.Contains("other mods installed, they may be incompatible", line);
			Assert.DoesNotContain("Update the mod", line);
		}
	}
}
