using System.Collections.Generic;
using Amplitude;
using Amplitude.Mercury.Interop;
using PresentationRoot = Amplitude.Mercury.Presentation.Presentation;
using FeedbackController = Amplitude.Mercury.Presentation.PresentationTileFeedbackController;
using BuildEnums = Amplitude.Mercury.Data.Presentation.PresentationEntityLevelBuildEnums;

namespace SettlementPlacementAdvisor
{
    /// <summary>Tile-feedback seam that keeps the renderer free of presentation types.</summary>
    internal interface PickCanvas
    {
        /// <summary>False while the presentation feedback controller does not exist.</summary>
        bool Available { get; }

        /// <summary>Start the star fx on a tile; 0 when unavailable (never stored as a handle).</summary>
        int StartFx(int tileIndex);

        /// <summary>Stop a started fx. No-op for the 0 handle.</summary>
        void StopFx(int fxHandle);
    }

    internal sealed class UnityPickCanvas : PickCanvas
    {
        private StaticString m_Group = new StaticString("SettlementPlacementAdvisorGroup");

        public bool Available
        {
            get { return PresentationRoot.PresentationTileFeedbackController != null; }
        }

        public int StartFx(int tileIndex)
        {
            var controller = PresentationRoot.PresentationTileFeedbackController;
            if (controller == null)
                return 0;
            var group = m_Group;
            return controller.StartFeedback(tileIndex, 0f, ref group,
                BuildEnums.TileFeedback.BuildableAdvisedNotHovered,
                new UnityEngine.Color(1f, 0.84f, 0.2f, 1f),
                FeedbackController.OptionsEnum.None, 0);
        }

        public void StopFx(int fxHandle)
        {
            var controller = PresentationRoot.PresentationTileFeedbackController;
            if (controller == null || fxHandle <= 0)
                return;
            var fx = fxHandle;
            controller.StopFeedback(ref fx);
        }
    }

    /// <summary>
    /// Draws the published picks on the pick canvas, on the cursor snapshot's presentation event
    /// as the cadence signal: what is drawn is the presentation channel, never the event's own
    /// data. Entering tiles start fx, leaving tiles stop fx, retained tiles keep their fx.
    /// Membership is answered from the captured snapshot, so a mid-pass publish
    /// cannot tear the reconcile. Stars are the shown set's first rendering; the cursor ghost
    /// and vanilla's own glyphs stay vanilla's.
    /// </summary>
    internal sealed class StarRenderer
    {
        private readonly PickCanvas m_Canvas;
        private readonly Dictionary<int, int> m_FxByTile = new Dictionary<int, int>();
        private readonly List<int> m_StaleTiles = new List<int>();
        private bool m_Subscribed;
        private bool m_DeferredLogged;
        private int m_RenderedVersion = -1; // -1 = cleared, 0 = published empty

        public StarRenderer(PickCanvas canvas)
        {
            m_Canvas = canvas;
        }

        /// <summary>
        /// Subscribe lazily: the snapshot exists only after the game creates it, long after
        /// plugin Awake (subscribing in Awake would NRE).
        /// </summary>
        public void EnsureSubscription()
        {
            if (m_Subscribed)
                return;
            var snapshot = Snapshots.SettlementPlacementCursorSnapshot;
            if (snapshot == null)
                return;
            snapshot.PresentationDataChanged += OnPresentationDataChanged;
            m_Subscribed = true;
        }

        public void Render(PickSet set)
        {
            int version = set == null ? 0 : set.Version;
            if (version == m_RenderedVersion)
                return;

            var canvas = m_Canvas;
            if (!canvas.Available)
            {
                if (!m_DeferredLogged)
                {
                    m_DeferredLogged = true;
                    var deferred = Plugin.Instance;
                    if (deferred != null)
                        deferred.Log("feedback controller unavailable; star render deferred");
                }
                return; // version NOT recorded: the next presentation event retries
            }

            int enteringCount = 0;
            int leavingCount = 0;

            if (set == null)
            {
                foreach (var fx in m_FxByTile.Values)
                    canvas.StopFx(fx);
                leavingCount = m_FxByTile.Count;
                m_FxByTile.Clear();
            }
            else
            {
                var leaving = set.Leaving;
                for (int i = 0; i < leaving.Length; i++)
                {
                    int fx;
                    if (m_FxByTile.TryGetValue(leaving[i], out fx))
                    {
                        canvas.StopFx(fx);
                        m_FxByTile.Remove(leaving[i]);
                        leavingCount++;
                    }
                }

                // Reconcile fx we track that THIS snapshot does not contain: covers versions
                // skipped while the canvas was unavailable. The captured set is the single source.
                m_StaleTiles.Clear();
                foreach (var entry in m_FxByTile)
                    if (!set.Contains(entry.Key))
                        m_StaleTiles.Add(entry.Key);
                for (int i = 0; i < m_StaleTiles.Count; i++)
                {
                    int fx = m_FxByTile[m_StaleTiles[i]];
                    canvas.StopFx(fx);
                    m_FxByTile.Remove(m_StaleTiles[i]);
                    leavingCount++;
                }

                var entering = set.Entering;
                for (int i = 0; i < entering.Length; i++)
                {
                    int fx = canvas.StartFx(entering[i]);
                    if (fx > 0)
                        m_FxByTile[entering[i]] = fx; // 0 (unavailable) is never stored
                    enteringCount++;
                }
            }

            var plugin = Plugin.Instance;
            if (plugin != null)
                plugin.Log("stars v=" + version + " entering=" + enteringCount + " leaving=" + leavingCount);
            m_RenderedVersion = version; // recorded only after a successful pass
        }

        /// <summary>Mode exit (presentation thread): stop every tracked fx, forget the version.</summary>
        public void Clear()
        {
            m_RenderedVersion = -1;
            var canvas = m_Canvas;
            foreach (var fx in m_FxByTile.Values)
                canvas.StopFx(fx);
            m_FxByTile.Clear();
        }

        private void OnPresentationDataChanged()
        {
            Render(PresentationView.Current.Picks); // capture the published picks exactly once per pass
        }
    }
}
