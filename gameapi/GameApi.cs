using System;
using System.Collections.Generic;
using System.Reflection;
using Amplitude;
using Amplitude.Mercury;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Data.World;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Interop.AI.Data;
using Amplitude.Mercury.Simulation;
using SettlementPlacementAdvisor.logger;
using AiSnapshots = Amplitude.Mercury.Interop.AI.Snapshots;
using Application = Amplitude.Framework.Application;
using SnapshotTerritory = Amplitude.Mercury.Interop.AI.Entities.Territory;
using Version = Amplitude.Framework.Version;
using WorldSnapshot = Amplitude.Mercury.Interop.AI.WorldSnapshot;

namespace SettlementPlacementAdvisor.gameapi
{
	/// <summary>
	///     The game surface's reflection adapter. Its constructor binds every reflected member name at startup
	///     through the binding pass; the pass stops at its first unresolved row and leaves through
	///     GameBuildDriftException naming that row, which Plugin.Awake turns into a hard, loud load failure that
	///     closes the game. A surface that was constructed therefore has every row bound: no Ready flag, no
	///     unbound-member branch, no null delegate, and no runtime path that re-checks a binding.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The constructor also compiles the hot members (the three tile gates, the terrain/POI and
	///         explored checks, the scoring primitives, the tile-to-position writer) into typed delegates, so the
	///         scan never pays reflection overhead per tile.
	///     </para>
	///     <para>
	///         Once constructed, the binding tables are read-only: no mid-game mutation, and the one lazy read
	///         cache, the open-generic member rebinds, is simulation-thread only.
	///     </para>
	/// </remarks>
	internal sealed class GameApi : IGameSurface
	{
		internal const string SandboxEvaluator = "Sandbox.SimulationEvaluator";
		internal const string SandboxRepository = "Sandbox.SimulationEntityRepository";
		internal const string SandboxWorld = "Sandbox.World";
		internal const string SandboxVisibility = "Sandbox.VisibilityController";
		internal const string SandboxEmpires = "Sandbox.Empires";
		internal const string RepositoryLookup = "SimulationEntityRepository.TryGetSimulationEntity";
		internal const string EvaluateOne = "SimulationEvaluator.EvaluateOneSettlementPlacement";
		internal const string Ponderation = "SimulationEvaluator.settlementScorePonderation";
		internal const string PonderationInit = "SimulationEvaluator.InitSettlementScorePonderation";

		internal const string TerrainAndPoi = "ConstructibleHelper.CheckTerrainAndPOI";

		internal const string Explored = "VisibilityController.IsWorldPositionExplored";
		internal const string WorldTerritoryInfo = "World.TerritoryInfo";
		internal const string TerritoryInfoEdge = "TerritoryInfo.IsEdgeOfTheWorld";
		internal const string TerritoryInfoClaimed = "TerritoryInfo.Claimed";
		internal const string TerritoryInfoSettlement = "TerritoryInfo.SettlementGUID";
		internal const string TerritoryInfoEmpire = "TerritoryInfo.EmpireIndex";
		internal const string SnapshotTerritoryInfo = "WorldSnapshot.TerritoryInfo";
		internal const string SnapshotAdjacencies = "Territory.Adjacencies";
		internal const string SnapshotTileIndexes = "Territory.TileIndexes";
		internal const string TileToPosition = "WorldPosition.GetHexagonOffsetFromTileIndex";

		internal const string EvaluationInitialize = "CampPlacementEvaluation.Initialize(CustomTileIndexArea)";
		internal const string PatchEvaluatePlacement = "Patch.SettlementPlacementCursorSnapshot.EvaluatePlacement";
		internal const string PatchUpdateCurrentPosition = "Patch.CampPlacementEvaluation.UpdateCurrentPosition";
		internal const string PatchDeactivateCursor = "Patch.SettlementPlacementCursor.DeactivateCursor";
		internal const string PatchPinMustMeShown = "Patch.SettlementPlacementPinsSubset.PinMustMeShown";
		internal const string PatchSandboxStart = "Patch.SandboxManager.Start";

		internal const string WorldDistrictInfo = "World.DistrictInfo";
		internal const string WorldConstructionInfo = "World.ConstructionInfo";
		internal const string PoolAllocatorFrame = "PoolAllocator.Frame";
		internal const string ArrayWithFrameCounter = "ArrayWithFrame.Frame";
		internal const string VisibilityFrame = "VisibilityController.Frame";
		internal const string EmpireVisionBits = "MajorEmpire.Bits";
		internal const string ArmyEmpireIndex = "Army.EmpireIndex";
		internal const string SnapshotActionType = "SettlementPlacementCursorSnapshot.actionType";
		internal const string SnapshotCursorPosition = "ConstructiblePlacementCursorSnapshot.nextConstructiblePosition";
		internal const string SnapshotType = "Amplitude.Mercury.Interop.SettlementPlacementCursorSnapshot";

		private static readonly int[] SNoTiles = Array.Empty<int>();
		private readonly Dictionary<string, Delegate> _mCompiled;

		private readonly Dictionary<string, FieldInfo> _mFields;
		private readonly Dictionary<string, MemberInfo> _mMembers;
		private readonly Dictionary<string, MethodInfo> _mMethods;
		private readonly Dictionary<string, MemberInfo> _mOpenGenericReads = new Dictionary<string, MemberInfo>();

		internal GameApi()
		{
			BindingPass bindingPass = new BindingPass();

			_mFields = bindingPass.Fields;
			_mMethods = bindingPass.Methods;
			_mMembers = bindingPass.Members;
			_mCompiled = bindingPass.Compiled;
		}

		public static string GameBuild
		{
			get
			{
				Version version = Application.Version;
				if (version.Major == 0 && version.Minor == 0 && version.Revision == 0 && version.Build == 0)
				{
					return "unknown";
				}

				return version.Major + "." + version.Minor + "." + version.Revision + "." + version.Build;
			}
		}

		public MethodBase PatchTarget(PatchPoint point)
		{
			string label;
			switch (point)
			{
				case PatchPoint.EvaluatePlacement:
					label = PatchEvaluatePlacement;
					break;
				case PatchPoint.UpdateCurrentPosition:
					label = PatchUpdateCurrentPosition;
					break;
				case PatchPoint.EvaluationInitialize:
					label = EvaluationInitialize;
					break;
				case PatchPoint.DeactivateCursor:
					label = PatchDeactivateCursor;
					break;
				case PatchPoint.PinMustMeShown:
					label = PatchPinMustMeShown;
					break;
				case PatchPoint.SandboxStart:
					label = PatchSandboxStart;
					break;
				default:
					SpaLogger.Error("unknown patch point " + point);
					return null;
			}

			return Method(label);
		}

		public bool TryReadSnapshotActionType(SettlementPlacementCursorSnapshot snapshot, out ArmyActionType mode)
		{
			mode = default;
			object stamped = MemberValue(snapshot, SnapshotActionType);
			if (!(stamped is ArmyActionType actionType))
			{
				return false;
			}

			mode = actionType;
			return true;
		}

		public int ReadSnapshotCursorTile(SettlementPlacementCursorSnapshot snapshot)
		{
			object boxed = MemberValue(snapshot, SnapshotCursorPosition);
			if (!(boxed is WorldPosition position))
			{
				return -1;
			}

			return position.IsWorldPositionValid()
					   ? position.ToTileIndex()
					   : -1;
		}

		public bool TryActivateEvaluation(CampPlacementEvaluation evaluation)
		{
			if (evaluation == null)
			{
				return false;
			}

			try
			{
				MethodInfo initialize = Method(EvaluationInitialize);
				initialize.Invoke(evaluation, new object[] { null });
				return true;
			}
			catch (Exception e)
			{
				SpaLogger.Error("activation Initialize: " + e.Message);
				return false;
			}
		}

		public object TryGetArmy(SimulationEntityGUID guid)
		{
			try
			{
				object repository = Field(SandboxRepository).GetValue(null);
				if (repository == null)
				{
					return null;
				}

				MethodInfo lookup = Method(RepositoryLookup);
				object[] args = { guid, null };
				bool ok = (bool)lookup.Invoke(repository, args);
				return ok
						   ? args[1]
						   : null;
			}
			catch (Exception e)
			{
				SpaLogger.Error("TryGetSimulationEntity: " + e.Message);
				return null;
			}
		}

		public int GetArmyEmpireIndex(object army)
		{
			return army == null
					   ? -1
					   : GetIntMember(army, ArmyEmpireIndex);
		}

		public bool TryGetDistrictDefinition(ArmyActionType mode, out DistrictDefinition districtDefinition)
		{
			districtDefinition = null;
			if (!ModePolicy.TryGetRow(mode, out ModeRow row))
			{
				return false;
			}

			try
			{
				districtDefinition = Field(row.DefinitionLabel).GetValue(null) as DistrictDefinition;
				return districtDefinition != null;
			}
			catch (Exception e)
			{
				SpaLogger.Error("district definition " + row.DefinitionLabel + ": " + e.Message);
				return false;
			}
		}

		public TileVerdict TestTilePrefilter(int tileIndex, int empireIndex, DistrictDefinition districtDefinition)
		{
			if (IsTileWater(tileIndex)
				|| !IsTileExplored(tileIndex, empireIndex)
				|| !IsSettlementSiteTile(districtDefinition, tileIndex))
			{
				return TileVerdict.Drop;
			}

			return TileVerdict.Pass;
		}

		public bool IsTileResourceTile(int tileIndex)
		{
			try
			{
				WorldSnapshot world = AiSnapshots.World;
				TileInfo[] tileInfo = world?.TileInfo;
				PointOfInterestDefinition[] pointsOfInterest = world?.PointOfInterestDefinitions;
				if (tileInfo == null || pointsOfInterest == null)
				{
					return true;
				}

				if (tileIndex < 0 || tileIndex >= tileInfo.Length)
				{
					return true;
				}

				byte pointOfInterest = tileInfo[tileIndex].PointOfInterest;
				if (pointOfInterest == byte.MaxValue)
				{
					return false;
				}

				if (pointOfInterest >= pointsOfInterest.Length)
				{
					return true;
				}

				return pointsOfInterest[pointOfInterest] is ResourceDepositDefinition;
			}
			catch (Exception e)
			{
				SpaLogger.Error("resource tile lookup: " + e.Message);
				return true;
			}
		}

		public bool EvaluateTile(
			object army,
			int tileIndex,
			DistrictDefinition districtDefinition,
			out DistrictPlacementEvaluation.ValidTile validTile,
			out FixedPoint score)
		{
			validTile = default;
			score = FixedPoint.Zero;
			if (army == null || districtDefinition == null)
			{
				return false;
			}

			try
			{
				object evaluator = Field(SandboxEvaluator).GetValue(null);
				if (evaluator == null)
				{
					return false;
				}

				DistrictPlacementEvaluation.ValidTile vt = default;
				vt.Initialize(tileIndex);
				((EvaluateOnePlacement)GetCompiled(EvaluateOne))(evaluator, army, ref vt, tileIndex, districtDefinition,
					null);

				FimsInfo fims = vt.OverallFims;
				FimsInfo ponderation = SettlementWeightTable(evaluator);
				fims.Multiply(ref ponderation);

				validTile = vt;
				score = fims.GetTotalFims();

				return true;
			}
			catch (Exception e)
			{
				SpaLogger.Error("EvaluateOneSettlementPlacement: " + e.Message);
				return false;
			}
		}

		public bool GetCurrentFrames(out int districtFrame, out int constructionFrame, out int visibilityFrame)
		{
			districtFrame = constructionFrame = visibilityFrame = 0;
			try
			{
				object world = Field(SandboxWorld).GetValue(null);
				object visibilityController = Field(SandboxVisibility).GetValue(null);
				if (world == null || visibilityController == null)
				{
					return false;
				}

				object districtInfo = MemberValue(world, WorldDistrictInfo);
				object constructionInfo = MemberValue(world, WorldConstructionInfo);
				if (districtInfo == null || constructionInfo == null)
				{
					return false;
				}

				districtFrame = GetIntMember(districtInfo, PoolAllocatorFrame);
				constructionFrame = GetIntMember(constructionInfo, PoolAllocatorFrame);
				visibilityFrame = GetIntMember(visibilityController, VisibilityFrame);
				return true;
			}
			catch (Exception e)
			{
				SpaLogger.Error("frame counters: " + e.Message);
				return false;
			}
		}

		public bool GetTerritoryFrame(out int territoryFrame)
		{
			territoryFrame = 0;
			try
			{
				object world = Field(SandboxWorld).GetValue(null);
				if (world == null)
				{
					return false;
				}

				object territoryInfo = Field(WorldTerritoryInfo).GetValue(world);
				if (territoryInfo == null)
				{
					return false;
				}

				object frame = MemberValue(territoryInfo, ArrayWithFrameCounter);
				if (!(frame is int frameInt))
				{
					return false;
				}

				territoryFrame = frameInt;
				return true;
			}
			catch (Exception e)
			{
				SpaLogger.Error("claim/exploration frame: " + e.Message);
				return false;
			}
		}

		public int TerritoryOfTile(int tileIndex)
		{
			try
			{
				WorldSnapshot world = AiSnapshots.World;
				TileInfo[] tileInfo = world?.TileInfo;
				if (tileInfo == null || tileIndex < 0 || tileIndex >= tileInfo.Length)
				{
					return -1;
				}

				return tileInfo[tileIndex].TerritoryIndex;
			}
			catch (Exception e)
			{
				SpaLogger.Error("TileInfo territory lookup: " + e.Message);
				return -1;
			}
		}

		public TerritoryFactsResult GetTerritoryFacts(int territoryIndex, out TerritoryFacts facts)
		{
			facts = default;
			facts.Tiles = SNoTiles;
			try
			{
				WorldSnapshot world = AiSnapshots.World;
				TerritoryInfo[] infos = world?.TerritoryInfo;
				SnapshotTerritory[] territories = world?.Territories;
				if (infos == null
					|| territories == null
					|| territoryIndex < 0
					|| territoryIndex >= infos.Length
					|| territoryIndex >= territories.Length)
				{
					return TerritoryFactsResult.Unknown;
				}

				TerritoryInfo info = infos[territoryIndex];
				facts.IsEdgeOfTheWorld = info.IsEdgeOfTheWorld;
				facts.Claimed = info.Claimed;
				facts.HasSettlement = info.SettlementGUID != 0UL;
				facts.IsOwnedByCity = info.IsOwnedByCity;
				facts.OwnerEmpireIndex = info.EmpireIndex == byte.MaxValue
											 ? -1
											 : info.EmpireIndex;

				int[] siteTiles = territories[territoryIndex].TileIndexes;
				if (siteTiles != null)
				{
					facts.Tiles = siteTiles;
				}

				return TerritoryFactsResult.Known;
			}
			catch (Exception e)
			{
				SpaLogger.Error("territory facts: " + e.Message);
				return TerritoryFactsResult.Unknown;
			}
		}

		public bool TryGetAdjacentTerritories(int territoryIndex, out SnapshotTerritory.Adjacency[] edges)
		{
			edges = null;
			try
			{
				WorldSnapshot world = AiSnapshots.World;
				SnapshotTerritory[] territories = world?.Territories;
				if (territories == null
					|| territoryIndex < 0
					|| territoryIndex >= territories.Length)
				{
					return false;
				}

				SnapshotTerritory.Adjacency[] reported = territories[territoryIndex].Adjacencies;
				if (reported == null)
				{
					return false;
				}

				edges = reported;
				return true;
			}
			catch (Exception e)
			{
				SpaLogger.Error("territory adjacencies: " + e.Message);
				return false;
			}
		}

		public bool TryGetTileGateFailures(ArmyActionType mode, object army, int tileIndex, out GateAnswer answer)
		{
			answer = default;
			if (army == null)
			{
				return false;
			}

			if (!ModePolicy.TryGetRow(mode, out ModeRow row))
			{
				return false;
			}

			try
			{
				switch (row.TileGateShape)
				{
					case BindingShape.CampTile:
					{
						CampTileGate campGate = (CampTileGate)GetCompiled(row.TileGateLabel);
						if (campGate == null)
						{
							return false;
						}

						GateCosts costs = new GateCosts();
						ArmyActionFailureFlags failures = campGate(army, tileIndex, ref costs);
						answer = new GateAnswer
						{
							Failures = failures, Money = costs.Money, Influence = costs.Influence, Unit = costs.Unit
						};
						break;
					}

					case BindingShape.PositionFlags:
					{
						PositionTileGate positionGate = (PositionTileGate)GetCompiled(row.TileGateLabel);
						if (positionGate == null)
						{
							return false;
						}

						PositionCarrier carrier = new PositionCarrier();
						((TilePositionWriter)GetCompiled(TileToPosition))(ref carrier, tileIndex);
						ArmyActionFailureFlags failures = ArmyActionFailureFlags.None;
						failures = positionGate(army, carrier, ref failures);
						answer = new GateAnswer { Failures = failures };
						break;
					}

					case BindingShape.TileFlags:
					{
						TileFlagsTileGate tileGate = (TileFlagsTileGate)GetCompiled(row.TileGateLabel);
						if (tileGate == null)
						{
							return false;
						}

						ArmyActionFailureFlags failures = ArmyActionFailureFlags.None;
						failures = tileGate(army, tileIndex, ref failures);
						answer = new GateAnswer { Failures = failures };
						break;
					}

					default:
						return false;
				}
			}
			catch (Exception e)
			{
				SpaLogger.Error("tile gate " + mode + ": " + e.Message);
				return false;
			}

			return true;
		}

		public bool TryGetArmyGateFailures(ArmyActionType mode, object army, out ArmyActionFailureFlags failures)
		{
			failures = ArmyActionFailureFlags.None;
			if (army == null)
			{
				return false;
			}

			if (!ModePolicy.TryGetRow(mode, out ModeRow row))
			{
				return false;
			}

			try
			{
				for (int i = 0; i < row.ArmyGateLabels.Length; i++)
				{
					object[] args = GateArguments(row.ArmyGateShapes[i], army, failures, FixedPoint.Zero,
						FixedPoint.Zero,
						FixedPoint.Zero);
					if (args == null)
					{
						return false;
					}

					Method(row.ArmyGateLabels[i]).Invoke(null, args);
					failures = (ArmyActionFailureFlags)args[1];
				}
			}
			catch (Exception e)
			{
				SpaLogger.Error("army gate " + mode + ": " + e.Message);
				return false;
			}

			return true;
		}

		/// <summary>
		///     True when the label's compiled delegate exists. Test-only utility: the binding tests assert the hot
		///     labels the scan calls through. The member is internal, not private, because the tests call it.
		/// </summary>
		internal bool IsHotMemberBound(string label)
		{
			return _mCompiled.TryGetValue(label, out _);
		}

		private bool IsSettlementSiteTile(DistrictDefinition districtDefinition, int tileIndex)
		{
			if (districtDefinition == null)
			{
				return false;
			}

			try
			{
				Func<DistrictDefinition, int, bool> terrainAndPoi = (Func<DistrictDefinition, int, bool>)GetCompiled(
					TerrainAndPoi);
				return terrainAndPoi(districtDefinition, tileIndex);
			}
			catch (Exception e)
			{
				SpaLogger.Error("CheckTerrainAndPOI: " + e.Message);
				return false;
			}
		}

		private static bool IsTileWater(int tileIndex)
		{
			try
			{
				WorldSnapshot world = AiSnapshots.World;
				Tile[] tiles = world?.Tiles;
				if (tiles == null || tileIndex < 0 || tileIndex >= tiles.Length)
				{
					return true;
				}

				return tiles[tileIndex].IsWater;
			}
			catch (Exception e)
			{
				SpaLogger.Error("tile water lookup: " + e.Message);
				return true;
			}
		}

		private bool IsTileExplored(int tileIndex, int empireIndex)
		{
			if (empireIndex < 0)
			{
				return true;
			}

			try
			{
				object visibilityController = Field(SandboxVisibility).GetValue(null);
				if (visibilityController == null)
				{
					return true;
				}

				int bits = GetEmpireVisionBits(empireIndex);
				return bits == 0
					   || ((Func<object, int, int, bool>)GetCompiled(Explored))(visibilityController, tileIndex, bits);
			}
			catch (Exception e)
			{
				SpaLogger.Error("IsWorldPositionExplored: " + e.Message);
				return true;
			}
		}

		private FimsInfo SettlementWeightTable(object evaluator)
		{
			FimsInfo ponderation = (FimsInfo)Field(Ponderation).GetValue(evaluator);
			if (!ponderation.IsEmpty)
			{
				return ponderation;
			}

			((Action<object>)GetCompiled(PonderationInit))(evaluator);
			return (FimsInfo)Field(Ponderation).GetValue(evaluator);
		}

		private static object[] GateArguments(
			BindingShape shape,
			object army,
			ArmyActionFailureFlags failures,
			FixedPoint moneyCost,
			FixedPoint influenceCost,
			FixedPoint unitCost)
		{
			switch (shape)
			{
				case BindingShape.CampArmy:
					return new[] { army, failures, moneyCost, influenceCost, unitCost, false };

				case BindingShape.ArmyFlags:
					return new[] { army, failures };

				default:
					return null;
			}
		}

		private FieldInfo Field(string label)
		{
			return _mFields.TryGetValue(label, out FieldInfo field)
					   ? field
					   : null;
		}

		private MethodInfo Method(string label)
		{
			return _mMethods.TryGetValue(label, out MethodInfo method)
					   ? method
					   : null;
		}

		private Delegate GetCompiled(string label)
		{
			return _mCompiled.TryGetValue(label, out Delegate compiled)
					   ? compiled
					   : null;
		}

		private object MemberValue(object target, string label)
		{
			MemberInfo member = _mMembers[label];

			if (member.DeclaringType != null
				&& member.DeclaringType.IsGenericType
				&& member.DeclaringType.ContainsGenericParameters)
			{
				if (!_mOpenGenericReads.TryGetValue(label, out MemberInfo rebound))
				{
					rebound = BindingPass.FindInheritedMember(target.GetType(), member.Name);
					if (rebound == null)
					{
						SpaLogger.Error("open-generic member has no instance on "
										+ target.GetType().FullName
										+ ": "
										+ label);
						return null;
					}

					_mOpenGenericReads[label] = rebound;
				}

				member = rebound;
			}

			FieldInfo field = member as FieldInfo;
			if (field != null)
			{
				return field.GetValue(target);
			}

			PropertyInfo property = member as PropertyInfo;
			return property == null
					   ? null
					   : property.GetValue(target, null);
		}

		private int GetEmpireVisionBits(int empireIndex)
		{
			Array empires = Field(SandboxEmpires).GetValue(null) as Array;
			if (empires == null || empireIndex < 0 || empireIndex >= empires.Length)
			{
				return 0;
			}

			object empire = empires.GetValue(empireIndex);
			return empire == null
					   ? 0
					   : GetIntMember(empire, EmpireVisionBits);
		}

		private int GetIntMember(object target, string label)
		{
			object value = MemberValue(target, label);
			return value is int valueInt
					   ? valueInt
					   : 0;
		}
	}
}
