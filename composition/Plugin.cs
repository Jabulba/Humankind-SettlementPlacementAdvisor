using System;
using System.Diagnostics;
using System.Threading;
using BepInEx;
using HarmonyLib;
using SettlementPlacementAdvisor.gameapi;
using SettlementPlacementAdvisor.logger;
using SettlementPlacementAdvisor.pipeline;

namespace SettlementPlacementAdvisor.composition
{
	/// <summary>
	///     The load gate. Awake is the mod's one-load decision: it resolves the game surface, fills the
	///     composition hand-off, applies the patches, and closes the game on any failure. No status flag carries
	///     that decision, and nothing later re-checks it.
	/// </summary>
	[BepInPlugin(AdvisorConfig.PluginGuid, AdvisorConfig.PluginName, ModVersion.Value)]
	public class Plugin : BaseUnityPlugin
	{
		/// <summary>
		///     A stopping binding pass leaves through GameBuildDriftException naming its row, and this turns it
		///     into one Fatal line beside the build pair: the build the mod was compiled for
		///     (GameBuild.Supported, read at build time from the compiled Assembly-CSharp.dll's Version
		///     attribute) and the build the player runs (GameApi.GameBuild). A differing build points at a mod
		///     update, a build equal to the supported one at an incompatible mod installed beside this one.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Any other exception takes the same fatal path. A surface that was constructed has every row
		///         bound, so no runtime path re-checks a binding.
		///     </para>
		/// </remarks>
		private void Awake()
		{
			try
			{
				SpaLogger.Init(Logger);

				AdvisorConfig.Entries cfg = AdvisorConfig.Bind(Config);
				SpaRuntime.TopHovered = cfg.TopHovered.Value;
				SpaRuntime.TopNeighbour = cfg.TopNeighbour.Value;
				SpaRuntime.RangeLevels = cfg.RangeLevels.Value;
				SpaRuntime.TailCapacity = AdvisorConfig.TailCapacityFor(SpaRuntime.TopHovered, SpaRuntime.TopNeighbour,
					SpaRuntime.RangeLevels);

				IGameSurface gameApi = new GameApi();

				SpaLogger.Log($"GameApi members resolved; tail capacity {SpaRuntime.TailCapacity}");

				EvaluationWriter writer = new EvaluationWriter(gameApi);
				SpaRuntime.GameApi = gameApi;
				SpaRuntime.Writer = writer;
				SpaRuntime.Advisor = new Advisor(gameApi, new PickComputation(gameApi), writer);

				new Harmony(AdvisorConfig.PluginGuid).PatchAll(typeof(Plugin).Assembly);
				SpaLogger.Log("SPA advisor patches applied.");
			}
			catch (GameBuildDriftException e)
			{
				ReportFatalFailure(FailureLine(e.Binding, GameBuild.Supported, GameApi.GameBuild));
			}
			catch (Exception e)
			{
				ReportFatalFailure($"Unhandled exception when loading the mod: {e.Message}");
			}
		}

		private static void ReportFatalFailure(string message)
		{
			SpaLogger.Fatal(message);
			Thread.Sleep(2500); // Wait for BepInEx?
			Process.GetCurrentProcess().Kill();
		}


		/// <summary>
		///     The player-facing line: the unresolved row, both game builds, and the advice the pair's equality
		///     picks. A pure string, so the headless tests pin it without instantiating the Unity-bound entry
		///     point.
		/// </summary>
		/// <remarks>
		///     <para>internal, not private: the test assembly calls it, and private would break that row.</para>
		/// </remarks>
		/// <param name="binding">The unresolved binding row the pass stopped at.</param>
		/// <param name="supported">The game build the mod was compiled for.</param>
		/// <param name="running">The game build the player runs.</param>
		internal static string FailureLine(string binding, string supported, string running)
		{
			string message;
			if (running == supported)
			{
				message = "The mod was built for your current version of the game, "
						  + supported
						  + ". If you have other mods installed, they may be incompatible. Test with this mod alone;"
						  + " if the problem persists, report this line on the issues page.";
			}
			else
			{
				message = "The mod was built for game version "
						  + supported
						  + ", but you are running version "
						  + running
						  + ". Update the mod to a version that supports your game version, or report this line on the"
						  + " issues page.";
			}

			return "Load failure: the mod's binding pass failed at " + binding + ". " + message;
		}
	}
}
