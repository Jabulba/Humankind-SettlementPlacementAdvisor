using Amplitude; // FixedPoint: the cursor slot's instant cost trio
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Data.Simulation; // ArmyActionType
using Amplitude.Mercury.Simulation;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// Simulation-thread orchestration from the vanilla placement snapshot to a published pick
    /// set. It owns no rule logic and writes no evaluation content: the pipeline lives in
    /// PickComputation, the writes in EvaluationWriter. It reads the facts the cursor slot needs -
    /// the mode gate call with no mask, so the hovered tile's failure flags and instant costs reach
    /// the writer faithfully - and hands them over; the writer owns the slot. Both entry points run
    /// the same mode-policy dispatch on the stamped SettleType, and an unwired mode is left entirely
    /// to vanilla: the advisor's own pipeline (SPEC section 2 steps 3-8) never runs for it.
    /// </summary>
    internal sealed class Advisor
    {
        private readonly GameSurface m_Game;
        private readonly PickComputation m_Picks;
        private readonly EvaluationWriter m_Writer;
        private readonly StarRenderer m_Stars;

        /// <summary>What a pipeline run produced. The outcome drives the display contract, and the
        /// difference matters: only a wired run that found nothing owns the evaluation tail.</summary>
        private enum Outcome
        {
            /// <summary>Not our mode, or the surface cannot answer: INACTIVE, vanilla's work untouched.</summary>
            NotAdvisable,
            /// <summary>A wired run with no admission set: INACTIVE, and the advisor's tail is cleared.</summary>
            NoPicks,
            /// <summary>A wired run with an admission set: publish, and write the tail on the refresh.</summary>
            Picks,
        }

        public Advisor(GameSurface game, PickComputation picks, EvaluationWriter writer, StarRenderer stars)
        {
            m_Game = game;
            m_Picks = picks;
            m_Writer = writer;
            m_Stars = stars;
        }

        /// <summary>
        /// Scan-pass entry, at the EvaluatePlacement takeover (SPEC section 1): the pass that fills
        /// the per-territory cache and publishes the admission set for the cursor territory. It
        /// writes no evaluation - the write belongs to the refresh, which serves from the cache - so
        /// it publishes no cursor-slot failure of its own: the hover path supplies that fact.
        /// </summary>
        public void RunScanPass(CampPlacementEvaluation evaluation, int cursorTile, SimulationEntityGUID armyGuid)
        {
            object army;
            var picks = Compute(out var outcome, out army, evaluation, cursorTile, armyGuid);
            if (outcome != Outcome.Picks)
            {
                // The scan pass writes nothing, so there is no tail to clear: an activation that
                // found nothing leaves the vanilla evaluation exactly as vanilla left it.
                Deactivate(evaluation.SettleType);
                return;
            }
            Publish(picks, evaluation.SettleType, ArmyActionFailureFlags.None);
        }

        /// <summary>
        /// Per-refresh entry (SPEC section 1 row 4, SPEC section 5 row 5): the cursor slot plus the
        /// admission set. The advisor writes the cursor slot itself from the mode gate call with no
        /// mask, so the failure flags and the three instant costs reach the camp mouse marker and the
        /// hovered-pin rule, and the cursor index points at that slot: the cursor-slot facts vanilla's
        /// own refresh body produces are produced here, with the mode's gate in place of the
        /// camp-only one. A zero-pick result publishes INACTIVE, which displays nothing: stars
        /// cleared, no cursor entry, no pick entries, no pin, and no vanilla surface taking the
        /// advice's place - and it takes the advisor's tail contribution with it.
        /// </summary>
        public void Run(CampPlacementEvaluation evaluation, int cursorTile, SimulationEntityGUID armyGuid)
        {
            object army;
            var picks = Compute(out var outcome, out army, evaluation, cursorTile, armyGuid);
            var mode = evaluation.SettleType;
            switch (outcome)
            {
                case Outcome.Picks:
                {
                    var cursor = CursorSlotFacts(evaluation, army, cursorTile);
                    m_Writer.Write(evaluation, picks, cursor);
                    Publish(picks, mode, cursor.Failure);
                    break;
                }
                case Outcome.NoPicks:
                    PublishEmpty(evaluation); // no cursor entry, no glyphs, no pick entries, no pins
                    break;
                default:
                    Deactivate(mode); // vanilla's mode: its entries, its cursor entry, its glyphs stay
                    break;
            }
        }

        /// <summary>
        /// The refresh decision the takeover patch asks (SPEC section 1 row 4): in a wired mode with
        /// an answering surface the advisor owns the cursor slot, so vanilla's hover append rule and
        /// its resize-by-10 die with the body that housed them and nothing lands above the reserved
        /// window. Anywhere else vanilla keeps running its own refresh, its own cursor entry
        /// included, and nothing is double-written.
        /// </summary>
        public bool OwnsRefresh(CampPlacementEvaluation evaluation)
        {
            return m_Game.Ready && ModePolicy.IsWired(evaluation.SettleType);
        }

        /// <summary>
        /// The cursor slot's content for one refresh (SPEC section 5 row 5): the mode gate call with
        /// NO mask, so the money and influence failures stay and the cost trio is what the camp mouse
        /// marker shows, plus the hovered tile's own scored entry the ghost and the tile feedback
        /// read. An unanswerable gate is the no-entry shape: nothing is claimed about the hovered
        /// tile, and the cursor slot stays scrubbed.
        /// </summary>
        private CursorSlot CursorSlotFacts(CampPlacementEvaluation evaluation, object army, int cursorTile)
        {
            var unanswered = CursorSlot.Unanswered;
            if (army == null) return unanswered;

            var mode = evaluation.SettleType;
            ArmyActionFailureFlags failure;
            FixedPoint money, influence, unit;
            if (!m_Game.TryGetTileGateFailures(mode, army, cursorTile, false,
                out failure, out money, out influence, out unit))
                return unanswered;

            object definition;
            DistrictPlacementEvaluation.ValidTile entry;
            FixedPoint score;
            if (!m_Game.TryGetDistrictDefinition(mode, out definition) ||
                !m_Game.EvaluateTile(army, cursorTile, definition, out entry, out score))
                return unanswered;

            entry.Showable = false; // no glyph on the cursor slot: the pick slots own the shown set
            var slot = new CursorSlot();
            slot.Answered = true;
            slot.Tile = cursorTile;
            slot.Entry = entry;
            slot.Failure = failure;
            slot.MoneyCost = money;
            slot.InfluenceCost = influence;
            slot.UnitCost = unit;
            return slot;
        }

        /// <summary>
        /// The pipeline entry both entry points share, in order: mode-policy dispatch on the stamped
        /// SettleType, the cursor context, the army-level gate, then the rules. Null means no
        /// admission set; the outcome says whether the advisor owned this run. The placing army
        /// comes out with the picks: the cursor slot's gate call needs it.
        /// </summary>
        private Pick[] Compute(out Outcome outcome, out object army, CampPlacementEvaluation evaluation,
            int cursorTile, SimulationEntityGUID armyGuid)
        {
            outcome = Outcome.NotAdvisable;
            army = null;
            var picks = null as Pick[];
            if (!m_Game.Ready) return picks; // unanswerable game fact: nothing to advise in any mode

            // Mode-policy dispatch (SPEC section 2 step 1): the settlement row and the FoundCity
            // row run the pipeline; the settler row and anything unmapped stay vanilla's.
            var mode = evaluation.SettleType;
            if (!ModePolicy.IsWired(mode)) return picks;

            army = m_Game.TryGetArmy(armyGuid);
            if (army == null)
            {
                outcome = Outcome.NoPicks; // our mode, and the army the advice hangs on is unanswerable
                return picks;
            }

            // Wire star rendering lazily: Snapshots.SettlementPlacementCursorSnapshot is null at
            // plugin Awake (the snapshot instance is created on the first placement).
            m_Stars.EnsureSubscription();

            // The cursor context: only a tile inside the world has a territory.
            var hovered = m_Game.TerritoryOfTile(cursorTile);
            if (hovered < 0)
            {
                outcome = Outcome.NoPicks;
                return picks;
            }

            // The pipeline: mode policy, army-level gate, territory filter, pre-filter, tile
            // gate, scoring, admission. An empty admission set (army gate hit, a territory no
            // tile survives, an unanswerable fact) publishes INACTIVE and writes nothing.
            var context = CursorContext.For(hovered, army, m_Game.GetArmyEmpireIndex(army),
                Plugin.CfgTopHovered.Value, Plugin.CfgTopNeighbour.Value, Plugin.CfgRangeLevels.Value, mode);
            picks = m_Picks.Compute(context);
            outcome = picks == null || picks.Length == 0 ? Outcome.NoPicks : Outcome.Picks;
            return picks;
        }

        /// <summary>
        /// Publish the admission set through the single presentation channel: the pick content, the
        /// mode it was published for, and the cursor slot's unmasked gate answer, in one atomic
        /// swap. Tiles ascend because the pick merge sorts them ascending.
        /// </summary>
        private static void Publish(Pick[] picks, ArmyActionType mode, ArmyActionFailureFlags settleFailure)
        {
            var tiles = new int[picks.Length];
            for (int i = 0; i < picks.Length; i++)
                tiles[i] = picks[i].Tile;
            PresentationView.Publish(tiles, mode, settleFailure);
        }

        /// <summary>
        /// Publish the inactive view for `mode` without touching the evaluation. Republishing an
        /// inactive view is a no-op, so a session that advises nothing costs no version churn per
        /// refresh.
        /// </summary>
        public void Deactivate(ArmyActionType mode)
        {
            PresentationView.PublishInactive(mode);
        }

        /// <summary>
        /// Taken-over mode only: drop the advisor's tail contribution, then deactivate.
        /// </summary>
        public void PublishEmpty(CampPlacementEvaluation evaluation)
        {
            m_Writer.ClearTail(evaluation);
            Deactivate(evaluation.SettleType);
        }
    }
}
