using System;
using System.Diagnostics;
using System.IO;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// The composition root's load gate (SPEC section 8): an unresolved game member is a hard, loud
    /// load failure, never a quiet disable. The failure list is logged in full, then the game process
    /// is closed, and control never returns to the advisor wiring, so a partial binding never runs.
    /// Unity-free by design: the log, the durable file path and the close mechanism are all injected
    /// or parameterized, so the harness drives the same decision the composition root takes in the
    /// game, with the harness's own log file and a close that returns.
    /// </summary>
    internal static class LoadGate
    {
        /// <summary>
        /// The load-failure line: every reflected member name the game build renamed, in the shape a
        /// player reports with the game version.
        /// </summary>
        public static string FailureLine(string resolveError)
        {
            return "Load failure, unresolved game members: " + resolveError
                + ". The mod refuses to load; report this line with the game version.";
        }

        /// <summary>
        /// The startup summary: every resolved member name, and the tail capacity the writer reserves
        /// for the config this load reads.
        /// </summary>
        public static string ReadyLine(string[] resolvedNames, int tailCapacity)
        {
            return resolvedNames.Length + " game members resolved: " + string.Join(", ", resolvedNames)
                + "; tail capacity " + tailCapacity;
        }

        /// <summary>
        /// Refuse to load an unresolved surface: log the failure list, then close the game, in that
        /// order, so the failure reaches every log before the process dies. A resolved surface passes
        /// through with nothing logged and the game open. The call NEVER returns on an unresolved
        /// surface: close must end the process, and when it does not (a headless caller), the failure
        /// is thrown so nothing continues on a partial binding.
        /// </summary>
        public static void EnforceResolvedSurface(Action<string> logFailure, Action closeGame)
        {
            if (GameApi.ReadyStatic) return;

            var line = FailureLine(GameApi.ResolveError);
            logFailure(line);
            closeGame();
            throw new InvalidOperationException(line);
        }

        /// <summary>
        /// The durable copy of the failure line: written, flushed and closed before the game closes.
        /// Needed because BepInEx 5's log file writer (DiskLogListener) is a synchronized StreamWriter
        /// that flushes on a 2 second timer, so a hard exit drops buffered lines, and it holds
        /// LogOutput.log open with FileShare.Read, so no other writer can append to it.
        /// </summary>
        public static void AppendFailureLine(string logPath, string line)
        {
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine);
        }

        /// <summary>
        /// The mechanism the game closes with: one flush tick for BepInEx's log writer, then the process
        /// is killed, which ends it from the load thread and closes the game. Chosen over the
        /// alternatives, every one of them measured in-game on the 1.0.31.4851 Mono/Proton boot with an
        /// intentionally unresolvable member: Environment.Exit hangs Unity's shutdown during plugin
        /// Awake, leaving the game open and frozen; Application.Quit is not honoured in that phase; and
        /// a thrown exception alone is swallowed by BepInEx's per-plugin load handler, which keeps
        /// booting the game. The sleep is one interval past DiskLogListener's 2 second flush timer, so
        /// the failure list also lands in LogOutput.log; the game's own Player.log and the mod's
        /// load-failure file already hold it, and only then is the process ended.
        /// </summary>
        public static void CloseGame()
        {
            System.Threading.Thread.Sleep(2500);
            Process.GetCurrentProcess().Kill();
        }
    }
}
