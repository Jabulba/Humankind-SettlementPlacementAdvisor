using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Data.Simulation; // ArmyActionType.
using Amplitude.Mercury.Simulation;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// THE ADVISOR: simulation-thread orchestration from the vanilla placement refresh to a
    /// published pick set. Readiness gates, hovered territory derivation, pick computation,
    /// evaluation writer, publish. No rule logic, no evaluation array writes, no presentation
    /// calls (the star renderer subscribes lazily from here). Contract: the advisor is a
    /// STRICT NO-OP on every evaluation it does not own, i.e. on all non-camp placement modes
    /// (see Run), where it only ever deactivates its own pick set.
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
            // The camp placement evaluation is SHARED by FoundCity and FoundCityWithSettler
            // (CampPlacementEvaluation.GetExtensionAndPackage switches on SettleType), and the
            // vanilla glyph display consumes ValidTiles[0..ValidTileCount) with
            // onlyShowShowable, so any write here would restyle city placement. Non-camp
            // evaluations are untouched: only our own pick set is deactivated, so no advisor
            // star survives into city placement and vanilla keeps its glyphs, pins and cursor
            // entry exactly as if the mod were absent.
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

        /// <summary>
        /// Deactivate WITHOUT writing the evaluation: publish an inactive pick set only. Every
        /// non-camp placement mode and every failure path take this route. Publishing an
        /// already-inactive set publishes nothing, so per-mouse-move calls here are free.
        /// </summary>
        public void Deactivate()
        {
            PickSet.Publish(null);
        }

        /// <summary>
        /// Camp placement only: drop the advisor's tail contribution, then deactivate. A
        /// non-camp call is inert (the writer's gate returns before touching the array), but
        /// keep it out of the non-camp paths so the intent stays visible at the call site:
        /// those take Deactivate, which never touches an evaluation.
        /// </summary>
        public void PublishEmpty(CampPlacementEvaluation evaluation)
        {
            m_Writer.ClearTail(evaluation);
            PickSet.Publish(null);
        }
    }
}
