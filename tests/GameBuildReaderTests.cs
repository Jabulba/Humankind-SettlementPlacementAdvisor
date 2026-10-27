using System.Reflection;
using Xunit;

namespace SettlementPlacementAdvisor.Tests
{
	/// <summary>
	///     The build-time reader behind GameBuild.Supported (tools/GameBuild), compiled into this
	///     assembly so its decode contract is pinned: the stamp is the Version attribute of the
	///     Assembly-CSharp.dll the compile is given, and every unreadable case reports a failure.
	///     The plugin build stops on a failure, so a failure reported here is a loud build stop,
	///     never a guessed stamp shipped in a release.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The assembly under test is the resolved reference of this project, so the read runs on
	///         the package copy in feed mode and on the install copy in local mode: the same file the
	///         plugin build reads.
	///     </para>
	///     <para>
	///         Segment values are not asserted here: the game build is a moving number, and pin
	///         equality belongs to the load-failure line and to the build's label warnings, not to a
	///         fixture.
	///     </para>
	/// </remarks>
	public class GameBuildReaderTests
	{
		private static string CompiledGameAssembly
		{
			get
			{
				return Assembly.Load(new AssemblyName("Assembly-CSharp")).Location;
			}
		}

		[Fact]
		public void TheCompiledGameAssemblyReportsFourNonZeroSegments()
		{
			string build;
			string failure;

			bool read = GameBuildReader.TryRead(CompiledGameAssembly, out build, out failure);

			Assert.True(read, failure);
			Assert.Null(failure);
			Assert.Matches(new System.Text.RegularExpressions.Regex(@"^[1-9]\d*\.\d+\.\d+\.[1-9]\d*$"), build);
		}

		[Fact]
		public void TheReadIsStableAcrossCalls()
		{
			string first;
			string second;
			string failure;

			Assert.True(GameBuildReader.TryRead(CompiledGameAssembly, out first, out failure));
			Assert.True(GameBuildReader.TryRead(CompiledGameAssembly, out second, out failure));

			Assert.Equal(first, second);
		}

		[Fact]
		public void AnAssemblyThatIsNotAGameAssemblyFailsNamingTheFile()
		{
			// A reference row pointing at a foreign assembly: the read stops, so the plugin build
			// stops. The reason is the reader's own (metadata resolution, a missing attribute, an
			// unreadable image); what the build relies on is that the file is named.
			string path = typeof(GameBuildReader).Assembly.Location;
			string build;
			string failure;

			bool read = GameBuildReader.TryRead(path, out build, out failure);

			Assert.False(read);
			Assert.Null(build);
			Assert.StartsWith(path, failure);
			Assert.True(failure.Length > path.Length, failure);
		}

		[Fact]
		public void AMissingFileFailsNamingThePath()
		{
			string build;
			string failure;

			bool read = GameBuildReader.TryRead("settlement-placement-advisor-absent.dll", out build, out failure);

			Assert.False(read);
			Assert.Null(build);
			Assert.Contains("file not found", failure);
		}
	}
}
