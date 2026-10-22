using System;
using BepInEx;
using BepInEx.Configuration;
using SettlementPlacementAdvisor;

namespace CfgGen
{
    /// <summary>
    /// Writes the pre-generated advisor cfg. Usage: CfgGen [version] [output-path].
    /// The version argument lands in the BepInEx header line, so a release cfg
    /// reads "created by plugin SettlementPlacementAdvisor v1.0.0".
    /// Same ConfigFile writer the game uses; the output is byte-shaped identical.
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
