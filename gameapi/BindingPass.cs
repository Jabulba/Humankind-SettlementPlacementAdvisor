using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Amplitude;
using Amplitude.Mercury;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;

namespace SettlementPlacementAdvisor.gameapi
{
	internal delegate void EvaluateOnePlacement(
		object evaluator,
		object army,
		ref DistrictPlacementEvaluation.ValidTile validTile,
		int tileIndex,
		DistrictDefinition districtDefinition,
		object startingPack);

#pragma warning disable 0649
	internal struct GateCosts
	{
		public FixedPoint Money;
		public FixedPoint Influence;
		public FixedPoint Unit;
	}
#pragma warning restore 0649

#pragma warning disable 0649
	internal struct PositionCarrier
	{
		public WorldPosition Value;
	}
#pragma warning restore 0649

	internal delegate ArmyActionFailureFlags CampTileGate(object army, int tileIndex, ref GateCosts costs);

	internal delegate ArmyActionFailureFlags PositionTileGate(
		object army,
		PositionCarrier carrier,
		ref ArmyActionFailureFlags failures);

	internal delegate ArmyActionFailureFlags TileFlagsTileGate(
		object army,
		int tileIndex,
		ref ArmyActionFailureFlags failures);

	internal delegate void TilePositionWriter(ref PositionCarrier carrier, int tileIndex);

	/// <summary>
	///     The argument form a binding row names: the shape the pass binds and, for hot members, compiles.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The mode-policy table's gate rows can only name a shape this vocabulary has, and the pass has a
	///         binder for every shape, so an unknown shape is a load failure, never a mid-game reflection error on
	///         the overload the metadata listed first. OverloadFree rows bind the method the owner type itself
	///         declares, which excludes inherited overloads.
	///     </para>
	/// </remarks>
	internal enum BindingShape
	{
		Field,

		Member,

		GenericArmy,

		OverloadFree,

		CampTile,
		CampArmy,
		ArmyFlags,
		PositionFlags,
		TileFlags,
		TerrainAndPoi,
		ExploredBits,
		TileToPosition,
		TailInitialize
	}

	/// <summary>
	///     One binding row: the label the surface calls it by, the owner type, the member name, and its shape.
	/// </summary>
	internal struct NameBinding
	{
		public string Label;
		public string TypeName;
		public string Member;
		public BindingShape Shape;
	}

	/// <summary>
	///     The startup resolution of every reflected game member, row by row. The pass stops at its first
	///     unresolved row and leaves through GameBuildDriftException naming it: one drift, one report, never a
	///     list.
	/// </summary>
	/// <remarks>
	///     <para>
	///         A pass that returns leaves Fields, Methods, Members and Compiled fully populated, so the
	///         surface built from it has every row bound and nothing re-checks a binding at runtime. A hot member
	///         that cannot compile aborts the pass the same way.
	///     </para>
	/// </remarks>
	internal sealed class BindingPass
	{
		private const string ArmyTypeName = "Amplitude.Mercury.Simulation.Army";

		private const BindingFlags MemberFlags = BindingFlags.Public
												 | BindingFlags.NonPublic
												 | BindingFlags.Instance
												 | BindingFlags.Static;

		private const BindingFlags InstanceMemberFlags = BindingFlags.Public
														 | BindingFlags.NonPublic
														 | BindingFlags.Instance;

		/// <summary>
		///     Every reflected member the mod reads, one row each. A new hot member needs a row here plus a
		///     BindingShape, or the scan pays reflection overhead the per-refresh bound forbids.
		/// </summary>
		private static readonly NameBinding[] Bindings =
		{
			new NameBinding
			{
				Label = GameApi.SandboxEvaluator,
				TypeName = "Amplitude.Mercury.Sandbox.Sandbox",
				Member = "SimulationEvaluator"
			},
			new NameBinding
			{
				Label = GameApi.SandboxRepository,
				TypeName = "Amplitude.Mercury.Sandbox.Sandbox",
				Member = "SimulationEntityRepository"
			},
			new NameBinding
			{
				Label = GameApi.SandboxWorld, TypeName = "Amplitude.Mercury.Sandbox.Sandbox", Member = "World"
			},
			new NameBinding
			{
				Label = GameApi.SandboxVisibility,
				TypeName = "Amplitude.Mercury.Sandbox.Sandbox",
				Member = "VisibilityController"
			},
			new NameBinding
			{
				Label = GameApi.SandboxEmpires,
				TypeName = "Amplitude.Mercury.Sandbox.Sandbox",
				Member = "Empires"
			},
			new NameBinding
			{
				Label = GameApi.RepositoryLookup,
				TypeName = "Amplitude.Mercury.Simulation.SimulationEntityRepository",
				Member = "TryGetSimulationEntity",
				Shape = BindingShape.GenericArmy
			},
			new NameBinding
			{
				Label = GameApi.EvaluateOne,
				TypeName = "Amplitude.Mercury.Simulation.SimulationEvaluator",
				Member = "EvaluateOneSettlementPlacement",
				Shape = BindingShape.OverloadFree
			},
			new NameBinding
			{
				Label = GameApi.Ponderation,
				TypeName = "Amplitude.Mercury.Simulation.SimulationEvaluator",
				Member = "settlementScorePonderation"
			},
			new NameBinding
			{
				Label = GameApi.PonderationInit,
				TypeName = "Amplitude.Mercury.Simulation.SimulationEvaluator",
				Member = "InitSettlementScorePonderation",
				Shape = BindingShape.OverloadFree
			},
			new NameBinding
			{
				Label = GameApi.TerrainAndPoi,
				TypeName = "Amplitude.Mercury.Simulation.ConstructibleHelper",
				Member = "CheckTerrainAndPOI",
				Shape = BindingShape.TerrainAndPoi
			},
			new NameBinding
			{
				Label = ModePolicy.CampDefinitionLabel,
				TypeName = "Amplitude.Mercury.Simulation.DepartmentOfTheInterior",
				Member = "CampDefinition"
			},
			new NameBinding
			{
				Label = ModePolicy.CityCenterDefinitionLabel,
				TypeName = "Amplitude.Mercury.Simulation.DepartmentOfTheInterior",
				Member = "CityCenterDefinition"
			},
			new NameBinding
			{
				Label = GameApi.Explored,
				TypeName = "Amplitude.Mercury.Simulation.VisibilityController",
				Member = "IsWorldPositionExplored",
				Shape = BindingShape.ExploredBits
			},
			new NameBinding
			{
				Label = GameApi.WorldTerritoryInfo,
				TypeName = "Amplitude.Mercury.Simulation.World",
				Member = "TerritoryInfo"
			},
			new NameBinding
			{
				Label = GameApi.TerritoryInfoEdge,
				TypeName = "Amplitude.Mercury.Interop.TerritoryInfo",
				Member = "IsEdgeOfTheWorld"
			},
			new NameBinding
			{
				Label = GameApi.TerritoryInfoClaimed,
				TypeName = "Amplitude.Mercury.Interop.TerritoryInfo",
				Member = "Claimed"
			},
			new NameBinding
			{
				Label = GameApi.TerritoryInfoSettlement,
				TypeName = "Amplitude.Mercury.Interop.TerritoryInfo",
				Member = "SettlementGUID"
			},
			new NameBinding
			{
				Label = GameApi.TerritoryInfoEmpire,
				TypeName = "Amplitude.Mercury.Interop.TerritoryInfo",
				Member = "EmpireIndex"
			},
			new NameBinding
			{
				Label = GameApi.SnapshotTerritoryInfo,
				TypeName = "Amplitude.Mercury.Interop.AI.WorldSnapshot",
				Member = "TerritoryInfo"
			},
			new NameBinding
			{
				Label = GameApi.SnapshotAdjacencies,
				TypeName = "Amplitude.Mercury.Interop.AI.Entities.Territory",
				Member = "Adjacencies"
			},
			new NameBinding
			{
				Label = GameApi.SnapshotTileIndexes,
				TypeName = "Amplitude.Mercury.Interop.AI.Entities.Territory",
				Member = "TileIndexes"
			},
			new NameBinding
			{
				Label = ModePolicy.CampTileGateLabel,
				TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper",
				Member = "FillCreateCampFailures",
				Shape = BindingShape.CampTile
			},
			new NameBinding
			{
				Label = ModePolicy.CampArmyGateLabel,
				TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper",
				Member = "FillCreateCampFailures",
				Shape = BindingShape.CampArmy
			},
			new NameBinding
			{
				Label = ModePolicy.FoundCityArmyGateLabel,
				TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper",
				Member = "FillFoundCityFailures",
				Shape = BindingShape.ArmyFlags
			},
			new NameBinding
			{
				Label = ModePolicy.FoundCityTileGateLabel,
				TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper",
				Member = "FillFoundCityFailures",
				Shape = BindingShape.PositionFlags
			},
			new NameBinding
			{
				Label = ModePolicy.SettlerArmyGateLabel,
				TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper",
				Member = "FillSettlerAbilityFailures",
				Shape = BindingShape.ArmyFlags
			},
			new NameBinding
			{
				Label = ModePolicy.SettlerTileGateLabel,
				TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper",
				Member = "FillSettlerAbilityFailures",
				Shape = BindingShape.TileFlags
			},
			new NameBinding
			{
				Label = GameApi.TileToPosition,
				TypeName = "Amplitude.Mercury.WorldPosition",
				Member = "GetHexagonOffsetFromTileIndex",
				Shape = BindingShape.TileToPosition
			},
			new NameBinding
			{
				Label = ModePolicy.CommonArmyGateLabel,
				TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper",
				Member = "FillCommonArmyFailures",
				Shape = BindingShape.ArmyFlags
			},
			new NameBinding
			{
				Label = GameApi.EvaluationInitialize,
				TypeName = "Amplitude.Mercury.Interop.CampPlacementEvaluation",
				Member = "Initialize",
				Shape = BindingShape.TailInitialize
			},
			new NameBinding
			{
				Label = GameApi.PatchEvaluatePlacement,
				TypeName = "Amplitude.Mercury.Interop.SettlementPlacementCursorSnapshot",
				Member = "EvaluatePlacement",
				Shape = BindingShape.OverloadFree
			},
			new NameBinding
			{
				Label = GameApi.PatchUpdateCurrentPosition,
				TypeName = "Amplitude.Mercury.Interop.CampPlacementEvaluation",
				Member = "UpdateCurrentPosition",
				Shape = BindingShape.OverloadFree
			},
			new NameBinding
			{
				Label = GameApi.PatchDeactivateCursor,
				TypeName = "Amplitude.Mercury.Presentation.SettlementPlacementCursor",
				Member = "DeactivateCursor",
				Shape = BindingShape.OverloadFree
			},
			new NameBinding
			{
				Label = GameApi.PatchPinMustMeShown,
				TypeName = "Amplitude.Mercury.UI.SettlementPlacementPinsSubset",
				Member = "PinMustMeShown",
				Shape = BindingShape.OverloadFree
			},
			new NameBinding
			{
				Label = GameApi.PatchSandboxStart,
				TypeName = "Amplitude.Mercury.Sandbox.SandboxManager",
				Member = "Start",
				Shape = BindingShape.OverloadFree
			},
			new NameBinding
			{
				Label = GameApi.WorldDistrictInfo,
				TypeName = "Amplitude.Mercury.Simulation.World",
				Member = "DistrictInfo",
				Shape = BindingShape.Member
			},
			new NameBinding
			{
				Label = GameApi.WorldConstructionInfo,
				TypeName = "Amplitude.Mercury.Simulation.World",
				Member = "ConstructionInfo",
				Shape = BindingShape.Member
			},
			new NameBinding
			{
				Label = GameApi.PoolAllocatorFrame,
				TypeName = "Amplitude.Mercury.PoolAllocator",
				Member = "Frame",
				Shape = BindingShape.Member
			},
			new NameBinding
			{
				Label = GameApi.ArrayWithFrameCounter,
				TypeName = "Amplitude.Mercury.Interop.ArrayWithFrame`1",
				Member = "Frame",
				Shape = BindingShape.Member
			},
			new NameBinding
			{
				Label = GameApi.VisibilityFrame,
				TypeName = "Amplitude.Mercury.Simulation.VisibilityController",
				Member = "Frame",
				Shape = BindingShape.Member
			},
			new NameBinding
			{
				Label = GameApi.EmpireVisionBits,
				TypeName = "Amplitude.Mercury.Simulation.MajorEmpire",
				Member = "Bits",
				Shape = BindingShape.Member
			},
			new NameBinding
			{
				Label = GameApi.ArmyEmpireIndex,
				TypeName = ArmyTypeName,
				Member = "EmpireIndex",
				Shape = BindingShape.Member
			},
			new NameBinding
			{
				Label = GameApi.SnapshotActionType,
				TypeName = GameApi.SnapshotType,
				Member = "actionType",
				Shape = BindingShape.Member
			},
			new NameBinding
			{
				Label = GameApi.SnapshotCursorPosition,
				TypeName = GameApi.SnapshotType,
				Member = "nextConstructiblePosition",
				Shape = BindingShape.Member
			}
		};

		public readonly Dictionary<string, Delegate> Compiled = new Dictionary<string, Delegate>();

		public readonly Dictionary<string, FieldInfo> Fields = new Dictionary<string, FieldInfo>();
		public readonly Dictionary<string, MemberInfo> Members = new Dictionary<string, MemberInfo>();
		public readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>();

		private readonly Type _armyType;

		public BindingPass()
		{
			_armyType = FindType(ArmyTypeName);
			if (_armyType == null)
			{
				throw new GameBuildDriftException(ArmyTypeName);
			}

			foreach (NameBinding binding in Bindings)
			{
				Bind(binding);
			}
		}

		private static bool HasGateBinder(BindingShape shape)
		{
			switch (shape)
			{
				case BindingShape.OverloadFree:
				case BindingShape.CampTile:
				case BindingShape.CampArmy:
				case BindingShape.ArmyFlags:
				case BindingShape.PositionFlags:
				case BindingShape.TileFlags:
				case BindingShape.TerrainAndPoi:
				case BindingShape.ExploredBits:
				case BindingShape.TileToPosition:
				case BindingShape.TailInitialize:
					return true;
				default:
					return false;
			}
		}

		private static Type FindType(string fullName)
		{
			return AppDomain.CurrentDomain.GetAssemblies()
				.Select(assembly => assembly.GetType(fullName, false))
				.FirstOrDefault(type => type != null);
		}

		private void Bind(NameBinding binding)
		{
			Type resolvedType = FindType(binding.TypeName);
			if (resolvedType == null)
			{
				throw Fail(binding);
			}

			switch (binding.Shape)
			{
				case BindingShape.Field:
					BindField(binding, resolvedType);
					return;
				case BindingShape.Member:
					BindMember(binding, resolvedType);
					return;
				case BindingShape.GenericArmy:
					BindArmyLookup(binding, resolvedType);
					return;
			}

			BindGateMethod(binding, resolvedType);
		}

		private void BindField(NameBinding binding, Type owner)
		{
			FieldInfo field = owner.GetField(binding.Member, MemberFlags);
			if (field == null)
			{
				throw Fail(binding);
			}

			Fields[binding.Label] = field;
		}

		private void BindMember(NameBinding binding, Type startType)
		{
			MemberInfo member = FindInheritedMember(startType, binding.Member);
			if (member == null)
			{
				throw Fail(binding);
			}

			Members[binding.Label] = member;
		}

		private void BindArmyLookup(NameBinding binding, Type owner)
		{
			MethodInfo lookupDefinition = owner.GetMethods(InstanceMemberFlags)
				.FirstOrDefault(method => IsArmyLookupCandidate(method, binding.Member));

			if (lookupDefinition == null)
			{
				throw Fail(binding);
			}

			Methods[binding.Label] = lookupDefinition.MakeGenericMethod(_armyType);
		}

		private static bool IsArmyLookupCandidate(MethodInfo method, string member)
		{
			if (method.Name != member || !method.IsGenericMethodDefinition)
			{
				return false;
			}

			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length == 2
				   && parameters[0].ParameterType == typeof(SimulationEntityGUID)
				   && parameters[1].ParameterType.IsByRef;
		}

		private void BindGateMethod(NameBinding binding, Type owner)
		{
			if (!HasGateBinder(binding.Shape))
			{
				throw Fail(binding);
			}

			MethodInfo method = FindMethod(owner, binding.Member, binding.Shape, _armyType);
			if (method == null)
			{
				throw Fail(binding);
			}

			Methods[binding.Label] = method;
			CompileHot(binding.Label, method);
		}

		private void CompileHot(string label, MethodInfo method)
		{
			if (!IsHotLabel(label))
			{
				return;
			}

			try
			{
				Compiled[label] = CreateCompiled(label, method);
			}
			catch (Exception e)
			{
				throw new GameBuildDriftException(label + " (compiled delegate: " + e.Message + ")");
			}
		}

		private static bool IsHotLabel(string label)
		{
			return label == ModePolicy.CampTileGateLabel
				   || label == ModePolicy.FoundCityTileGateLabel
				   || label == ModePolicy.SettlerTileGateLabel
				   || label == GameApi.TerrainAndPoi
				   || label == GameApi.Explored
				   || label == GameApi.EvaluateOne
				   || label == GameApi.PonderationInit
				   || label == GameApi.TileToPosition;
		}

		private static Delegate CreateCompiled(string label, MethodInfo method)
		{
			switch (label)
			{
				case ModePolicy.CampTileGateLabel:
				{
					ParameterExpression tileIndex = Expression.Parameter(typeof(int), "tileIndex");
					ParameterExpression costs = Expression.Parameter(typeof(GateCosts).MakeByRefType(), "costs");
					return GateFlags(typeof(CampTileGate),
						method,
						tileIndex,
						tileIndex,
						costs,
						Expression.Field(costs, "Money"),
						Expression.Field(costs, "Influence"),
						Expression.Field(costs, "Unit"));
				}

				case ModePolicy.FoundCityTileGateLabel:
				{
					ParameterExpression position = Expression.Parameter(typeof(PositionCarrier), "carrier");
					return GateFlags(typeof(PositionTileGate),
						method,
						position,
						Expression.Field(position, "Value"),
						Expression.Parameter(method.GetParameters()[2].ParameterType, "failures"));
				}

				case ModePolicy.SettlerTileGateLabel:
				{
					ParameterExpression tileIndex = Expression.Parameter(typeof(int), "tileIndex");
					return GateFlags(typeof(TileFlagsTileGate),
						method,
						tileIndex,
						tileIndex,
						Expression.Parameter(method.GetParameters()[2].ParameterType, "failures"));
				}

				case GameApi.EvaluateOne:
					return GateForward(typeof(EvaluateOnePlacement),
						method,
						("evaluator", typeof(object)),
						("army", typeof(object)),
						("validTile", typeof(DistrictPlacementEvaluation.ValidTile).MakeByRefType()),
						("tileIndex", typeof(int)),
						("districtDefinition", typeof(DistrictDefinition)),
						("startingPack", typeof(object)));

				case GameApi.TerrainAndPoi:
					return GateForward(typeof(Func<DistrictDefinition, int, bool>),
						method,
						("districtDefinition", typeof(DistrictDefinition)),
						("tileIndex", typeof(int)));

				case GameApi.Explored:
					return GateForward(typeof(Func<object, int, int, bool>),
						method,
						("visibilityController", typeof(object)),
						("tileIndex", typeof(int)),
						("bits", typeof(int)));

				case GameApi.PonderationInit:
					return GateForward(typeof(Action<object>),
						method,
						("evaluator", typeof(object)));
			}

			ParameterExpression carrier = Expression.Parameter(typeof(PositionCarrier).MakeByRefType(), "carrier");
			ParameterExpression toTile = Expression.Parameter(typeof(int), "tileIndex");
			ParameterExpression offset = Expression.Variable(method.ReturnType, "offset");
			ConstructorInfo ctor = method.DeclaringType.GetConstructor(new[] { method.ReturnType });
			BlockExpression body = Expression.Block(new[] { offset },
				Expression.Assign(offset, Expression.Call(method, toTile)),
				Expression.Assign(Expression.Field(carrier, "Value"), Expression.New(ctor, offset)));

			return Expression.Lambda<TilePositionWriter>(body, carrier, toTile).Compile();
		}

		private static Delegate GateFlags(
			Type gateType,
			MethodInfo method,
			ParameterExpression position,
			Expression positionArgument,
			ParameterExpression last,
			params Expression[] extraArguments)
		{
			ParameterInfo[] parameters = method.GetParameters();
			ParameterExpression army = Expression.Parameter(typeof(object), "army");
			ParameterExpression flags = Expression.Parameter(parameters[2].ParameterType.GetElementType(), "failures");
			List<Expression> arguments = new List<Expression>
			{
				Expression.Convert(army, parameters[0].ParameterType), positionArgument, flags
			};
			arguments.AddRange(extraArguments);
			return Expression.Lambda(gateType,
					Expression.Block(new[] { flags }, Expression.Call(method, arguments), flags),
					army,
					position,
					last)
				.Compile();
		}

		private static Delegate GateForward(
			Type gateType,
			MethodInfo method,
			params (string Name, Type Type)[] lambdaTypes)
		{
			ParameterExpression[] parameters = lambdaTypes
				.Select(entry => Expression.Parameter(entry.Type, entry.Name))
				.ToArray();

			ParameterInfo[] target = method.GetParameters();
			int offset = method.IsStatic
							 ? 0
							 : 1;
			Expression[] arguments = new Expression[target.Length];
			for (int i = 0; i < target.Length; i++)
			{
				ParameterExpression parameter = parameters[i + offset];
				Type slot = parameter.Type;
				Type parameterType = target[i].ParameterType;
				bool same = parameterType.IsByRef
								? slot == parameterType.GetElementType()
								: slot == parameterType;

				arguments[i] = same
								   ? parameter
								   : (Expression)Expression.Convert(parameter, parameterType);
			}

			Expression body = method.IsStatic
								  ? Expression.Call(method, arguments)
								  : Expression.Call(Expression.Convert(parameters[0], method.DeclaringType),
									  method,
									  arguments);

			return Expression.Lambda(gateType, body, parameters).Compile();
		}

		private static Type[] GateParameterTypes(BindingShape shape, Type armyType)
		{
			Type flagsRef = typeof(ArmyActionFailureFlags).MakeByRefType();
			Type costRef = typeof(FixedPoint).MakeByRefType();
			switch (shape)
			{
				case BindingShape.OverloadFree:
					return null;
				case BindingShape.CampTile:
					return new[] { armyType, typeof(int), flagsRef, costRef, costRef, costRef };
				case BindingShape.CampArmy:
					return new[] { armyType, flagsRef, costRef, costRef, costRef, typeof(bool) };
				case BindingShape.ArmyFlags:
					return new[] { armyType, flagsRef };
				case BindingShape.PositionFlags:
					return new[] { armyType, typeof(WorldPosition), flagsRef };
				case BindingShape.TileFlags:
					return new[] { armyType, typeof(int), flagsRef };
				case BindingShape.TerrainAndPoi:
					return new[] { typeof(DistrictDefinition), typeof(int) };
				case BindingShape.ExploredBits:
					return new[] { typeof(int), typeof(int) };
				case BindingShape.TileToPosition:
					return new[] { typeof(int) };
				case BindingShape.TailInitialize:
					return new[] { typeof(CustomTileIndexArea) };
				default:
					return null;
			}
		}

		private static MethodInfo FindMethod(Type owner, string member, BindingShape shape, Type armyType)
		{
			bool overloadFree = shape == BindingShape.OverloadFree;
			Type[] parameterTypes = GateParameterTypes(shape, armyType);
			MethodInfo methodInfo = null;
			bool found = false;
			foreach (MethodInfo method in owner.GetMethods(MemberFlags))
			{
				if (method.Name != member)
				{
					continue;
				}

				if (overloadFree && method.DeclaringType != owner)
				{
					continue;
				}

				if (parameterTypes != null && !ParametersMatch(method, parameterTypes))
				{
					continue;
				}

				found = true;
				methodInfo = method;
				if (!overloadFree)
				{
					break;
				}
			}

			return found
					   ? methodInfo
					   : null;
		}

		private static bool ParametersMatch(MethodInfo method, Type[] parameterTypes)
		{
			ParameterInfo[] parameters = method.GetParameters();
			if (parameters.Length != parameterTypes.Length)
			{
				return false;
			}

			for (int i = 0; i < parameterTypes.Length; i++)
			{
				if (parameters[i].ParameterType != parameterTypes[i])
				{
					return false;
				}
			}

			return true;
		}

		internal static MemberInfo FindInheritedMember(Type startType, string name)
		{
			Type candidate = startType;
			while (candidate != null)
			{
				FieldInfo field = candidate.GetField(name, MemberFlags);
				if (field != null)
				{
					return field;
				}

				PropertyInfo property = candidate.GetProperty(name, MemberFlags);
				if (property != null)
				{
					return property;
				}

				candidate = candidate.BaseType;
			}

			return null;
		}

		private static GameBuildDriftException Fail(NameBinding binding)
		{
			return new GameBuildDriftException(binding.Label);
		}
	}
}
