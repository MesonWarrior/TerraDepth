using System;
using System.Buffers;
using Terraria;

namespace TerraDepth.World
{
	/// <summary>
	/// Vanilla physics only knows Main.tile, which holds the live slice (or the middle slice outside the live window).
	/// An entity at another depth is updated with the cells around it temporarily replaced by its own slice, so it falls,
	/// stands and collides on the ground where it really is. Whatever the update changed there is stored in that slice.
	/// </summary>
	public static class SliceSwap
	{
		/// <summary>Slice whose content Main.tile holds at (x, y).</summary>
		public static int LiveSliceAt(int x, int y) => Voxel.WindowOpen && Voxel.InWindow(x, y) ? Voxel.WinZ : Voxel.ZC;

		/// <summary>Whether an entity at depth z near (x, y) needs its own slice put into Main.tile.</summary>
		public static bool Needed(int x, int y, float z) => Voxel.Ready && (int)Math.Floor(z) != LiveSliceAt(x, y);

		public static void Run(int x0, int y0, int x1, int y1, float depth, Action body)
		{
			int z = Math.Clamp((int)Math.Floor(depth), 0, Voxel.D - 1);
			x0 = Math.Max(1, x0);
			y0 = Math.Max(1, y0);
			x1 = Math.Min(Main.maxTilesX - 2, x1);
			y1 = Math.Min(Main.maxTilesY - 2, y1);
			int w = x1 - x0 + 1, h = y1 - y0 + 1;
			if (w <= 0 || h <= 0 || w * h > 8192) {
				body();
				return;
			}
			int n = w * h;
			VTile[] saved = ArrayPool<VTile>.Shared.Rent(n);
			VTile[] written = ArrayPool<VTile>.Shared.Rent(n);
			try {
				for (int x = 0; x < w; x++)
					for (int y = 0; y < h; y++) {
						Tile t = Main.tile[x0 + x, y0 + y];
						int k = x * h + y;
						saved[k] = VTile.From(t);
						written[k] = Voxel.Get(x0 + x, y0 + y, z);
						written[k].WriteTo(t);
					}
				body();
			}
			finally {
				for (int x = 0; x < w; x++)
					for (int y = 0; y < h; y++) {
						Tile t = Main.tile[x0 + x, y0 + y];
						int k = x * h + y;
						VTile now = VTile.From(t);
						saved[k].WriteTo(t);
						if (!VTile.SameContent(now, written[k]))
							Voxel.Set(x0 + x, y0 + y, z, now);
					}
				ArrayPool<VTile>.Shared.Return(saved);
				ArrayPool<VTile>.Shared.Return(written);
			}
		}
	}
}
