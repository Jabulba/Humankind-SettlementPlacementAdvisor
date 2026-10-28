using System;
using System.Collections.Generic;
using Amplitude;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using SettlementPlacementAdvisor.gameapi;
using SettlementPlacementAdvisor.logger;
using SnapshotTerritory = Amplitude.Mercury.Interop.AI.Entities.Territory;

namespace SettlementPlacementAdvisor.pipeline
{
	/// <summary>
	///     One recommended tile: tile index, weighted FIMS score, and the evaluated placement data the pins read.
	/// </summary>
	internal struct Pick
	{
		public int Tile;
		public FixedPoint Score;
		public DistrictPlacementEvaluation.ValidTile Data;
	}

	/// <summary>
	///     The pick computation's whole input for one refresh: hovered territory, placing army and empire, the
	///     advised counts, the neighborhood range, and the mode.
	/// </summary>
	internal struct CursorContext
	{
		public int HoveredTerritory;
		public object Army;
		public int EmpireIndex;
		public int TopHovered;
		public int TopNeighbour;
		public int RangeLevels;
		public ArmyActionType Mode;

		public static CursorContext For(
			int hoveredTerritory,
			object army,
			int empireIndex,
			int topHovered,
			int topNeighbour,
			int rangeLevels,
			ArmyActionType mode)
		{
			return new CursorContext
			{
				HoveredTerritory = hoveredTerritory,
				Army = army,
				EmpireIndex = empireIndex,
				TopHovered = Math.Max(1, topHovered),
				TopNeighbour = Math.Max(1, topNeighbour),
				RangeLevels = Math.Max(1, rangeLevels),
				Mode = mode
			};
		}
	}

	/// <summary>
	///     The rules in pipeline order, from the army-level gate to admission, with the per-territory cache. The
	///     pick computation runs only on refresh.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Cache: one entry per territory index, holding that territory's ranked survivors with the stamps
	///         that produced them. An entry serves a pass when every stamp matches, and entries are never evicted, so
	///         a cursor move rescans only the territories entering the neighborhood for the first time. Moving within
	///         a territory, or back to one the cursor already scanned, costs no scan while its stamps hold, and one
	///         slot per territory means a mode switch re-scans a territory the next time the cursor reaches it.
	///     </para>
	///     <para>
	///         The cache belongs to the running game, and one game runs at a time: starting a game, loading a
	///         game, and quitting to the menu then starting again all go through the game's sandbox start, and
	///         that patch empties the cache before the new game's first pass. A new game is therefore never
	///         served a stored entry, and an entry carries no game identity of its own.
	///     </para>
	///     <para>
	///         The quota is applied by admission, so it never keys an entry: a territory whose role flips
	///         between hovered and neighbour is served as stored, and admission slices its survivors for the new
	///         role.
	///     </para>
	///     <para>
	///         Revalidation re-reads facts, it does not scan: on a territory-frame bump every neighborhood
	///         territory is read once, a matching stamp reuses the entry without evaluating any tile, and a mismatch
	///         re-reads the facts during recomputation and re-evaluates the territory's tiles when it is claimable.
	///         The check compares every stamped fact, not only the claim.
	///     </para>
	///     <para>
	///         The counters the T1 tests read bound the scan's reach (adjacency, gate, scoring and fact-read
	///         calls), not its time; the 5 ms per refresh bound is stated on Advisor.
	///     </para>
	/// </remarks>
	internal sealed class PickComputation
	{
		/// <summary>
		///     One entry per territory index.
		/// </summary>
		private readonly Dictionary<int, CacheEntry> _mCache = new Dictionary<int, CacheEntry>();

		private readonly IGameSurface _mGame;
		private readonly List<Pick> _mMerged = new List<Pick>();
		private readonly HashSet<int> _mSeenTiles = new HashSet<int>();
		private readonly HashSet<int> _mVisited = new HashSet<int>();
		private List<int> _mFrontier = new List<int>();
		private List<int> _mNextFrontier = new List<int>();

		public PickComputation(IGameSurface game)
		{
			_mGame = game;
		}

		/// <summary>
		///     The one-game rule's reset: the cache goes empty, so the next pass scans from scratch. The
		///     sandbox-start patch calls it on the game's start thread, before that game's simulation thread is
		///     created: vanilla refuses to start a sandbox while one is shutting down, so no pass runs between
		///     the reset and the thread start, and the thread start is what makes the cleared cache visible to
		///     the simulation thread.
		/// </summary>
		public void ClearCache()
		{
			_mCache.Clear();
		}

		/// <summary>
		///     The fail-closed entry points: an unanswered frame, an unwired mode, an unanswered district
		///     definition, and an unanswerable army-level gate each return null, and the pass publishes
		///     nothing. A gate answer carrying any flag returns the empty set.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The army-level gate answers once per scan pass and never per tile, with no cost preview; each
		///         row of the mode-policy table names vanilla's shared army-state row first, because the camp and
		///         settler army overloads never run it.
		///     </para>
		/// </remarks>
		/// <returns>
		///     null when the pass cannot be answered, the empty set when the army cannot act, and
		///     otherwise the merged picks ascending by tile index.
		/// </returns>
		public Pick[] Compute(CursorContext context)
		{
			Pass pass = new Pass { Context = context };

			if (!_mGame.GetCurrentFrames(out pass.DistrictFrame, out pass.ConstructionFrame, out pass.VisibilityFrame))
			{
				return null;
			}

			if (!_mGame.GetTerritoryFrame(out pass.TerritoryFrame))
			{
				return null;
			}

			if (!ModePolicy.IsWired(context.Mode))
			{
				return null;
			}

			if (!_mGame.TryGetDistrictDefinition(context.Mode, out DistrictDefinition districtDefinition))
			{
				return null;
			}

			pass.DistrictDefinition = districtDefinition;

			if (!_mGame.TryGetArmyGateFailures(context.Mode, context.Army, out ArmyActionFailureFlags armyFailures))
			{
				return null;
			}

			if (armyFailures != ArmyActionFailureFlags.None)
			{
				return Array.Empty<Pick>();
			}

			_mMerged.Clear();
			_mSeenTiles.Clear();
			AddAdmitted(context.HoveredTerritory, context.TopHovered, pass);
			if (AddNeighbourhood(pass))
			{
				_mMerged.Clear();
				_mSeenTiles.Clear();
				AddAdmitted(context.HoveredTerritory, context.TopHovered, pass);
			}

			Pick[] picks = _mMerged.ToArray();
			Array.Sort(picks, (a, b) => a.Tile.CompareTo(b.Tile));
			return picks;
		}

		/// <summary>
		///     The placement neighborhood: the hovered territory plus RangeLevels - 1 hops over every adjacency
		///     edge (no transition filter), every tile of every territory - fixed at one hop, the immediately
		///     adjacent territories. No reachability, movement points, zone of control, cap, budget or
		///     truncation.
		/// </summary>
		/// <returns>
		///     true when an unanswerable edge set degraded the whole pass to hovered-only, which the
		///     caller repairs by restarting the merge; the degradation is logged through SpaLogger.Error.
		/// </returns>
		private bool AddNeighbourhood(Pass pass)
		{
			_mVisited.Clear();
			_mVisited.Add(pass.Context.HoveredTerritory);
			_mFrontier.Clear();
			_mFrontier.Add(pass.Context.HoveredTerritory);

			for (int level = 2; level <= pass.Context.RangeLevels && _mFrontier.Count > 0; level++)
			{
				_mNextFrontier.Clear();
				foreach (int frontier in _mFrontier)
				{
					if (!_mGame.TryGetAdjacentTerritories(frontier, out SnapshotTerritory.Adjacency[] edges))
					{
						SpaLogger.Error("placement-neighborhood adjacency: territory " + frontier);
						return true;
					}

					for (int e = 0; e < edges.Length; e++)
					{
						int territory = edges[e].TerritoryIndex;
						if (!_mVisited.Add(territory))
						{
							continue;
						}

						AddAdmitted(territory, pass.Context.TopNeighbour, pass);
						_mNextFrontier.Add(territory);
					}
				}

				(_mFrontier, _mNextFrontier) = (_mNextFrontier, _mFrontier);
			}

			return false;
		}

		/// <summary>
		///     Admission per territory: its quota plus the full top tie set, merged unique by tile index.
		/// </summary>
		private void AddAdmitted(int territoryIndex, int topCount, Pass pass)
		{
			Pick[] ranked = GetRankedPicks(territoryIndex, pass);
			int take = Admit(ranked, topCount);
			for (int i = 0; i < take; i++)
			{
				if (_mSeenTiles.Add(ranked[i].Tile))
				{
					_mMerged.Add(ranked[i]);
				}
			}
		}

		/// <summary>
		///     Cache serving. An entry serves the pass when the district, construction and visibility frames,
		///     the territory frame, the empire index and the mode all match.
		/// </summary>
		/// <remarks>
		///     <para>
		///         A territory-frame bump alone re-reads the territory facts and reuses the entry when every
		///         stamped fact matches; a changed fact, a district, construction or visibility frame bump, an empire
		///         change, and a mode change each recompute the entry. A new game never reaches this check with a
		///         stored entry: the sandbox-start patch empties the cache before its first pass, so a stamp that
		///         happens to line up with the previous game's entry has nothing to serve.
		///     </para>
		/// </remarks>
		/// <returns>The territory's ranked survivors.</returns>
		private Pick[] GetRankedPicks(int territoryIndex, Pass pass)
		{
			bool keyed = _mCache.TryGetValue(territoryIndex, out CacheEntry cached)
						 && cached.DistrictFrame == pass.DistrictFrame
						 && cached.ConstructionFrame == pass.ConstructionFrame
						 && cached.VisibilityFrame == pass.VisibilityFrame
						 && cached.EmpireIndex == pass.Context.EmpireIndex
						 && cached.Mode == pass.Context.Mode;
			if (!keyed)
			{
				return Recompute(territoryIndex, pass);
			}

			if (cached.TerritoryFrame == pass.TerritoryFrame)
			{
				return cached.Ranked;
			}

			if (_mGame.GetTerritoryFacts(territoryIndex, out TerritoryFacts facts) != TerritoryFactsResult.Known
				|| !StampMatches(cached, facts))
			{
				return Recompute(territoryIndex, pass);
			}

			cached.TerritoryFrame = pass.TerritoryFrame;
			_mCache[territoryIndex] = cached;
			return cached.Ranked;
		}

		/// <summary>
		///     Every stamped territory fact: edge of world, claim, settlement, city ownership, owner empire, and
		///     tile count.
		/// </summary>
		/// <returns>
		///     true when the stored entry's stamps cover the facts the game now reports. The check is
		///     complete, not claim-only: a territory-frame bump that changes none of them reuses the entry.
		/// </returns>
		private static bool StampMatches(CacheEntry cached, TerritoryFacts facts)
		{
			return cached.Known
				   && cached.IsEdgeOfTheWorld == facts.IsEdgeOfTheWorld
				   && cached.Claimed == facts.Claimed
				   && cached.HasSettlement == facts.HasSettlement
				   && cached.IsOwnedByCity == facts.IsOwnedByCity
				   && cached.OwnerEmpireIndex == facts.OwnerEmpireIndex
				   && cached.TileCount == facts.Tiles.Length;
		}

		/// <summary>
		///     Recomputation. The territory filter per mode comes first, and territory facts fail closed: an
		///     unknown territory never advises, it contributes no tile and asks no gate. Then the tile
		///     pre-filter, then the tile gate, then the resource-tile exclusion, then vanilla's scoring primitive
		///     and weight table.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The pre-filter runs before the gate, so a dropped tile costs no gate call, and a territory
		///         emptied by it costs the pass none.
		///     </para>
		/// </remarks>
		/// <returns>The territory's ranked survivors, stored as the territory's cache entry.</returns>
		private Pick[] Recompute(int territoryIndex, Pass pass)
		{
			List<Pick> survivors = new List<Pick>();

			bool known = _mGame.GetTerritoryFacts(territoryIndex, out TerritoryFacts facts)
						 == TerritoryFactsResult.Known;
			if (known && ModePolicy.IsTerritoryClaimable(pass.Context.Mode, facts, pass.Context.EmpireIndex))
			{
				int[] tiles = facts.Tiles;
				foreach (int tile in tiles)
				{
					if (_mGame.TestTilePrefilter(tile, pass.Context.EmpireIndex, pass.DistrictDefinition)
						!= TileVerdict.Pass)
					{
						continue;
					}

					if (!GateIsAdmissible(tile, pass))
					{
						continue;
					}

					if (_mGame.IsTileResourceTile(tile))
					{
						continue;
					}

					if (!_mGame.EvaluateTile(pass.Context.Army,
							tile,
							pass.DistrictDefinition,
							out DistrictPlacementEvaluation.ValidTile validTile,
							out FixedPoint score))
					{
						continue;
					}

					survivors.Add(new Pick { Tile = tile, Score = score, Data = validTile });
				}
			}

			Pick[] ranked = Rank(survivors);
			_mCache[territoryIndex] = new CacheEntry
			{
				Ranked = ranked,
				DistrictFrame = pass.DistrictFrame,
				ConstructionFrame = pass.ConstructionFrame,
				VisibilityFrame = pass.VisibilityFrame,
				TerritoryFrame = pass.TerritoryFrame,
				EmpireIndex = pass.Context.EmpireIndex,
				Mode = pass.Context.Mode,
				Known = known,
				IsEdgeOfTheWorld = facts.IsEdgeOfTheWorld,
				Claimed = facts.Claimed,
				HasSettlement = facts.HasSettlement,
				IsOwnedByCity = facts.IsOwnedByCity,
				OwnerEmpireIndex = facts.OwnerEmpireIndex,
				TileCount = known
								? facts.Tiles.Length
								: 0
			};
			return ranked;
		}

		/// <summary>
		///     The gate answers unmasked. Admission applies the membership mask: every failure except
		///     NotEnoughMoney and NotEnoughInfluence drops the tile, so an unaffordable tile stays recommended.
		/// </summary>
		/// <returns>true when the masked gate answer is clean; an unanswered gate is not admissible.</returns>
		private bool GateIsAdmissible(int tileIndex, Pass pass)
		{
			if (!_mGame.TryGetTileGateFailures(pass.Context.Mode, pass.Context.Army, tileIndex, out GateAnswer answer))
			{
				return false;
			}

			return ApplyMoneyInfluenceMask(answer.Failures) == ArmyActionFailureFlags.None;
		}

		private static ArmyActionFailureFlags ApplyMoneyInfluenceMask(ArmyActionFailureFlags failures)
		{
			failures.Remove(ArmyActionFailureFlags_Part1.NotEnoughMoney);
			failures.Remove(ArmyActionFailureFlags_Part1.NotEnoughInfluence);
			return failures;
		}

		/// <summary>
		///     Ranked survivors: score descending, tile ascending.
		/// </summary>
		private static Pick[] Rank(List<Pick> evaluated)
		{
			evaluated.Sort((a, b) =>
			{
				if (a.Score < b.Score)
				{
					return 1;
				}

				if (b.Score < a.Score)
				{
					return -1;
				}

				return a.Tile.CompareTo(b.Tile);
			});

			return evaluated.ToArray();
		}

		/// <summary>
		///     The quota plus every tile tied with the last admitted score. No tie cap.
		/// </summary>
		/// <returns>How many of the ranked survivors the territory admits.</returns>
		private static int Admit(Pick[] ranked, int quota)
		{
			int take = Math.Min(quota, ranked.Length);
			if (take > 0)
			{
				FixedPoint lastAdmittedScore = ranked[take - 1].Score;
				while (take < ranked.Length && ranked[take].Score == lastAdmittedScore)
				{
					take++;
				}
			}

			return take;
		}

		private struct Pass
		{
			public CursorContext Context;
			public DistrictDefinition DistrictDefinition;
			public int DistrictFrame;
			public int ConstructionFrame;
			public int VisibilityFrame;
			public int TerritoryFrame;
		}

		/// <summary>
		///     One territory's ranked survivors with the stamps that produced them.
		/// </summary>
		private struct CacheEntry
		{
			public Pick[] Ranked;
			public int DistrictFrame;
			public int ConstructionFrame;
			public int VisibilityFrame;
			public int TerritoryFrame;
			public int EmpireIndex;
			public ArmyActionType Mode;

			public bool Known;
			public bool IsEdgeOfTheWorld;
			public bool Claimed;
			public bool HasSettlement;
			public bool IsOwnedByCity;
			public int OwnerEmpireIndex;
			public int TileCount;
		}
	}
}
