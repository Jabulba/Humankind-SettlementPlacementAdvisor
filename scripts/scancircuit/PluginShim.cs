namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// DEBUG-harness stand-in for Plugin so GameApi.Fail can log. Instance stays null: the
    /// harness reads picks and gate counts, not failures.
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
