using System;
using System.Collections.Generic;
using System.Reflection;
using Amplitude;
using Amplitude.Mercury.Data.AI;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;
using SettlementPlacementAdvisor.gameapi;
using SnapshotTerritory = Amplitude.Mercury.Interop.AI.Entities.Territory;

namespace SettlementPlacementAdvisor.Tests
{
	/// <summary>
	///     Strict scripted game surface: every answer is scripted (territory graph, per-tile pre-filter verdicts,
	///     tile gate and army-level gate answers, scoring verdict, territory facts, frames), each with a scripted
	///     answer for the unanswered case, so expected pick sets are literals derived from the contract, never read
	///     back from the code under test. Call counters make cache serving and the reach of a scan observable.
	/// </summary>
	/// <remarks>
	///     <para>
	///         DistrictDefinition is abstract in the game and never constructed headless; the pipeline only
	///         forwards it to the (scripted) prefilter and gate.
	///     </para>
	/// </remarks>
	internal sealed class ScriptSurface : IGameSurface
	{
		public readonly SimulationEntityGUID HoveredSettlementGuid = new SimulationEntityGUID(99UL);
		public readonly Dictionary<int, TileVerdict> Prefilter = new Dictionary<int, TileVerdict>();

		public readonly HashSet<int> ResourceTiles = new HashSet<int>();
		public readonly Dictionary<int, int> ScoreOverride = new Dictionary<int, int>();

		public readonly Dictionary<int, Territory> Territories = new Dictionary<int, Territory>();

		public readonly Dictionary<int, ArmyActionFailureFlags>
			TileGate = new Dictionary<int, ArmyActionFailureFlags>();

		public bool ActivateAnswer = true;

		public int AdjacencyCalls;
		public object Army = new object();
		public bool ArmyGateAnswered = true;
		public int ArmyGateCalls;
		public ArmyActionFailureFlags ArmyGateFailures = ArmyActionFailureFlags.None;

		/// <summary>
		///     The only GUID that resolves to the placing army. A takeover point reading the hovered settlement
		///     GUID where the army GUID belongs gets no army, and publishes nothing.
		/// </summary>
		public SimulationEntityGUID ArmyGuid = new SimulationEntityGUID(1UL);

		public int ConstructionFrame = 1;
		public int CursorTile = -1;
		public bool DistrictDefinitionAnswer = true;
		public int DistrictFrame = 1;
		public int EmpireIndex = 1;
		public bool EvaluateTileAnswered = true;
		public int EvaluateTileCalls;
		public int FactsReads;
		public bool FramesAnswer = true;

		public ArmyActionType Mode = ArmyActionType.CreateCamp;
		public int TerritoryFrame = 1;
		public bool ThrowArmyGate;
		public bool TileGateAnswered = true;
		public int TileGateCalls;
		public bool TryReadModeAnswer = true;
		public int VisibilityFrame = 1;

		/// <summary>
		///     Patch targets come from the real binding pass: the patches bind the game methods the shipped
		///     adapter resolves, not names transcribed into the tests.
		/// </summary>
		public MethodBase PatchTarget(PatchPoint point)
		{
			return Probe.NewGameApi().PatchTarget(point);
		}

		public bool TryReadSnapshotActionType(SettlementPlacementCursorSnapshot snapshot, out ArmyActionType mode)
		{
			mode = Mode;
			return TryReadModeAnswer;
		}

		public int ReadSnapshotCursorTile(SettlementPlacementCursorSnapshot snapshot)
		{
			return CursorTile;
		}

		public bool TryActivateEvaluation(CampPlacementEvaluation evaluation)
		{
			return ActivateAnswer;
		}

		public object TryGetArmy(SimulationEntityGUID guid)
		{
			return guid == ArmyGuid
					   ? Army
					   : null;
		}

		public int GetArmyEmpireIndex(object army)
		{
			return EmpireIndex;
		}

		public bool TryGetDistrictDefinition(ArmyActionType mode, out DistrictDefinition districtDefinition)
		{
			districtDefinition = null;
			return DistrictDefinitionAnswer;
		}

		public TileVerdict TestTilePrefilter(int tileIndex, int empireIndex, DistrictDefinition districtDefinition)
		{
			return Prefilter.TryGetValue(tileIndex, out TileVerdict verdict)
					   ? verdict
					   : TileVerdict.Pass;
		}

		public bool IsTileResourceTile(int tileIndex)
		{
			return ResourceTiles.Contains(tileIndex);
		}

		public bool EvaluateTile(
			object army,
			int tileIndex,
			DistrictDefinition districtDefinition,
			out DistrictPlacementEvaluation.ValidTile validTile,
			out FixedPoint score)
		{
			EvaluateTileCalls++;

			DistrictPlacementEvaluation.ValidTile entry = default;
			entry.Initialize(tileIndex);
			validTile = entry;
			score = ScoreOverride.TryGetValue(tileIndex, out int raw)
						? raw * FixedPoint.One
						: tileIndex * FixedPoint.One;
			return EvaluateTileAnswered;
		}

		public bool GetCurrentFrames(out int districtFrame, out int constructionFrame, out int visibilityFrame)
		{
			districtFrame = DistrictFrame;
			constructionFrame = ConstructionFrame;
			visibilityFrame = VisibilityFrame;
			return FramesAnswer;
		}

		public bool GetTerritoryFrame(out int territoryFrame)
		{
			territoryFrame = TerritoryFrame;
			return FramesAnswer;
		}

		public int TerritoryOfTile(int tileIndex)
		{
			foreach (KeyValuePair<int, Territory> entry in Territories)
			{
				foreach (int tile in entry.Value.Tiles)
				{
					if (tile == tileIndex)
					{
						return entry.Key;
					}
				}
			}

			return -1;
		}

		public TerritoryFactsResult GetTerritoryFacts(int territoryIndex, out TerritoryFacts facts)
		{
			FactsReads++;
			if (!Territories.TryGetValue(territoryIndex, out Territory territory))
			{
				facts = default;
				return TerritoryFactsResult.Unknown;
			}

			facts = new TerritoryFacts
			{
				IsEdgeOfTheWorld = territory.IsEdgeOfTheWorld,
				Claimed = territory.Claimed,
				HasSettlement = territory.HasSettlement,
				IsOwnedByCity = territory.IsOwnedByCity,
				OwnerEmpireIndex = territory.OwnerEmpireIndex,
				Tiles = territory.Tiles
			};
			return TerritoryFactsResult.Known;
		}

		public bool TryGetAdjacentTerritories(int territoryIndex, out SnapshotTerritory.Adjacency[] edges)
		{
			AdjacencyCalls++;
			if (Territories.TryGetValue(territoryIndex, out Territory territory) && territory.Edges != null)
			{
				edges = territory.Edges;
				return true;
			}

			edges = null;
			return false;
		}

		public bool TryGetTileGateFailures(ArmyActionType mode, object army, int tileIndex, out GateAnswer answer)
		{
			TileGateCalls++;
			if (!TileGateAnswered)
			{
				answer = default;
				return false;
			}

			answer = new GateAnswer
			{
				Failures = TileGate.TryGetValue(tileIndex, out ArmyActionFailureFlags flags)
							   ? flags
							   : ArmyActionFailureFlags.None,
				Money = FixedPoint.Zero,
				Influence = FixedPoint.Zero,
				Unit = FixedPoint.Zero
			};
			return true;
		}

		public bool TryGetArmyGateFailures(ArmyActionType mode, object army, out ArmyActionFailureFlags failures)
		{
			ArmyGateCalls++;
			if (ThrowArmyGate)
			{
				throw new InvalidOperationException("scripted scan failure");
			}

			if (!ArmyGateAnswered)
			{
				failures = ArmyActionFailureFlags.None;
				return false;
			}

			failures = ArmyGateFailures;
			return true;
		}

		public ScriptSurface AddTerritory(int index, params int[] tiles)
		{
			Territories[index] = new Territory { Tiles = tiles };
			return this;
		}

		public ScriptSurface Link(int from, int to, TerritoryTransition transition = TerritoryTransition.Open)
		{
			Territory territory = Territories[from];
			SnapshotTerritory.Adjacency[] edges = territory.Edges ?? Array.Empty<SnapshotTerritory.Adjacency>();
			SnapshotTerritory.Adjacency[] next = new SnapshotTerritory.Adjacency[edges.Length + 1];
			Array.Copy(edges, next, edges.Length);
			next[edges.Length] = new SnapshotTerritory.Adjacency { TerritoryIndex = to, Transition = transition };
			territory.Edges = next;
			return this;
		}

		/// <summary>
		///     The game reports adjacency on both sides; a cursor move across the border needs it.
		/// </summary>
		public ScriptSurface LinkBoth(int from, int to, TerritoryTransition transition = TerritoryTransition.Open)
		{
			return Link(from, to, transition).Link(to, from, transition);
		}

		/// <summary>
		///     Standard graph: territory 0 = {101,102,103} (hovered), territory 1 = {201,202,203}, one edge
		///     0-&gt;1. Scores are the tile index, so ranked order per territory is 103,102,101.
		/// </summary>
		/// <returns>The surface every T1 rule test starts from.</returns>
		public static ScriptSurface Standard()
		{
			return new ScriptSurface()
				.AddTerritory(0, 101, 102, 103)
				.AddTerritory(1, 201, 202, 203)
				.Link(0, 1);
		}

		public sealed class Territory
		{
			public bool Claimed;

			// null: adjacency unanswered for this territory.
			public SnapshotTerritory.Adjacency[] Edges = Array.Empty<SnapshotTerritory.Adjacency>();
			public bool HasSettlement;
			public bool IsEdgeOfTheWorld;
			public bool IsOwnedByCity;
			public int OwnerEmpireIndex = -1;
			public int[] Tiles = Array.Empty<int>();
		}
	}
}
