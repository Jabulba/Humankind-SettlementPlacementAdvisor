using System.Linq;

namespace SettlementPlacementAdvisor.pipeline
{
	/// <summary>
	///     The immutable result of one pick computation: the published tiles, ascending and unique by tile
	///     index, that the pin gate answers membership on.
	/// </summary>
	internal sealed class PickSet
	{
		public readonly int[] Tiles;

		private PickSet(int[] tiles)
		{
			Tiles = tiles;
		}

		public bool Contains(int tileIndex)
		{
			int[] tiles = Tiles;
			if (tiles == null)
			{
				return false;
			}

			int lo = 0;
			int hi = tiles.Length - 1;
			while (lo <= hi)
			{
				int mid = (lo + hi) >> 1;
				int value = tiles[mid];
				if (value == tileIndex)
				{
					return true;
				}

				if (value < tileIndex)
				{
					lo = mid + 1;
				}
				else
				{
					hi = mid - 1;
				}
			}

			return false;
		}

		/// <summary>
		///     An unchanged set returns the previous instance, so an equal publish skips the cross-thread swap.
		/// </summary>
		/// <returns>The set to publish: the previous instance when the tiles match, a new set otherwise.</returns>
		public static PickSet Build(int[] tiles, PickSet previous)
		{
			if ((tiles == null
				 && previous?.Tiles == null)
				|| (previous?.Tiles != null
					&& tiles?.Length == previous.Tiles.Length
					&& SameTiles(previous.Tiles, tiles)))
			{
				return previous;
			}

			return new PickSet(tiles);
		}

		private static bool SameTiles(int[] a, int[] b)
		{
			if (a.Length != b.Length)
			{
				return false;
			}

			return !a.Where((t, i) => t != b[i]).Any();
		}
	}
}
