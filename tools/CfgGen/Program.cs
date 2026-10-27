using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using SettlementPlacementAdvisor.composition;

namespace CfgGen
{
	internal static class Program
	{
		// The cfg header carries the plugin version from the tag; the game build is not an argument
		// and never hardcoded: GameBuildReader, the plugin build's own reader, reads it from the
		// Assembly-CSharp.dll copy this tool runs on. A build that reader cannot read stops the
		// release step, so no cfg is written by assemblies whose provenance is unknown.
		private static int Main(string[] args)
		{
			string version = args.Length > 0
								 ? args[0]
								 : "0.0.0-dev";
			string outPath = args.Length > 1
								 ? args[1]
								 : "com.jabulba.settlementplacementadvisor.cfg";

			string gameAssembly = Path.Combine(AppContext.BaseDirectory, "Assembly-CSharp.dll");
			string gameBuild;
			string readFailure;
			if (!GameBuildReader.TryRead(gameAssembly, out gameBuild, out readFailure))
			{
				Console.Error.WriteLine("cfggen: " + readFailure);
				return 1;
			}

			ConfigFile file = new ConfigFile(outPath, false,
				new BepInPlugin(AdvisorConfig.PluginGuid, AdvisorConfig.PluginName, version));
			AdvisorConfig.Bind(file);
			file.Save();

			Console.WriteLine(outPath);
			Console.WriteLine("game build: " + gameBuild + " (read from " + gameAssembly + ")");
			return 0;
		}
	}
}
