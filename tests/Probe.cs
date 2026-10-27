using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using BepInEx.Logging;
using HarmonyLib;
using SettlementPlacementAdvisor.composition;
using SettlementPlacementAdvisor.gameapi;
using SettlementPlacementAdvisor.logger;
using SettlementPlacementAdvisor.presentation;
using Xunit;


// All tests share process-global statics (PresentationView.Current, SpaLogger, Harmony
// patches), so the assembly runs serial.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SettlementPlacementAdvisor.Tests
{
	internal static class Probe
	{
		public static readonly List<string> LogLines = new List<string>();

		private static bool s_Preloaded;
		private static ManualLogSource s_Log;

		public static Version HarmonyVersion => typeof(Harmony).Assembly.GetName().Version;

		// Preloads every assembly the test host shipped: BindingPass resolves member names through
		// AppDomain.GetAssemblies(), and an assembly the pass has not loaded is a name it cannot
		// resolve (a LoadFrom-context-only preload produced a false GameBuildDriftException on
		// Amplitude.Mercury.Simulation.Army in the probes).
		public static void Start()
		{
			Preload();
			if (s_Log == null)
			{
				s_Log = new ManualLogSource("tests");
				s_Log.LogEvent += (source, e) => LogLines.Add(Convert.ToString(e.Data));
				SpaLogger.Init(s_Log);
			}

			LogLines.Clear();
			ResetView();
			ResetConfig();
		}

		private static void Preload()
		{
			if (s_Preloaded)
			{
				return;
			}

			foreach (string file in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
			{
				try
				{
					Assembly.LoadFrom(file);
				}
				catch (Exception)
				{
					// Native-dependent modules (UnityEngine) are unloadable headless; the suite
					// never touches them.
				}
			}

			s_Preloaded = true;
		}

		public static void ResetView()
		{
			PresentationView.PublishInactive(ArmyActionType.AttackArmy);
		}

		// The config snapshot the scan and the writer's reserve read: the AdvisorConfig bind
		// defaults, the rows every expected pick set and tail pin is derived from.
		public static void ResetConfig()
		{
			SpaRuntime.TopHovered = AdvisorConfig.TopHovered;
			SpaRuntime.TopNeighbour = AdvisorConfig.TopNeighbour;
			SpaRuntime.RangeLevels = AdvisorConfig.RangeLevels;
			SpaRuntime.TailCapacity = AdvisorConfig.TailCapacityFor(
				AdvisorConfig.TopHovered, AdvisorConfig.TopNeighbour, AdvisorConfig.RangeLevels);
		}

		public static void ResetRuntime()
		{
			SpaRuntime.GameApi = null;
			SpaRuntime.Advisor = null;
			SpaRuntime.Writer = null;
			ResetConfig();
		}

		public static GameApi NewGameApi()
		{
			Preload();
			return new GameApi();
		}

		// Real CampPlacementEvaluation, populated headless: vanilla baseline rows carry recognizable
		// TileIndex values (5000+), so a test can tell advisor writes from vanilla entries. Built
		// through vanilla's own Initialize, the way a real activation runs it, so the arrays vanilla
		// allocates at activation exist - a test can assert the advisor leaves them stumped.
		public static CampPlacementEvaluation NewEvaluation(int baselineCount, ArmyActionType settleType)
		{
			CampPlacementEvaluation evaluation
				= (CampPlacementEvaluation)Activator.CreateInstance(typeof(CampPlacementEvaluation));
			typeof(CampPlacementEvaluation)
				.GetMethod("Initialize", BindingFlags.NonPublic | BindingFlags.Instance)
				.Invoke(evaluation, new object[] { null });
			evaluation.SettleType = settleType;

			DistrictPlacementEvaluation.ValidTile[] tiles = new DistrictPlacementEvaluation.ValidTile[baselineCount];
			for (int i = 0; i < baselineCount; i++)
			{
				DistrictPlacementEvaluation.ValidTile entry = default;
				entry.Initialize(5000 + i);
				entry.Showable = false;
				tiles[i] = entry;
			}

			evaluation.ValidTiles = tiles;
			evaluation.ValidTileCount = baselineCount;
			evaluation.ConditionalFlagsTileIndexes = new DistrictPlacementConditionFlags[baselineCount];
			evaluation.CurrentPositionIndex = -1;
			evaluation.SettleType = settleType;
			return evaluation;
		}

		public static SettlementPlacementCursorSnapshot NewSnapshot()
		{
			SettlementPlacementCursorSnapshot snapshot = (SettlementPlacementCursorSnapshot)
				Activator.CreateInstance(typeof(SettlementPlacementCursorSnapshot));
			SetFlag(snapshot, "enabled", true);
			SetFlag(snapshot, "syncAskedByPresentation", true);
			SetFlag(snapshot, "forcePresentationChanged", true);
			return snapshot;
		}

		private static void SetFlag(object target, string name, bool value)
		{
			for (Type type = target.GetType(); type != null; type = type.BaseType)
			{
				FieldInfo field = type.GetField(name,
					BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
				if (field != null)
				{
					field.SetValue(target, value);
					return;
				}
			}

			throw new MissingFieldException(target.GetType().FullName, name);
		}

		// Invokes a Harmony patch method (private static prefix/postfix) directly: the proven
		// pattern for patch logic the real game method cannot drive headless (Unity-native bodies).
		public static object InvokePatch(Type patchClass, string method, params object[] args)
		{
			MethodInfo m = patchClass.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
			try
			{
				return m.Invoke(null, args);
			}
			catch (TargetInvocationException e)
			{
				throw e.InnerException;
			}
		}

		public static object InvokePatched(GameApi api, PatchPoint point, object instance, params object[] args)
		{
			MethodBase target = api.PatchTarget(point);
			try
			{
				return target.Invoke(instance, args);
			}
			catch (TargetInvocationException e)
			{
				throw e.InnerException;
			}
		}

		// 2.4.2 (compiled reference): instance UnpatchAll(id). The shipped 2.9 fork moved this to
		// UnpatchSelf; dispatch by reflection so either assembly that loads tears down cleanly.
		public static void Unpatch(Harmony harmony, string id)
		{
			MethodInfo self = typeof(Harmony).GetMethod("UnpatchSelf", Type.EmptyTypes);
			if (self != null)
			{
				self.Invoke(harmony, null);
				return;
			}

			harmony.UnpatchAll(id);
		}

		// Injects an uninitialized World carrying a stamped TerritoryInfo frame into the static
		// Sandbox.World the compiled frame delegate reads: World's constructor needs Unity assets,
		// the frame read only touches fields.
		public static void InjectWorldTerritoryFrame(int frame)
		{
			Type worldType = FindType("Amplitude.Mercury.Simulation.World");
			Type sandboxType = FindType("Amplitude.Mercury.Sandbox.Sandbox");

			object world = FormatterServices.GetUninitializedObject(worldType);
			FieldInfo worldTerritoryInfo
				= worldType.GetField("TerritoryInfo", BindingFlags.NonPublic | BindingFlags.Instance);
			Assume(worldTerritoryInfo != null, "World.TerritoryInfo field");

			object territoryInfo = Activator.CreateInstance(
				worldTerritoryInfo.FieldType.GetGenericTypeDefinition().MakeGenericType(typeof(TerritoryInfo)));
			SetFrame(territoryInfo, frame);

			worldTerritoryInfo.SetValue(world, territoryInfo);

			FieldInfo sandboxWorld = sandboxType.GetField("World", BindingFlags.Public | BindingFlags.Static);
			Assume(sandboxWorld != null, "Sandbox.World field");
			sandboxWorld.SetValue(null, world);
		}

		public static void ClearWorldInjection()
		{
			FindType("Amplitude.Mercury.Sandbox.Sandbox")
				.GetField("World", BindingFlags.Public | BindingFlags.Static)
				.SetValue(null, null);
		}

		private static void SetFrame(object territoryInfo, int frame)
		{
			for (Type type = territoryInfo.GetType(); type != null; type = type.BaseType)
			{
				FieldInfo field = type.GetField("Frame",
					BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
				if (field != null)
				{
					field.SetValue(territoryInfo, frame);
					return;
				}

				PropertyInfo property = type.GetProperty("Frame",
					BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
				if (property != null && property.GetSetMethod(true) != null)
				{
					property.SetValue(territoryInfo, frame);
					return;
				}
			}

			throw new MissingMemberException(territoryInfo.GetType().FullName, "Frame");
		}

		public static Type FindType(string fullName)
		{
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type type = assembly.GetType(fullName);
				if (type != null)
				{
					return type;
				}
			}

			throw new TypeLoadException(fullName);
		}

		private static void Assume(bool condition, string what)
		{
			if (!condition)
			{
				throw new MissingMemberException("headless fixture: " + what);
			}
		}
	}
}
