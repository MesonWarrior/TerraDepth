using System;
using Microsoft.Xna.Framework;
using TerraDepth.World;

namespace TerraDepth.Rendering
{
	/// <summary>
	/// Voxel DDA in "tile space": x right, y down (tile rows), z depth. One unit = one tile.
	/// </summary>
	public static class Raycast
	{
		public struct Hit
		{
			public int X, Y, Z;
			public int NX, NY, NZ;
			public float Distance;
			public Vector3 Point;
		}

		/// <summary>True when no opaque block lies strictly between the two points (light occlusion).</summary>
		public static bool LineClear(Vector3 from, Vector3 to)
		{
			Vector3 d = to - from;
			float len = d.Length();
			if (len < 0.9f)
				return true;
			// stop half a cell early: the end point is the air cell lit in front of a face
			return !Cast(from, d, len - 0.5f, v => v.IsOpaqueCube, out _);
		}

		public static bool Cast(Vector3 origin, Vector3 dir, float maxDist, Func<VTile, bool> pred, out Hit hit)
		{
			hit = default;
			if (dir.LengthSquared() < 1e-8f)
				return false;
			dir.Normalize();
			int x = (int)Math.Floor(origin.X), y = (int)Math.Floor(origin.Y), z = (int)Math.Floor(origin.Z);
			int stepX = Math.Sign(dir.X), stepY = Math.Sign(dir.Y), stepZ = Math.Sign(dir.Z);
			float tDeltaX = stepX != 0 ? Math.Abs(1f / dir.X) : float.MaxValue;
			float tDeltaY = stepY != 0 ? Math.Abs(1f / dir.Y) : float.MaxValue;
			float tDeltaZ = stepZ != 0 ? Math.Abs(1f / dir.Z) : float.MaxValue;
			float tMaxX = stepX > 0 ? (x + 1 - origin.X) * tDeltaX : stepX < 0 ? (origin.X - x) * tDeltaX : float.MaxValue;
			float tMaxY = stepY > 0 ? (y + 1 - origin.Y) * tDeltaY : stepY < 0 ? (origin.Y - y) * tDeltaY : float.MaxValue;
			float tMaxZ = stepZ > 0 ? (z + 1 - origin.Z) * tDeltaZ : stepZ < 0 ? (origin.Z - z) * tDeltaZ : float.MaxValue;
			int nx = 0, ny = 0, nz = 0;
			float t = 0f;
			for (int i = 0; i < 512 && t <= maxDist; i++) {
				if (Voxel.InBounds(x, y, z) && pred(Voxel.Get(x, y, z))) {
					hit.X = x; hit.Y = y; hit.Z = z;
					hit.NX = nx; hit.NY = ny; hit.NZ = nz;
					hit.Distance = t;
					hit.Point = origin + dir * t;
					return true;
				}
				if (tMaxX < tMaxY && tMaxX < tMaxZ) {
					x += stepX; t = tMaxX; tMaxX += tDeltaX; nx = -stepX; ny = 0; nz = 0;
				}
				else if (tMaxY < tMaxZ) {
					y += stepY; t = tMaxY; tMaxY += tDeltaY; nx = 0; ny = -stepY; nz = 0;
				}
				else {
					z += stepZ; t = tMaxZ; tMaxZ += tDeltaZ; nx = 0; ny = 0; nz = -stepZ;
				}
			}
			return false;
		}
	}
}
