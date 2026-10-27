using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using SettlementPlacementAdvisor.gameapi;
using SettlementPlacementAdvisor.presentation;
using Xunit;

namespace SettlementPlacementAdvisor.Tests
{
	/// <summary>
	///     T1: the published view (single cross-thread channel) and the mode-policy table.
	/// </summary>
	public class ViewModeTests
	{
		[Fact]
		public void EqualPublishReusesThePublishedInstance()
		{
			Probe.Start();
			PresentationView.Publish(new[] { 102, 103 }, ArmyActionType.CreateCamp, ArmyActionFailureFlags.None, -1);
			PresentationView published = PresentationView.Current;

			PresentationView.Publish(new[] { 102, 103 }, ArmyActionType.CreateCamp, ArmyActionFailureFlags.None, -1);

			Assert.Same(published, PresentationView.Current);
		}

		[Fact]
		public void ChangedSetSwapsAndMembershipFollowsTheSet()
		{
			Probe.Start();
			PresentationView.Publish(new[] { 102, 103 }, ArmyActionType.CreateCamp, ArmyActionFailureFlags.None, -1);
			PresentationView first = PresentationView.Current;

			PresentationView.Publish(new[] { 102, 103, 203 }, ArmyActionType.CreateCamp, ArmyActionFailureFlags.None,
				-1);

			Assert.NotSame(first, PresentationView.Current);
			Assert.True(PresentationView.Current.Contains(203));
			Assert.True(PresentationView.Current.Contains(103));
			Assert.False(PresentationView.Current.Contains(999));
		}

		[Fact]
		public void InactiveViewCarriesNoMembership()
		{
			Probe.Start();
			PresentationView.Publish(new[] { 102, 103 }, ArmyActionType.CreateCamp, ArmyActionFailureFlags.None, -1);

			PresentationView.PublishInactive(ArmyActionType.CreateCamp);

			Assert.False(PresentationView.Current.IsActive);
			Assert.False(PresentationView.Current.Contains(102));
		}

		[Fact]
		public void CursorFactsRideTheSamePublish()
		{
			Probe.Start();
			ArmyActionFailureFlags cost = ArmyActionFailureFlags_Part1.NotEnoughMoney;
			PresentationView.Publish(new[] { 102 }, ArmyActionType.FoundCity, cost, 101);

			Assert.Equal(101, PresentationView.Current.CursorTile);
			Assert.Equal(cost, PresentationView.Current.SettleFailure);
			Assert.Equal(ArmyActionType.FoundCity, PresentationView.Current.Mode);
		}

		[Fact]
		public void ModeTableWiresExactlyTheThreePlacementModes()
		{
			Assert.True(ModePolicy.IsWired(ArmyActionType.CreateCamp));
			Assert.True(ModePolicy.IsWired(ArmyActionType.FoundCity));
			Assert.True(ModePolicy.IsWired(ArmyActionType.FoundCityWithSettler));
			Assert.False(ModePolicy.IsWired(ArmyActionType.Ransack));
			Assert.False(ModePolicy.IsWired(ArmyActionType.AttackArmy));
		}

		[Fact]
		public void TerritoryFilterPerMode()
		{
			TerritoryFacts clean = Facts(false, false, false, -1);
			Assert.True(ModePolicy.IsTerritoryClaimable(ArmyActionType.CreateCamp, clean, 0));
			Assert.True(ModePolicy.IsTerritoryClaimable(ArmyActionType.FoundCity, Facts(true, false, false, -1), 0));
			Assert.False(ModePolicy.IsTerritoryClaimable(ArmyActionType.CreateCamp, Facts(true, false, false, -1), 0));
			Assert.False(ModePolicy.IsTerritoryClaimable(ArmyActionType.FoundCity, Facts(false, true, false, 1), 0));

			// Settler: own camp settlement stays, own city settlement goes, foreign settlement goes.
			Assert.True(ModePolicy.IsTerritoryClaimable(ArmyActionType.FoundCityWithSettler,
				Facts(false, true, false, 0), 0));
			Assert.False(ModePolicy.IsTerritoryClaimable(ArmyActionType.FoundCityWithSettler,
				Facts(false, true, true, 0), 0));
			Assert.False(ModePolicy.IsTerritoryClaimable(ArmyActionType.FoundCityWithSettler,
				Facts(false, true, false, 1), 0));
		}

		[Fact]
		public void WorldEdgeIsDroppedByEveryMode()
		{
			TerritoryFacts edge = Facts(false, false, false, -1);
			edge.IsEdgeOfTheWorld = true;
			Assert.False(ModePolicy.IsTerritoryClaimable(ArmyActionType.CreateCamp, edge, 0));
			Assert.False(ModePolicy.IsTerritoryClaimable(ArmyActionType.FoundCity, edge, 0));
			Assert.False(ModePolicy.IsTerritoryClaimable(ArmyActionType.FoundCityWithSettler, edge, 0));
		}

		private static TerritoryFacts Facts(bool claimed, bool settled, bool ownedByCity, int owner)
		{
			return new TerritoryFacts
			{
				Claimed = claimed, HasSettlement = settled, IsOwnedByCity = ownedByCity, OwnerEmpireIndex = owner
			};
		}
	}
}
