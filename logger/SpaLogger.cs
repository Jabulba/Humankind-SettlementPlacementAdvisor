using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using SettlementPlacementAdvisor.composition;

namespace SettlementPlacementAdvisor.logger
{
	/// <summary>
	///     The advisor's logging seam: every line it sends to the BepInEx log carries the SPA tag, so a
	///     reader can pick the advisor's own lines out of the game's log.
	/// </summary>
	internal static class SpaLogger
	{
		private static ManualLogSource _log;

		internal static void Init(ManualLogSource log)
		{
			_log = log;
		}

		/// <summary>
		///     The one place the SPA tag is written: every BepInEx line this class logs runs through it, so
		///     the tag cannot drift between call sites.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The load-failure file is the exception. Its own line is stamped with the plugin name and
		///         the mod version, and carries no tag.
		///     </para>
		/// </remarks>
		/// <param name="message">The message body, left as it came.</param>
		/// <returns>The message with the tag in front of it.</returns>
		private static string Tagged(string message)
		{
			return "[SPA] " + message;
		}

		public static void Log(string message)
		{
			_log.LogInfo(Tagged(message));
		}

		public static void Error(string message)
		{
			_log.LogError(Tagged("advisor failure: " + message));
		}

		public static void Fatal(string message)
		{
			_log.LogFatal(Tagged(message));

			try
			{
				File.AppendAllText(
					Path.Combine(Paths.BepInExRootPath, AdvisorConfig.PluginName + "-load-failure.log"),
					DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ")
					+ AdvisorConfig.PluginName
					+ " "
					+ ModVersion.Value
					+ ": "
					+ message
					+ Environment.NewLine);
			}
			catch (Exception e)
			{
				_log.LogFatal(Tagged("load-failure file not written: " + e.Message));
			}
		}
	}
}
