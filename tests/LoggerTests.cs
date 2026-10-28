using SettlementPlacementAdvisor.logger;
using Xunit;

namespace SettlementPlacementAdvisor.Tests
{
	/// <summary>
	///     The SPA tag rule: every line SpaLogger sends to the BepInEx log opens with the tag, and the tag
	///     is written in the one place its contract names.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Asserted on the strings Probe captures from the log source's events, the headless seam for
	///         ManualLogSource. The expected lines are literals taken from SpaLogger's contract comments, the
	///         tag in front and the failure wording after it.
	///     </para>
	///     <para>
	///         The headless ceiling: Fatal also appends the load-failure file, which needs the BepInEx root
	///         path the test host has no answer for, so Fatal's BepInEx line and the stamp that file line
	///         carries stay the in-game smoke check.
	///     </para>
	/// </remarks>
	public class LoggerTests
	{
		[Fact]
		public void InfoAndFailureLinesOpenWithTheTag()
		{
			Probe.Start();
			// The reset publish is not part of this row.
			Probe.LogLines.Clear();

			SpaLogger.Log("territory adjacencies resolved");
			SpaLogger.Error("tile gate CreateCamp: index out of range");

			Assert.Equal(
				new[]
				{
					"[SPA] territory adjacencies resolved",
					"[SPA] advisor failure: tile gate CreateCamp: index out of range"
				},
				Probe.LogLines);
		}

		[Fact]
		public void TheTagIsWrittenOncePerLine()
		{
			Probe.Start();
			Probe.LogLines.Clear();

			SpaLogger.Log("publish picks=2");

			foreach (string line in Probe.LogLines)
			{
				Assert.StartsWith("[SPA] ", line);
				Assert.Equal(0, line.LastIndexOf("[SPA] "));
			}
		}
	}
}
