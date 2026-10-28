using System;

namespace SettlementPlacementAdvisor.gameapi
{
	/// <summary>
	///     The binding pass stopping on a row the running game build does not expose: the game moved a member
	///     this mod was compiled against. The row label is the load failure line's subject.
	/// </summary>
	/// <remarks>
	///     <para>A game-build drift is a load failure, never a silent disable.</para>
	/// </remarks>
	internal sealed class GameBuildDriftException : InvalidOperationException
	{
		public GameBuildDriftException(string binding) : base("game build drift: unresolved binding " + binding)
		{
			Binding = binding;
		}

		public string Binding { get; }
	}
}
