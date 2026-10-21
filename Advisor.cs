using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Data.Simulation; // ArmyActionType.
using Amplitude.Mercury.Simulation;

namespace CampPlacementAdvisor
{
    /// <summary>
    /// THE ADVISOR: simulation-thread orchestration from the vanilla placement refresh to a
    /// published pick set. Config gates, hovered territory derivation, pick computation,
    /// evaluation writer, publish. No rule logic, no evaluation array writes, no presentation
    /// calls (the star renderer subscribes lazily from here).
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
            if (!Plugin.CfgEnabled.Value || !m_Game.Ready)
            {
                PublishEmpty(evaluation);
                return;
            }
            if (evaluation.SettleType != ArmyActionType.CreateCamp)
            {
                PublishEmpty(evaluation);
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
                PublishEmpty(evaluation); // game facts unavailable: advisor deactivates
                return;
            }

            m_Writer.Write(evaluation, picks, cursorTile);

            // Canonical pick set: fully filled ascending tiles, no holes.
            var tiles = new int[picks.Length];
            for (int i = 0; i < picks.Length; i++)
                tiles[i] = picks[i].Tile;
            PickSet.Publish(tiles);
        }

        public void PublishEmpty(CampPlacementEvaluation evaluation)
        {
            m_Writer.ClearTail(evaluation);
            PickSet.Publish(null);
        }
    }
}
