namespace CampPlacementAdvisor
{
    /// <summary>
    /// DEBUG-harness stand-in for the BepInEx plugin class: GameApi.Fail logs through
    /// Plugin.Instance. Null Instance = failures silently ignored, which is what the
    /// harness wants (the harness reads the picks, not the log).
    /// </summary>
    internal sealed class Plugin
    {
        internal static Plugin Instance;

        internal void Log(string message)
        {
            System.Console.WriteLine("[shim] " + message);
        }
    }
}
