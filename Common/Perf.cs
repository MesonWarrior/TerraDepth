using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Terraria;
using Terraria.ModLoader;

namespace TerraDepth.Common
{
	/// <summary>
	/// Frame time breakdown written to the tModLoader log (client.log) every 10 seconds while the game runs slowly,
	/// so a slowdown can be traced to the part of the mod that causes it. Nothing is shown in game.
	/// </summary>
	public static class Perf
	{
		private static readonly Dictionary<string, double> sums = new();
		private static readonly Stopwatch window = Stopwatch.StartNew();
		private static readonly Stopwatch frameClock = Stopwatch.StartNew();
		private static double frameSum, frameMax;
		private static int frames;

		public static long Now => Stopwatch.GetTimestamp();

		/// <summary>Adds the time since <paramref name="start"/> to a section and returns the current timestamp.</summary>
		public static long Lap(string section, long start)
		{
			long now = Stopwatch.GetTimestamp();
			double ms = (now - start) * 1000.0 / Stopwatch.Frequency;
			sums[section] = sums.TryGetValue(section, out double s) ? s + ms : ms;
			return now;
		}

		/// <summary>Called once per drawn frame.</summary>
		public static void Frame()
		{
			double ms = frameClock.Elapsed.TotalMilliseconds;
			frameClock.Restart();
			frames++;
			frameSum += ms;
			if (ms > frameMax)
				frameMax = ms;
			if (window.Elapsed.TotalSeconds < 10)
				return;
			double avg = frameSum / frames;
			if (avg > 20.0) {
				string parts = string.Join(", ", sums.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value / frames:0.00}"));
				ModContent.GetInstance<TerraDepthMod>().Logger.Info(
					$"TerraDepth perf: frame avg {avg:0.0} ms (max {frameMax:0.0}), fps {Main.frameRate}; per frame ms: {parts}");
			}
			sums.Clear();
			frames = 0;
			frameSum = frameMax = 0;
			window.Restart();
		}
	}

	public class PerfSystem : ModSystem
	{
		private static long updateStart;

		public override void PreUpdateEntities() => updateStart = Perf.Now;

		public override void PostUpdateEverything()
		{
			if (updateStart != 0)
				Perf.Lap("game update (vanilla + mod entities)", updateStart);
			updateStart = 0;
		}
	}
}
