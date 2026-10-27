using System;
using BepInEx;
using BepInEx.Configuration;
using SettlementPlacementAdvisor;

namespace CfgGen
{
    /// <summary>
    /// Writes the pre-generated advisor cfg. Usage: CfgGen [version] [output-path]. Uses the
    /// game's ConfigFile writer, so output matches a game-written file. The version lands in
    /// the BepInEx header line.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string version = args.Length > 0 ? args[0] : "0.0.0-dev";
            string outPath = args.Length > 1
                ? args[1]
                : "com.jabulba.settlementplacementadvisor.cfg";

            var file = new ConfigFile(outPath, false,
                new BepInPlugin(AdvisorConfig.PluginGuid, AdvisorConfig.PluginName, version));
            file.SaveOnConfigSet = false;
            AdvisorConfig.Bind(file);
            file.Save();

            Console.WriteLine(outPath);
            return 0;
        }
    }
}
