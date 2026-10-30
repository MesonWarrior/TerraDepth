using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TerraDepth.World;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraDepth.Rendering
{
	public struct LightSource
	{
		public Vector3 Pos;
		public Vector3 Color;
		public float Radius;
	}

	/// <summary>Per-quad lighting inputs; final vertex colors are recomputed when the sun or the player's light changes.</summary>
	public struct QuadLight
	{
		public Vector3 Center;
		public Vector3 Block;
		public float Sky;
		public float Shade;
		public byte Alpha;
		public Vector3 Tint;
	}

	public sealed class MeshPart
	{
		public Texture2D Tex;
		public bool Translucent;
		public VertexPositionColorTexture[] Verts;
		public QuadLight[] Quads;
		public int QuadCount;
		public DynamicVertexBuffer VB;

		public void Dispose()
		{
			VB?.Dispose();
			VB = null;
		}
	}

	public sealed class ChunkMesh
	{
		public int CX, CY, CZ;
		public BoundingBox Box;
		public readonly List<MeshPart> Parts = new();
		public bool Built, Dirty, Empty;
		public int LastUsed;
		public List<LightSource> Lights;
		public float LitSun = -1f;
		public Vector3 LitPlayer;
		public bool LitWithPlayer;
		public uint Checksum;
		public long ChecksumStamp = -1;

		public ChunkMesh(int cx, int cy, int cz)
		{
			CX = cx; CY = cy; CZ = cz;
			// render space box: y is flipped
			Vector3 a = new(cx * 16, -(cy * 16 + 16), cz * 16);
			Box = new BoundingBox(a - new Vector3(3f, 3f, 3f), a + new Vector3(19f, 19f, 19f));
		}

		public Vector3 CenterTile => new(CX * 16 + 8, CY * 16 + 8, CZ * 16 + 8);

		public void DisposeParts()
		{
			foreach (var p in Parts)
				p.Dispose();
			Parts.Clear();
		}
	}

	/// <summary>Builds the geometry of one 16^3 chunk. Main thread only.</summary>
	public static class ChunkBuilder
	{
		private const int P = 18; // padded size
		private static readonly VTile[] buf = new VTile[P * P * P];
		private static readonly Dictionary<(Texture2D, bool), PartBuilder> builders = new();
		private static List<LightSource> lights;

		private sealed class PartBuilder
		{
			public Texture2D Tex;
			public bool Translucent;
			public readonly List<VertexPositionColorTexture> Verts = new();
			public readonly List<QuadLight> Quads = new();
		}

		private static int Idx(int lx, int ly, int lz) => ((lz + 1) * P + (lx + 1)) * P + (ly + 1);

		private static readonly int[] NX = { -1, 1, 0, 0, 0, 0 };
		private static readonly int[] NY = { 0, 0, -1, 1, 0, 0 };
		private static readonly int[] NZ = { 0, 0, 0, 0, -1, 1 };
		private static readonly float[] Shade = { 0.8f, 0.8f, 1f, 0.55f, 0.7f, 0.7f };

		/// <summary>Light emitters inside a chunk (no meshing).</summary>
		public static List<LightSource> ScanLights(int cx, int cy, int cz)
		{
			Voxel.FillBlock(cx * 16 - 1, cy * 16 - 1, cz * 16 - 1, P, P, P, buf);
			return CollectLights(cx, cy, cz);
		}

		private static List<LightSource> CollectLights(int cx, int cy, int cz)
		{
			var list = new List<LightSource>();
			for (int lz = 0; lz < 16; lz++)
				for (int lx = 0; lx < 16; lx++)
					for (int ly = 0; ly < 16; ly++) {
						ref VTile v = ref buf[Idx(lx, ly, lz)];
						int x = cx * 16 + lx, y = cy * 16 + ly, z = cz * 16 + lz;
						if (v.HasTile && v.TileType < TileLoader.TileCount && Main.tileLighted[v.TileType]) {
							list.Add(LightFor(v, x, y, z));
						}
						else if (v.LiquidAmount > 0 && v.LiquidType == LiquidID.Lava && ((x + z + y) & 3) == 0) {
							list.Add(new LightSource { Pos = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), Color = new Vector3(1f, 0.45f, 0.15f), Radius = 7f });
						}
						if (list.Count >= 64)
							return list;
					}
			return list;
		}

		private static LightSource LightFor(in VTile v, int x, int y, int z)
		{
			var ls = new LightSource { Pos = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), Color = new Vector3(0.9f, 0.8f, 0.6f), Radius = 9f };
			ushort t = v.TileType;
			if (TileID.Sets.Torch[t] || t == TileID.Torches) {
				ls.Color = new Vector3(1f, 0.86f, 0.6f);
				ls.Radius = 12f;
				int style = v.State.TileFrameY / 22;
				switch (style) {
					case 1: ls.Color = new Vector3(0.2f, 0.4f, 1f); break;   // blue
					case 2: ls.Color = new Vector3(1f, 0.2f, 0.2f); break;   // red
					case 3: ls.Color = new Vector3(0.3f, 1f, 0.3f); break;   // green
					case 4: ls.Color = new Vector3(0.8f, 0.3f, 1f); break;   // purple
					case 5: ls.Color = new Vector3(1.1f, 1.1f, 1.1f); break; // white
					case 6: ls.Color = new Vector3(1f, 0.9f, 0.2f); break;   // yellow
					case 8: ls.Color = new Vector3(0.2f, 0.4f, 1f); break;   // ice
				}
			}
			else if (t == TileID.MushroomGrass || t == TileID.MushroomPlants || t == TileID.MushroomTrees) {
				ls.Color = new Vector3(0.15f, 0.3f, 0.9f);
				ls.Radius = 5f;
			}
			else if (Main.tileSolid[t] && !Main.tileFrameImportant[t]) {
				ls.Radius = 5f;
				ls.Color = new Vector3(0.7f, 0.5f, 0.3f);
			}
			return ls;
		}

		public delegate List<LightSource> LightLookup(int cx, int cy, int cz, ref bool scanned);

		public static void Build(ChunkMesh mesh, LightLookup neighborLights, out bool lightsChanged)
		{
			int cx = mesh.CX, cy = mesh.CY, cz = mesh.CZ;
			int x0 = cx * 16, y0 = cy * 16, z0 = cz * 16;
			Voxel.FillBlock(x0 - 1, y0 - 1, z0 - 1, P, P, P, buf);
			var own = CollectLights(cx, cy, cz);
			lightsChanged = !SameLights(mesh.Lights, own);
			mesh.Lights = own;

			bool any = false;
			for (int i = 0; i < 16 * 16 * 16 && !any; i++) {
				int lz = i >> 8, lx = (i >> 4) & 15, ly = i & 15;
				ref VTile v = ref buf[Idx(lx, ly, lz)];
				if (v.HasTile || v.LiquidAmount > 0)
					any = true;
			}
			mesh.DisposeParts();
			mesh.Empty = !any;
			mesh.Built = true;
			mesh.Dirty = false;
			mesh.LitSun = -1f;
			if (!any)
				return;

			// neighbour light lists may need a scan, which reuses the shared buffer: refill afterwards
			var gathered = new List<LightSource>(own);
			bool scanned = false;
			for (int dz = -1; dz <= 1; dz++)
				for (int dx = -1; dx <= 1; dx++)
					for (int dy = -1; dy <= 1; dy++)
						if (dx != 0 || dy != 0 || dz != 0) {
							var l = neighborLights(cx + dx, cy + dy, cz + dz, ref scanned);
							if (l != null)
								gathered.AddRange(l);
						}
			if (scanned)
				Voxel.FillBlock(x0 - 1, y0 - 1, z0 - 1, P, P, P, buf);
			lights = gathered;

			foreach (var b in builders.Values) {
				b.Verts.Clear();
				b.Quads.Clear();
			}

			for (int lz = 0; lz < 16; lz++)
				for (int lx = 0; lx < 16; lx++)
					for (int ly = 0; ly < 16; ly++) {
						int idx = Idx(lx, ly, lz);
						ref VTile v = ref buf[idx];
						int x = x0 + lx, y = y0 + ly, z = z0 + lz;
						if (x >= Voxel.W || y >= Voxel.H || z >= Voxel.D)
							continue;
						// walls (background) are a 2D device and are not drawn in 3D; vanilla still uses them (housing, spawns)
						if (v.HasTile && v.TileType < TileLoader.TileCount)
							EmitTile(ref v, lx, ly, lz, x, y, z);
						if (v.LiquidAmount > 0 && !v.IsOpaqueCube)
							EmitLiquid(ref v, lx, ly, lz, x, y, z);
					}

			foreach (var b in builders.Values) {
				if (b.Quads.Count == 0)
					continue;
				var part = new MeshPart {
					Tex = b.Tex,
					Translucent = b.Translucent,
					Verts = b.Verts.ToArray(),
					Quads = b.Quads.ToArray(),
					QuadCount = b.Quads.Count
				};
				mesh.Parts.Add(part);
			}
			lights = null;
		}

		private static bool SameLights(List<LightSource> a, List<LightSource> b)
		{
			if (a == null)
				return b.Count == 0;
			if (a.Count != b.Count)
				return false;
			for (int i = 0; i < a.Count; i++)
				if (a[i].Pos != b[i].Pos || a[i].Color != b[i].Color)
					return false;
			return true;
		}

		// ------------------------------------------------------------------ emitters

		private static PartBuilder Part(Texture2D tex, bool translucent = false)
		{
			if (!builders.TryGetValue((tex, translucent), out var b))
				builders[(tex, translucent)] = b = new PartBuilder { Tex = tex, Translucent = translucent };
			return b;
		}

		/// <summary>
		/// Quads are grown by a hair so neighbouring faces overlap: without it the rasterizer leaves pinholes along shared
		/// edges (T-junctions between split faces), through which the sky shows as bright dots.
		/// </summary>
		private const float Seal = 0.0035f;

		private static void Quad(PartBuilder b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector4 uv, QuadLight ql)
		{
			Vector3 c = (p0 + p1 + p2 + p3) * 0.25f;
			p0 += Grow(p0 - c);
			p1 += Grow(p1 - c);
			p2 += Grow(p2 - c);
			p3 += Grow(p3 - c);
			b.Verts.Add(new VertexPositionColorTexture(Camera3D.ToRender(p0), Color.White, new Vector2(uv.X, uv.Y)));
			b.Verts.Add(new VertexPositionColorTexture(Camera3D.ToRender(p1), Color.White, new Vector2(uv.Z, uv.Y)));
			b.Verts.Add(new VertexPositionColorTexture(Camera3D.ToRender(p2), Color.White, new Vector2(uv.Z, uv.W)));
			b.Verts.Add(new VertexPositionColorTexture(Camera3D.ToRender(p3), Color.White, new Vector2(uv.X, uv.W)));
			b.Quads.Add(ql);
		}

		private static Vector3 Grow(Vector3 d)
		{
			float l = d.Length();
			return l > 1e-5f ? d * (Seal / l) : Vector3.Zero;
		}

		private static Vector4 UV(Texture2D tex, int fx, int fy, int fw = 16, int fh = 16)
		{
			fx = Math.Clamp(fx, 0, Math.Max(0, tex.Width - fw));
			fy = Math.Clamp(fy, 0, Math.Max(0, tex.Height - fh));
			float iw = 1f / tex.Width, ih = 1f / tex.Height;
			// stay a fraction of a texel inside the frame so edge pixels never sample the transparent gutter
			const float inset = 0.02f;
			return new Vector4((fx + inset) * iw, (fy + inset) * ih, (fx + fw - inset) * iw, (fy + fh - inset) * ih);
		}

		/// <summary>
		/// Sky light of an air cell: 1 under open sky. Light spreads sideways from open columns nearby, so cliff faces
		/// and ground next to overhangs stay lit and only deep overhangs and caves get dark.
		/// </summary>
		public static float SkyLight(int x, int y, int z) => SkyAt(x, y, z);

		private static float SkyAt(int x, int y, int z)
		{
			float best = 0f;
			for (int dz = -3; dz <= 3; dz++)
				for (int dx = -3; dx <= 3; dx++) {
					float dist = MathF.Sqrt(dx * dx + dz * dz);
					if (dist > 3.2f)
						continue;
					float reach = 1f - dist * 0.14f;
					if (reach <= best)
						continue;
					int top = Voxel.ColumnTop(x + dx, z + dz);
					float open = y < top ? 1f : Math.Max(0f, 1f - (y - top + 1) * 0.3f);
					best = Math.Max(best, open * reach);
					if (best >= 1f)
						return 1f;
				}
			return best;
		}

		private static Vector3 BlockLightAt(Vector3 p)
		{
			Vector3 c = Vector3.Zero;
			foreach (var l in lights) {
				float d = Vector3.Distance(p, l.Pos);
				if (d >= l.Radius)
					continue;
				if (!Raycast.LineClear(l.Pos, p))
					continue;
				float k = 1f - d / l.Radius;
				c += l.Color * (k * k);
			}
			return Vector3.Min(c, new Vector3(1.4f));
		}

		private static QuadLight LightFace(int x, int y, int z, int face, float shade, byte alpha = 255)
		{
			int lx = x + (face >= 0 ? NX[face] : 0), ly = y + (face >= 0 ? NY[face] : 0), lz = z + (face >= 0 ? NZ[face] : 0);
			Vector3 c = new(lx + 0.5f, ly + 0.5f, lz + 0.5f);
			Vector3 block = BlockLightAt(c);
			if (ly >= HellRow)
				block = Vector3.Max(block, HellLight);
			return new QuadLight { Center = c, Sky = SkyAt(lx, ly, lz), Block = block, Shade = shade, Alpha = alpha, Tint = Vector3.One };
		}

		/// <summary>Vanilla lights every open cell of the underworld (TileLightScanner.ApplyHellLight) with this colour.</summary>
		public static readonly Vector3 HellLight = new(0.55f, 0.55f * 0.6f, 0.55f * 0.2f);
		public static int HellRow => Main.maxTilesY - 200;

		private static int Hash(int x, int y, int z) => (int)((uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791) % 3);

		private static void EmitTile(ref VTile v, int lx, int ly, int lz, int x, int y, int z)
		{
			ushort type = v.TileType;
			Texture2D tex = TileTextures.Tile(type);
			if (tex == null)
				return;

			if (v.IsCube) {
				float h0 = v.State.IsHalfBlock ? 0.5f : 0f;
				EmitBox(ref v, tex, lx, ly, lz, x, y, z, 0f, 1f, h0, 1f, 0f, 1f, true);
				return;
			}
			if (TileID.Sets.Platforms[type]) {
				EmitBox(ref v, tex, lx, ly, lz, x, y, z, 0f, 1f, 0f, 0.25f, 0f, 1f, false, v.State.TileFrameX, v.State.TileFrameY);
				return;
			}
			bool tree = type == TileID.Trees || type == TileID.PalmTree || type >= TileID.TreeTopaz && type <= TileID.TreeAmber
				|| type == TileID.TreeAsh || type == TileID.VanityTreeSakura || type == TileID.VanityTreeYellowWillow || type == TileID.MushroomTrees;
			if (tree) {
				EmitTree(ref v, tex, lx, ly, lz, x, y, z);
				return;
			}
			if (type == TileID.Cactus) {
				EmitBox(ref v, tex, lx, ly, lz, x, y, z, 0.12f, 0.88f, 0f, 1f, 0.12f, 0.88f, false, v.State.TileFrameX, v.State.TileFrameY);
				return;
			}
			if (Main.tileSolid[type] && !Main.tileSolidTop[type] && !Main.tileFrameImportant[type]) {
				EmitBox(ref v, tex, lx, ly, lz, x, y, z, 0f, 1f, 0f, 1f, 0f, 1f, true);
				return;
			}
			// sprite-like tiles: plants become crossed quads, everything else a flat cut-out in the cell
			Vector4 uv = UV(tex, v.State.TileFrameX, v.State.TileFrameY);
			var b = Part(tex);
			QuadLight ql = LightFace(x, y, z, -1, 0.95f);
			if (Main.tileCut[type] || Main.tileRope[type] || type == TileID.Vines || TileID.Sets.IsVine[type]) {
				Cross(b, x + 0.5f, y, y + 1f, z + 0.5f, 0.5f, uv, ql);
			}
			else {
				float zp = z + 0.5f;
				Quad(b, new Vector3(x, y, zp), new Vector3(x + 1, y, zp), new Vector3(x + 1, y + 1, zp), new Vector3(x, y + 1, zp), uv, ql);
			}
		}

		private static void Cross(PartBuilder b, float cx, float yTop, float yBottom, float cz, float half, Vector4 uv, QuadLight ql)
		{
			const float k = 0.7071f;
			float d = half * k;
			Quad(b, new Vector3(cx - d, yTop, cz - d), new Vector3(cx + d, yTop, cz + d), new Vector3(cx + d, yBottom, cz + d), new Vector3(cx - d, yBottom, cz - d), uv, ql);
			Quad(b, new Vector3(cx - d, yTop, cz + d), new Vector3(cx + d, yTop, cz - d), new Vector3(cx + d, yBottom, cz - d), new Vector3(cx - d, yBottom, cz + d), uv, ql);
		}

		private static bool HidesFace(in VTile self, in VTile n, int face)
		{
			if (!n.IsOpaqueCube)
				return n.IsCube && self.IsCube && n.TileType == self.TileType && !n.State.IsHalfBlock && !self.State.IsHalfBlock;
			if (face == 2 && self.State.IsHalfBlock)
				return false;
			return true;
		}

		/// <summary>
		/// Vanilla block frames (WorldGen.TileFrame) indexed by connected neighbours: bit 3 up, 2 down, 1 left, 0 right.
		/// Each entry has the three random variants.
		/// </summary>
		private static readonly (int X, int Y)[][] BlockFrames = BuildFrameTable();

		private static (int, int)[][] BuildFrameTable()
		{
			var t = new (int, int)[16][];
			void Set(string udlr, (int, int) a, (int, int) b, (int, int) c) => t[Convert.ToInt32(udlr, 2)] = new[] { a, b, c };
			Set("1111", (18, 18), (36, 18), (54, 18));
			Set("0111", (18, 0), (36, 0), (54, 0));
			Set("1011", (18, 36), (36, 36), (54, 36));
			Set("1101", (0, 0), (0, 18), (0, 36));
			Set("1110", (72, 0), (72, 18), (72, 36));
			Set("0101", (0, 54), (36, 54), (72, 54));
			Set("0110", (18, 54), (54, 54), (90, 54));
			Set("1001", (0, 72), (36, 72), (72, 72));
			Set("1010", (18, 72), (54, 72), (90, 72));
			Set("1100", (90, 0), (90, 18), (90, 36));
			Set("0011", (108, 72), (126, 72), (144, 72));
			Set("0100", (108, 0), (126, 0), (144, 0));
			Set("1000", (108, 54), (126, 54), (144, 54));
			Set("0001", (162, 0), (162, 18), (162, 36));
			Set("0010", (216, 0), (216, 18), (216, 36));
			Set("0000", (162, 54), (180, 54), (198, 54));
			return t;
		}

		/// <summary>
		/// Strip along one face axis: source pixels [S0,S1) drawn at [D0,D1) (sixteenths of a tile).
		/// TouchA/TouchB tell whether the strip belongs to the start/end edge: only those edges pick the frame for it,
		/// so the middle of the block comes from the inner frame and no edge row is drawn twice.
		/// </summary>
		private readonly struct Strip
		{
			public readonly int S0, S1, D0, D1;
			public readonly bool TouchA, TouchB;
			public Strip(int s0, int s1, int d0, int d1, bool a, bool b) { S0 = s0; S1 = s1; D0 = d0; D1 = d1; TouchA = a; TouchB = b; }
		}

		private const int Edge = 4; // depth of the ragged sprite edge, in texels

		/// <summary>
		/// Lays a frame out along one axis so its ragged edge bands sit outside the block on open sides and the block
		/// itself is covered at 1:1 scale: rows next to an open edge come from the edge frame, the rest from the inner one.
		/// </summary>
		private static int Strips(bool openA, bool openB, Span<Strip> o)
		{
			if (!openA && !openB) {
				o[0] = new Strip(0, 16, 0, 16, true, true);
				return 1;
			}
			if (openA && !openB) {
				o[0] = new Strip(0, Edge, -Edge, 0, true, false);
				o[1] = new Strip(Edge, 16, 0, 16 - Edge, true, false);
				o[2] = new Strip(16 - Edge, 16, 16 - Edge, 16, false, true);
				return 3;
			}
			if (!openA) {
				o[0] = new Strip(0, Edge, 0, Edge, true, false);
				o[1] = new Strip(0, 16 - Edge, Edge, 16, false, true);
				o[2] = new Strip(16 - Edge, 16, 16, 16 + Edge, false, true);
				return 3;
			}
			o[0] = new Strip(0, Edge, -Edge, 0, true, false);
			o[1] = new Strip(Edge, 2 * Edge, 0, Edge, true, false);
			o[2] = new Strip(Edge, 16 - Edge, Edge, 16 - Edge, false, false);
			o[3] = new Strip(16 - 2 * Edge, 16 - Edge, 16 - Edge, 16, false, true);
			o[4] = new Strip(16 - Edge, 16, 16, 16 + Edge, false, true);
			return 5;
		}

		/// <summary>Box in cell-local coordinates (y down). Cubes pick block frames, others use a fixed frame.</summary>
		private static void EmitBox(ref VTile v, Texture2D tex, int lx, int ly, int lz, int x, int y, int z,
			float ax, float bx, float ay, float by, float az, float bz, bool cube, int fixedFx = -1, int fixedFy = -1)
		{
			int variant = Hash(x, y, z);
			bool grass = cube && TileTextures.IsGrass(v.TileType);
			bool topOpen = !buf[Idx(lx, ly - 1, lz)].IsCube;
			bool full = cube && ay == 0f && by == 1f;
			Span<Vector3> q = stackalloc Vector3[4];
			for (int f = 0; f < 6; f++) {
				ref VTile n = ref buf[Idx(lx + NX[f], ly + NY[f], lz + NZ[f])];
				if (cube && HidesFace(v, n, f))
					continue;
				float X0 = x + ax, X1 = x + bx, Y0 = y + ay, Y1 = y + by, Z0 = z + az, Z1 = z + bz;
				// corners: top-left, top-right, bottom-right, bottom-left (as seen from outside)
				switch (f) {
					case 0: q[0] = new(X0, Y0, Z0); q[1] = new(X0, Y0, Z1); q[2] = new(X0, Y1, Z1); q[3] = new(X0, Y1, Z0); break;
					case 1: q[0] = new(X1, Y0, Z1); q[1] = new(X1, Y0, Z0); q[2] = new(X1, Y1, Z0); q[3] = new(X1, Y1, Z1); break;
					case 2: q[0] = new(X0, Y0, Z0); q[1] = new(X1, Y0, Z0); q[2] = new(X1, Y0, Z1); q[3] = new(X0, Y0, Z1); break;
					case 3: q[0] = new(X0, Y1, Z1); q[1] = new(X1, Y1, Z1); q[2] = new(X1, Y1, Z0); q[3] = new(X0, Y1, Z0); break;
					case 4: q[0] = new(X1, Y0, Z0); q[1] = new(X0, Y0, Z0); q[2] = new(X0, Y1, Z0); q[3] = new(X1, Y1, Z0); break;
					default: q[0] = new(X0, Y0, Z1); q[1] = new(X1, Y0, Z1); q[2] = new(X1, Y1, Z1); q[3] = new(X0, Y1, Z1); break;
				}
				QuadLight ql = LightFace(x, y, z, f, Shade[f]);
				if (full && f != 2 && f != 3) {
					EmitSideFace(tex, q, f, lx, ly, lz, variant, ql);
					continue;
				}
				Texture2D t = tex;
				Vector4 uv;
				if (!cube)
					uv = UV(tex, fixedFx, fixedFy);
				else if (f == 2 && grass && topOpen) {
					t = TileTextures.GrassTop(v.TileType);
					uv = new Vector4(0, 0, 1, 1);
				}
				else
					uv = UV(tex, 18 + 18 * variant, 18);
				Quad(Part(t), q[0], q[1], q[2], q[3], uv, ql);
			}
		}

		/// <summary>
		/// Side face of a block with the vanilla frame for its neighbours in the face plane (so grass edges, outlines and
		/// corners look like the original sprite). The ragged edge pixels stick out of the block on open sides.
		/// </summary>
		private static void EmitSideFace(Texture2D tex, Span<Vector3> q, int f, int lx, int ly, int lz, int variant, QuadLight ql)
		{
			Vector3 right = q[1] - q[0];
			Vector3 down = q[3] - q[0];
			int rx = (int)Math.Round(right.X), rz = (int)Math.Round(right.Z);
			bool up = buf[Idx(lx, ly - 1, lz)].IsCube;
			bool dn = buf[Idx(lx, ly + 1, lz)].IsCube;
			bool lf = buf[Idx(lx - rx, ly, lz - rz)].IsCube;
			bool rt = buf[Idx(lx + rx, ly, lz + rz)].IsCube;

			Span<Strip> hs = stackalloc Strip[5];
			Span<Strip> vs = stackalloc Strip[5];
			int nh = Strips(!lf, !rt, hs);
			int nv = Strips(!up, !dn, vs);
			var b = Part(tex);
			Vector3 lift = new Vector3(NX[f], NY[f], NZ[f]) * 0.003f;
			const float k = 1f / 16f;
			for (int iv = 0; iv < nv; iv++) {
				Strip sv = vs[iv];
				for (int ih = 0; ih < nh; ih++) {
					Strip sh = hs[ih];
					// frame for this piece: an edge counts as open only if the piece lies along that edge
					bool u = up || !sv.TouchA, d = dn || !sv.TouchB, l = lf || !sh.TouchA, r = rt || !sh.TouchB;
					int key = (u ? 8 : 0) | (d ? 4 : 0) | (l ? 2 : 0) | (r ? 1 : 0);
					var (fx, fy) = BlockFrames[key][variant];
					bool outside = sv.D0 < 0 || sv.D1 > 16 || sh.D0 < 0 || sh.D1 > 16;
					Vector3 o = q[0] + (outside ? lift : Vector3.Zero);
					Vector3 a = o + right * (sh.D0 * k) + down * (sv.D0 * k);
					Vector3 c = o + right * (sh.D1 * k) + down * (sv.D0 * k);
					Vector3 dd = o + right * (sh.D1 * k) + down * (sv.D1 * k);
					Vector3 e = o + right * (sh.D0 * k) + down * (sv.D1 * k);
					Quad(b, a, c, dd, e, UV(tex, fx + sh.S0, fy + sv.S0, sh.S1 - sh.S0, sv.S1 - sv.S0), ql);
				}
			}
		}

		private static void EmitTree(ref VTile v, Texture2D tex, int lx, int ly, int lz, int x, int y, int z)
		{
			int fx = v.State.TileFrameX, fy = v.State.TileFrameY;
			bool top = fy >= 198 && fx >= 22;
			if (!top) {
				EmitBox(ref v, tex, lx, ly, lz, x, y, z, 0.18f, 0.82f, 0f, 1f, 0.18f, 0.82f, false, fx, fy);
				return;
			}
			int treeFrame = fy == 220 ? 1 : fy == 242 ? 2 : 0;
			int style = 0, w = 80, h = 80;
			if (v.TileType == TileID.Trees) {
				// vanilla foliage lookup only where Main.tile really holds this slice
				bool inMainTile = z == Voxel.WinZ && Voxel.InWindow(x, y) || z == Voxel.ZC && !Voxel.InWindow(x, y);
				bool found = false;
				if (inMainTile) {
					try {
						int tf = treeFrame;
						if (WorldGen.InWorld(x, y, 2) && WorldGen.GetCommonTreeFoliageData(x, y, 0, ref tf, ref style, out _, out w, out h)) {
							treeFrame = tf;
							found = true;
						}
					}
					catch {
						style = 0; w = 80; h = 80;
					}
				}
				if (!found) {
					w = 80; h = 80;
					style = TreeStyleFromGround(x, y, z);
				}
			}
			QuadLight ql = LightFace(x, y, z, -1, 1f);
			if (fx == 22) {
				Texture2D tt = TileTextures.TreeTop(style);
				Vector4 uv = UV(tt, treeFrame * (w + 2), 0, w, h);
				var b = Part(tt);
				float half = w / 32f;
				float hh = h / 16f;
				Cross(b, x + 0.5f, y + 1f - hh, y + 1f, z + 0.5f, half * 1.25f, uv, ql);
				// a flat layer facing the default camera so the canopy reads as a volume
				float zp = z + 0.5f;
				Quad(b, new Vector3(x + 0.5f - half, y + 1f - hh, zp), new Vector3(x + 0.5f + half, y + 1f - hh, zp),
					new Vector3(x + 0.5f + half, y + 1f, zp), new Vector3(x + 0.5f - half, y + 1f, zp), uv, ql);
			}
			else {
				Texture2D bt = TileTextures.TreeBranch(style);
				bool left = fx == 44;
				Vector4 uv = UV(bt, left ? 0 : 42, treeFrame * 42, 40, 40);
				var b = Part(bt);
				float cx = left ? x - 0.75f : x + 1.75f;
				Cross(b, cx, y - 0.75f, y + 1.75f, z + 0.5f, 1.25f, uv, ql);
			}
		}

		private static int TreeStyleFromGround(int x, int y, int z)
		{
			for (int k = 1; k < 40; k++) {
				VTile g = Voxel.Get(x, y + k, z);
				if (!g.HasTile || g.TileType == TileID.Trees)
					continue;
				return g.TileType switch {
					TileID.CorruptGrass => 1,
					TileID.JungleGrass => 2,
					TileID.HallowedGrass => 3,
					TileID.SnowBlock => 4,
					TileID.CrimsonGrass => 5,
					TileID.MushroomGrass => 14,
					_ => 0
				};
			}
			return 0;
		}



		/// <summary>
		/// Liquid faces use vanilla's animated liquid texture (see <see cref="LiquidTextures"/>): the side of the top cell shows
		/// the surface frame cropped to the liquid level like vanilla crops it, every other face the inside of the liquid.
		/// </summary>
		private static void EmitLiquid(ref VTile v, int lx, int ly, int lz, int x, int y, int z)
		{
			int lt = v.LiquidType;
			Texture2D tex = LiquidTextures.Get(lt) ?? TileTextures.White;
			bool animated = tex != TileTextures.White;
			byte alpha = (byte)(LiquidTextures.Opacity(lt) * 255f);
			ref VTile up = ref buf[Idx(lx, ly - 1, lz)];
			bool fullAbove = up.LiquidAmount > 0 && up.LiquidType == lt;
			float h = fullAbove ? 1f : Math.Max(0.06f, v.LiquidAmount / 255f);
			float Y0 = y + 1f - h, Y1 = y + 1f;
			var b = Part(tex, true);
			float iw = 1f / LiquidTextures.FrameW, ih = 1f / LiquidTextures.FrameH;
			const float inset = 0.02f;
			Vector4 inside = animated ? new Vector4((16 + inset) * iw, (48 + inset) * ih, (32 - inset) * iw, (64 - inset) * ih) : new Vector4(0, 0, 1, 1);
			Vector4 surface = animated && !fullAbove ? new Vector4((16 + inset) * iw, inset * ih, (32 - inset) * iw, (h * 16f - inset) * ih) : inside;
			for (int f = 0; f < 6; f++) {
				ref VTile n = ref buf[Idx(lx + NX[f], ly + NY[f], lz + NZ[f])];
				if (n.IsOpaqueCube)
					continue;
				if (f != 2 && n.LiquidAmount > 0 && n.LiquidType == lt)
					continue;
				if (f == 2 && fullAbove)
					continue;
				QuadLight ql = LightFace(x, y, z, f == 2 ? -1 : f, Shade[f], alpha);
				// lava glows: vanilla lights it by itself, so it shows its own colours even in the dark
				if (lt == LiquidID.Lava) {
					ql.Block = Vector3.One;
					ql.Shade = 1f;
				}
				ql.Tint = Vector3.One;
				Vector4 uv = f == 2 || f == 3 ? inside : surface;
				float X0 = x, X1 = x + 1, Z0 = z, Z1 = z + 1;
				switch (f) {
					case 0: Quad(b, new(X0, Y0, Z0), new(X0, Y0, Z1), new(X0, Y1, Z1), new(X0, Y1, Z0), uv, ql); break;
					case 1: Quad(b, new(X1, Y0, Z1), new(X1, Y0, Z0), new(X1, Y1, Z0), new(X1, Y1, Z1), uv, ql); break;
					case 2: Quad(b, new(X0, Y0, Z0), new(X1, Y0, Z0), new(X1, Y0, Z1), new(X0, Y0, Z1), uv, ql); break;
					case 3: Quad(b, new(X0, Y1, Z1), new(X1, Y1, Z1), new(X1, Y1, Z0), new(X0, Y1, Z0), uv, ql); break;
					case 4: Quad(b, new(X1, Y0, Z0), new(X0, Y0, Z0), new(X0, Y1, Z0), new(X1, Y1, Z0), uv, ql); break;
					case 5: Quad(b, new(X0, Y0, Z1), new(X1, Y0, Z1), new(X1, Y1, Z1), new(X0, Y1, Z1), uv, ql); break;
				}
			}
		}

		public static void Clear() => builders.Clear();
	}
}
