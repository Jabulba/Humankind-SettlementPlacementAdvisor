using System;
using Amplitude.Mercury.Data.Simulation;

namespace SettlementPlacementAdvisor.gameapi
{
	/// <summary>
	///     One mode: its district definition label, its territory filter, and the labels and binding shapes of
	///     its tile gate and army-level gates.
	/// </summary>
	internal struct ModeRow
	{
		public ArmyActionType Mode;
		public string DefinitionLabel;

		public Func<TerritoryFacts, int, bool> TerritoryClaimable;

		public string TileGateLabel;
		public BindingShape TileGateShape;
		public string[] ArmyGateLabels;
		public BindingShape[] ArmyGateShapes;
	}

	/// <summary>
	///     The mode-policy table is the single source of mode dispatch: a mode's row is the one place its
	///     territory filter, district definition and gates live, a mode is wired when its row exists, and an
	///     unmapped mode fails closed.
	/// </summary>
	/// <remarks>
	///     <list type="table">
	///         <listheader>
	///             <term>Mode</term>
	///             <description>
	///                 Territory filter (drops), tile gate, army-level gate,
	///                 district definition
	///             </description>
	///         </listheader>
	///         <item>
	///             <term>CreateCamp</term>
	///             <description>
	///                 world edge, claimed, settled; FillCreateCampFailures
	///                 (tile); shared rows plus the camp army overload; CampDefinition
	///             </description>
	///         </item>
	///         <item>
	///             <term>FoundCity</term>
	///             <description>
	///                 world edge, settled; FillFoundCityFailures; shared rows plus
	///                 FillFoundCityFailures; CityCenterDefinition
	///             </description>
	///         </item>
	///         <item>
	///             <term>FoundCityWithSettler</term>
	///             <description>
	///                 world edge, settled, keeping your own camp or
	///                 pre-camp settlement; FillSettlerAbilityFailures (tile); shared rows plus
	///                 FillSettlerAbilityFailures; CityCenterDefinition
	///             </description>
	///         </item>
	///     </list>
	///     <para>
	///         Every row uses the pipeline's membership mask, applied by the pick computation, never here.
	///         Territory facts fail closed: an unknown territory never advises, and a row cannot name a member the
	///         startup probe did not check. The army-level gate list names vanilla's shared army-state row first,
	///         because the camp and settler army overloads never run it. Never filter on IsOcean or
	///         AnyClaimableTile.
	///     </para>
	/// </remarks>
	internal static class ModePolicy
	{
		public const string CampDefinitionLabel = "DepartmentOfTheInterior.CampDefinition";

		public const string CityCenterDefinitionLabel = "DepartmentOfTheInterior.CityCenterDefinition";
		public const string CommonArmyGateLabel = "ArmyActionHelper.FillCommonArmyFailures(army)";
		public const string CampTileGateLabel = "ArmyActionHelper.FillCreateCampFailures(tile)";
		public const string CampArmyGateLabel = "ArmyActionHelper.FillCreateCampFailures(army)";
		public const string FoundCityTileGateLabel = "ArmyActionHelper.FillFoundCityFailures(position)";
		public const string FoundCityArmyGateLabel = "ArmyActionHelper.FillFoundCityFailures(army)";
		public const string SettlerTileGateLabel = "ArmyActionHelper.FillSettlerAbilityFailures(tile)";
		public const string SettlerArmyGateLabel = "ArmyActionHelper.FillSettlerAbilityFailures(army)";

		private static readonly ModeRow[] SRows =
		{
			new ModeRow
			{
				Mode = ArmyActionType.CreateCamp,
				DefinitionLabel = CampDefinitionLabel,
				TerritoryClaimable = (facts, empire) => !facts.Claimed && !facts.HasSettlement,
				TileGateLabel = CampTileGateLabel,
				TileGateShape = BindingShape.CampTile,
				ArmyGateLabels = new[] { CommonArmyGateLabel, CampArmyGateLabel },
				ArmyGateShapes = new[] { BindingShape.ArmyFlags, BindingShape.CampArmy }
			},
			new ModeRow
			{
				Mode = ArmyActionType.FoundCity,
				DefinitionLabel = CityCenterDefinitionLabel,
				TerritoryClaimable = (facts, empire) => !facts.HasSettlement,
				TileGateLabel = FoundCityTileGateLabel,
				TileGateShape = BindingShape.PositionFlags,
				ArmyGateLabels = new[] { CommonArmyGateLabel, FoundCityArmyGateLabel },
				ArmyGateShapes = new[] { BindingShape.ArmyFlags, BindingShape.ArmyFlags }
			},
			new ModeRow
			{
				Mode = ArmyActionType.FoundCityWithSettler,
				DefinitionLabel = CityCenterDefinitionLabel,
				TerritoryClaimable = (facts, empire) =>
					!facts.HasSettlement || (facts.OwnerEmpireIndex == empire && !facts.IsOwnedByCity),
				TileGateLabel = SettlerTileGateLabel,
				TileGateShape = BindingShape.TileFlags,
				ArmyGateLabels = new[] { CommonArmyGateLabel, SettlerArmyGateLabel },
				ArmyGateShapes = new[] { BindingShape.ArmyFlags, BindingShape.ArmyFlags }
			}
		};

		/// <summary>
		///     A mode is wired when the table has its row. The patch prefixes, the writer's guards and the scan
		///     dispatch all read this, and an unwired mode writes nothing.
		/// </summary>
		public static bool IsWired(ArmyActionType mode)
		{
			return TryGetRow(mode, out ModeRow _);
		}

		public static bool TryGetRow(ArmyActionType mode, out ModeRow row)
		{
			for (int i = 0; i < SRows.Length; i++)
			{
				if (SRows[i].Mode != mode)
				{
					continue;
				}

				row = SRows[i];
				return true;
			}

			row = default;
			return false;
		}

		/// <summary>
		///     IsEdgeOfTheWorld always drops; the row's own filter follows.
		/// </summary>
		/// <returns>
		///     true when the mode can claim the territory. An unwired mode, or a row with no filter,
		///     closes the territory.
		/// </returns>
		public static bool IsTerritoryClaimable(ArmyActionType mode, TerritoryFacts facts, int empireIndex)
		{
			if (facts.IsEdgeOfTheWorld)
			{
				return false;
			}

			if (!TryGetRow(mode, out ModeRow row) || row.TerritoryClaimable == null)
			{
				return false;
			}

			return row.TerritoryClaimable(facts, empireIndex);
		}
	}
}
