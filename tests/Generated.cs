namespace SettlementPlacementAdvisor
{
	/// <summary>
	///     The plugin build emits ModVersion and GameBuild as generated constants (GenerateModVersion in
	///     SettlementPlacementAdvisor.csproj); the test compile provides them so SpaLogger and the binding rows
	///     compile.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The values are inert in tests: GameApi.GameBuild reads the game's version statics, not these
	///         constants.
	///     </para>
	/// </remarks>
	internal static class ModVersion
	{
		public const string Value = "0.0.0";
	}

	internal static class GameBuild
	{
		public const string Supported = "1.31.4851.741546";
	}
}
