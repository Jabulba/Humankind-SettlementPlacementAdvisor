using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Data.Simulation; // ArmyActionType.
using Amplitude.Mercury.Simulation;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// Simulation-thread orchestration from the vanilla placement refresh to a published pick
    /// set. It owns no rule logic or evaluation writes, and is a strict no-op on every
    /// evaluation it does not own: non-settlement modes only deactivate the advisor's pick set.
    /// </summary>
    internal sealed class Advisor
    {
        private readonly GameSurface m_Game;
        private readonly PickComputation m_Picks;
        private readonly EvaluationWriter m_Writer;
        private readonly StarRenderer m_Stars;

        public Advisor(GameSurface game, PickComputation picks, EvaluationWriter writer, StarRenderer stars)
        {
            m_Game = game;
            m_Picks = picks;
            m_Writer = writer;
            m_Stars = stars;
        }

        public void Run(CampPlacementEvaluation evaluation, int cursorTile, SimulationEntityGUID armyGuid)
        {
            // FoundCity and FoundCityWithSettler share this evaluation type, and vanilla
            // displays its glyphs from ValidTiles[0..ValidTileCount), so writing here would
            // restyle city placement. Non-settlement modes may only deactivate our pick set.
            if (!m_Game.Ready || evaluation.SettleType != ArmyActionType.CreateCamp)
            {
                Deactivate();
                return;
            }

            var army = m_Game.TryGetArmy(armyGuid);
            if (army == null)
            {
                PublishEmpty(evaluation);
                return;
            }

            // Wire star rendering lazily: Snapshots.SettlementPlacementCursorSnapshot is
            // null at plugin Awake (the snapshot instance is created on first placement).
            m_Stars.EnsureSubscription();

            var hovered = m_Game.TerritoryOfTile(cursorTile);
            if (hovered < 0)
            {
                PublishEmpty(evaluation);
                return;
            }

            var picks = m_Picks.Compute(CursorContext.For(hovered, army,
                m_Game.GetArmyEmpireIndex(army),
                Plugin.CfgTopHovered.Value, Plugin.CfgTopNeighbour.Value, Plugin.CfgRangeLevels.Value));
            if (picks == null)
            {
                PublishEmpty(evaluation);
                return;
            }

            m_Writer.Write(evaluation, picks, cursorTile);

            var tiles = new int[picks.Length];
            for (int i = 0; i < picks.Length; i++)
                tiles[i] = picks[i].Tile;
            PickSet.Publish(tiles);
        }

        /// <summary>
        /// Publish an inactive pick set without touching the evaluation. Used by every
        /// non-settlement mode and failure path; republishing an inactive set is a no-op.
        /// </summary>
        public void Deactivate()
        {
            PickSet.Publish(null);
        }

        /// <summary>
        /// Settlement placement only: drop the advisor's tail contribution, then deactivate.
        /// </summary>
        public void PublishEmpty(CampPlacementEvaluation evaluation)
        {
            m_Writer.ClearTail(evaluation);
            PickSet.Publish(null);
        }
    }
}
