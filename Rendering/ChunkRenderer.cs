using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TerraDepth.Common;
using TerraDepth.World;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraDepth.Rendering
{
	/// <summary>Chunk cache, build queue, relighting and drawing.</summary>
	public static class ChunkRenderer
	{
		private static readonly Dictionary<int, ChunkMesh> meshes = new();
		private static readonly Dictionary<int, List<LightSource>> lightCache = new();
		private static readonly List<ChunkMesh> visible = new();
		private static readonly List<(float d, int cx, int cy, int cz)> buildQueue = new();
		private static readonly List<(float d, ChunkMesh m)> relightQueue = new();
		private static IndexBuffer quadIndices;
		private const int MaxQuads = 16384;
		private static int frame;
		private static int checksumCursor;
		private static bool subscribed;

		public static Vector3 Sun = Vector3.One;
		private static float sunKey = -1f;
		public static Vector3 PlayerLightPos;
		public static Vector3 PlayerLightColor;
		public static float PlayerLightRadius;
		private static Vector3 lastPlayerLightPos;
		private static Vector3 lastPlayerLightColor;

		public static int BuiltCount => meshes.Count;
		public static int VisibleCount => visible.Count;

		private static void Subscribe()
		{
			if (subscribed)
				return;
			Voxel.ChunkChanged += OnChunkChanged;
			subscribed = true;
		}

		public static void Unload()
		{
			if (subscribed)
				Voxel.ChunkChanged -= OnChunkChanged;
			subscribed = false;
			Clear();
			quadIndices?.Dispose();
			quadIndices = null;
			ChunkBuilder.Clear();
		}

		public static void Clear()
		{
			foreach (var m in meshes.Values)
				m.DisposeParts();
			meshes.Clear();
			lightCache.Clear();
			visible.Clear();
		}

		private static void OnChunkChanged(int cx, int cy, int cz)
		{
			if (cx < 0 || cy < 0 || cz < 0 || cx >= Voxel.CW || cy >= Voxel.CH || cz >= Voxel.CD)
				return;
			int key = Voxel.ChunkKey(cx, cy, cz);
			lightCache.Remove(key);
			if (meshes.TryGetValue(key, out var m))
				m.Dirty = true;
		}

		private static void MarkNeighborsDirty(int cx, int cy, int cz)
		{
			for (int dz = -1; dz <= 1; dz++)
				for (int dx = -1; dx <= 1; dx++)
					for (int dy = -1; dy <= 1; dy++) {
						if (dx == 0 && dy == 0 && dz == 0)
							continue;
						int x = cx + dx, y = cy + dy, z = cz + dz;
						if (x < 0 || y < 0 || z < 0 || x >= Voxel.CW || y >= Voxel.CH || z >= Voxel.CD)
							continue;
						if (meshes.TryGetValue(Voxel.ChunkKey(x, y, z), out var m))
							m.Dirty = true;
					}
		}

		private static List<LightSource> GetLights(int cx, int cy, int cz, ref bool scanned)
		{
			if (cx < 0 || cy < 0 || cz < 0 || cx >= Voxel.CW || cy >= Voxel.CH || cz >= Voxel.CD)
				return null;
			int key = Voxel.ChunkKey(cx, cy, cz);
			if (meshes.TryGetValue(key, out var m) && m.Built && !m.Dirty && m.Lights != null)
				return m.Lights;
			if (lightCache.TryGetValue(key, out var l))
				return l;
			l = ChunkBuilder.ScanLights(cx, cy, cz);
			scanned = true;
			lightCache[key] = l;
			return l;
		}

		private static void EnsureIndices(GraphicsDevice gd)
		{
			if (quadIndices != null && !quadIndices.IsDisposed)
				return;
			var idx = new ushort[MaxQuads * 6];
			for (int q = 0; q < MaxQuads; q++) {
				int v = q * 4, i = q * 6;
				idx[i] = (ushort)v;
				idx[i + 1] = (ushort)(v + 1);
				idx[i + 2] = (ushort)(v + 2);
				idx[i + 3] = (ushort)v;
				idx[i + 4] = (ushort)(v + 2);
				idx[i + 5] = (ushort)(v + 3);
			}
			quadIndices = new IndexBuffer(gd, IndexElementSize.SixteenBits, idx.Length, BufferUsage.WriteOnly);
			quadIndices.SetData(idx);
		}

		// ------------------------------------------------------------------ lighting

		public static void UpdateLightingInputs(Player p)
		{
			Color tc = Main.tileColor;
			Vector3 sun = tc.ToVector3();
			float key = (float)Math.Round(sun.Length() * 16f) / 16f;
			if (Math.Abs(key - sunKey) > 0.001f) {
				sunKey = key;
				Sun = sun;
			}
			// Only a light the player actually carries (or a light pet); no ambient glow around the player.
			PlayerLightPos = Camera3D.PlayerHead(p);
			Vector3 c = Vector3.Zero;
			float r = 0f;
			Item held = p.HeldItem;
			if (held.type > ItemID.None && !p.dead && (ItemID.Sets.Torches[held.type] || ItemID.Sets.Glowsticks[held.type])) {
				c = new Vector3(1f, 0.85f, 0.6f);
				r = 11f;
			}
			if (!p.dead && LightPet(p, out Vector3 pc)) {
				// lights the player the same way a torch in hand does
				c = Vector3.Max(c, pc);
				r = Math.Max(r, 13f);
			}
			PlayerLightColor = c;
			PlayerLightRadius = r;
		}

		private static bool LightPet(Player p, out Vector3 color)
		{
			foreach (Projectile proj in Main.ActiveProjectiles) {
				if (proj.owner != p.whoAmI || !ProjectileID.Sets.LightPet[proj.type])
					continue;
				color = proj.type switch {
					ProjectileID.Wisp => new Vector3(0.6f, 0.85f, 1f),
					ProjectileID.ShadowOrb => new Vector3(0.7f, 0.45f, 1f),
					ProjectileID.CrimsonHeart => new Vector3(1f, 0.45f, 0.45f),
					ProjectileID.MagicLantern => new Vector3(1f, 0.9f, 0.65f),
					ProjectileID.SuspiciousTentacle => new Vector3(0.7f, 0.9f, 0.8f),
					ProjectileID.BlueFairy => new Vector3(0.5f, 0.7f, 1f),
					ProjectileID.PinkFairy => new Vector3(1f, 0.6f, 0.9f),
					ProjectileID.GreenFairy => new Vector3(0.6f, 1f, 0.6f),
					ProjectileID.DD2PetGhost => new Vector3(0.6f, 0.8f, 1f),
					ProjectileID.FairyQueenPet => new Vector3(1f, 0.8f, 1f),
					_ => new Vector3(1f, 0.95f, 0.85f)
				};
				return true;
			}
			color = Vector3.Zero;
			return false;
		}

		private static void Relight(ChunkMesh m, GraphicsDevice gd)
		{
			Vector3 pl = PlayerLightPos;
			bool usePlayer = PlayerLightRadius > 0f && Vector3.Distance(m.CenterTile, pl) < PlayerLightRadius + 16f;
			foreach (var part in m.Parts) {
				var verts = part.Verts;
				for (int q = 0; q < part.QuadCount; q++) {
					ref QuadLight ql = ref part.Quads[q];
					Vector3 light = Vector3.Max(ql.Block, Sun * ql.Sky);
					if (usePlayer) {
						float d = Vector3.Distance(ql.Center, pl);
						if (d < PlayerLightRadius && Raycast.LineClear(pl, ql.Center)) {
							float k = 1f - d / PlayerLightRadius;
							light += PlayerLightColor * (k * k);
						}
					}
					light = Vector3.Max(light, new Vector3(0.025f));
					Vector3 c = Vector3.Min(light * ql.Shade, Vector3.One) * ql.Tint;
					float a = ql.Alpha / 255f;
					// translucent parts are drawn premultiplied, like vanilla's textures are
					Color col = part.Translucent ? new Color(c.X * a, c.Y * a, c.Z * a, a) : new Color(c.X, c.Y, c.Z, a);
					int v = q * 4;
					verts[v].Color = col;
					verts[v + 1].Color = col;
					verts[v + 2].Color = col;
					verts[v + 3].Color = col;
				}
				if (part.VB == null || part.VB.IsDisposed || part.VB.VertexCount < verts.Length) {
					part.VB?.Dispose();
					part.VB = new DynamicVertexBuffer(gd, VertexPositionColorTexture.VertexDeclaration, verts.Length, BufferUsage.WriteOnly);
				}
				part.VB.SetData(verts, 0, part.QuadCount * 4, SetDataOptions.Discard);
			}
			m.LitSun = sunKey;
			m.LitPlayer = pl;
			m.LitWithPlayer = usePlayer;
		}

		// ------------------------------------------------------------------ live slice change detection

		private static uint SliceChecksum(int cx, int cy)
		{
			uint h = 2166136261;
			int z = Voxel.WinZ;
			for (int x = cx * 16; x < cx * 16 + 16; x++)
				for (int y = cy * 16; y < cy * 16 + 16; y++) {
					if (!Voxel.InWindow(x, y))
						continue;
					Tile t = Main.tile[x, y];
					uint v = (uint)(t.HasTile ? t.TileType + 1 : 0);
					v = v * 31 + (uint)t.TileFrameX;
					v = v * 31 + (uint)t.TileFrameY;
					v = v * 31 + t.WallType;
					v = v * 31 + (uint)(t.LiquidAmount >> 4) + (uint)t.LiquidType * 17;
					v = v * 31 + (uint)t.Slope + (t.IsHalfBlock ? 7u : 0u) + (t.IsActuated ? 13u : 0u);
					h = (h ^ v) * 16777619;
				}
			return h;
		}

		private static long WindowStamp => ((long)Voxel.WinZ << 40) ^ ((long)Voxel.WinX << 20) ^ Voxel.WinY;

		private static void CheckLiveSlice(Vector3 camTile)
		{
			if (!Voxel.WindowOpen)
				return;
			int cz = Voxel.WinZ >> 4;
			long stamp = WindowStamp;
			int r = 5;
			int pcx = (int)camTile.X >> 4, pcy = (int)camTile.Y >> 4;
			int side = r * 2 + 1;
			int total = side * side;
			int perFrame = 24;
			for (int i = 0; i < perFrame; i++) {
				int n = checksumCursor++ % total;
				int cx = pcx - r + n % side, cy = pcy - r + n / side;
				if (cx < 0 || cy < 0 || cx >= Voxel.CW || cy >= Voxel.CH)
					continue;
				if (!meshes.TryGetValue(Voxel.ChunkKey(cx, cy, cz), out var m) || !m.Built)
					continue;
				uint sum = SliceChecksum(cx, cy);
				if (m.ChecksumStamp != stamp) {
					m.ChecksumStamp = stamp;
					m.Checksum = sum;
					continue;
				}
				if (sum != m.Checksum) {
					m.Checksum = sum;
					m.Dirty = true;
					lightCache.Remove(Voxel.ChunkKey(cx, cy, cz));
					Voxel.InvalidateColumns(cx * 16 - 1, cx * 16 + 16, Voxel.WinZ);
					// neighbours along the slice share faces with this chunk
					MarkSliceNeighbors(cx, cy, cz);
				}
			}
		}

		private static void MarkSliceNeighbors(int cx, int cy, int cz)
		{
			int lz = Voxel.WinZ & 15;
			for (int dx = -1; dx <= 1; dx++)
				for (int dy = -1; dy <= 1; dy++)
					for (int dz = (lz == 0 ? -1 : 0); dz <= (lz == 15 ? 1 : 0); dz++) {
						if (dx == 0 && dy == 0 && dz == 0)
							continue;
						if (meshes.TryGetValue(Voxel.ChunkKey(cx + dx, cy + dy, cz + dz), out var m))
							m.Dirty = true;
					}
		}

		// ------------------------------------------------------------------ per-frame

		public static void Update(GraphicsDevice gd, Vector3 camTile)
		{
			Subscribe();
			EnsureIndices(gd);
			frame++;
			var cfg = ModContent.GetInstance<DepthClientConfig>();
			int radius = cfg.RenderDistance / 16 + 1;
			int pcx = (int)Math.Floor(camTile.X) >> 4, pcy = (int)Math.Floor(camTile.Y) >> 4, pcz = (int)Math.Floor(camTile.Z) >> 4;
			float maxDist = cfg.RenderDistance + 8f;

			CheckLiveSlice(camTile);

			visible.Clear();
			buildQueue.Clear();
			int ry = Math.Min(radius, 8);
			for (int cz = pcz - radius; cz <= pcz + radius; cz++) {
				if (cz < 0 || cz >= Voxel.CD)
					continue;
				for (int cx = pcx - radius; cx <= pcx + radius; cx++) {
					if (cx < 0 || cx >= Voxel.CW)
						continue;
					for (int cy = pcy - ry; cy <= pcy + ry; cy++) {
						if (cy < 0 || cy >= Voxel.CH)
							continue;
						Vector3 center = new(cx * 16 + 8, cy * 16 + 8, cz * 16 + 8);
						float d = Vector3.Distance(center, camTile);
						if (d > maxDist + 14f)
							continue;
						int key = Voxel.ChunkKey(cx, cy, cz);
						meshes.TryGetValue(key, out var m);
						bool inView;
						if (m != null)
							inView = Camera3D.Frustum.Intersects(m.Box);
						else {
							Vector3 a = new(cx * 16, -(cy * 16 + 16), cz * 16);
							inView = Camera3D.Frustum.Intersects(new BoundingBox(a, a + new Vector3(16f)));
						}
						bool near = d < 40f;
						if (m == null || !m.Built || m.Dirty) {
							if (inView || near)
								buildQueue.Add((inView ? d : d + 64f, cx, cy, cz));
						}
						if (m != null) {
							m.LastUsed = frame;
							if (inView && m.Built && !m.Empty)
								visible.Add(m);
						}
					}
				}
			}

			// build within the time budget, closest first
			buildQueue.Sort((a, b) => a.d.CompareTo(b.d));
			var sw = Stopwatch.StartNew();
			double budget = cfg.ChunkBuildBudgetMs;
			foreach (var q in buildQueue) {
				if (sw.Elapsed.TotalMilliseconds > budget)
					break;
				int key = Voxel.ChunkKey(q.cx, q.cy, q.cz);
				if (!meshes.TryGetValue(key, out var m)) {
					m = new ChunkMesh(q.cx, q.cy, q.cz);
					meshes[key] = m;
				}
				bool hadMeshVisible = m.Built && !m.Empty;
				ChunkBuilder.Build(m, GetLights, out bool lightsChanged);
				lightCache.Remove(key);
				if (lightsChanged && (hadMeshVisible || m.Lights.Count > 0))
					MarkNeighborsDirty(q.cx, q.cy, q.cz);
				m.LastUsed = frame;
				if (!m.Empty && Camera3D.Frustum.Intersects(m.Box) && !visible.Contains(m))
					visible.Add(m);
			}

			// relight: new meshes first, then everything lit with an old sun or player light, nearest first, within a time budget
			bool playerMoved = Vector3.Distance(PlayerLightPos, lastPlayerLightPos) > 0.5f || PlayerLightColor != lastPlayerLightColor;
			relightQueue.Clear();
			foreach (var m in meshes.Values) {
				if (!m.Built || m.Empty || m.Parts.Count == 0)
					continue;
				bool fresh = m.LitSun < 0f || m.Parts[0].VB == null;
				bool sunStale = Math.Abs(m.LitSun - sunKey) > 0.001f;
				bool playerStale = playerMoved && (m.LitWithPlayer || PlayerLightRadius > 0f && Vector3.Distance(m.CenterTile, PlayerLightPos) < PlayerLightRadius + 16f);
				if (fresh || sunStale || playerStale) {
					float d = Vector3.DistanceSquared(m.CenterTile, camTile);
					relightQueue.Add((fresh ? d - 1e9f : d, m));
				}
			}
			relightQueue.Sort((a, b) => a.d.CompareTo(b.d));
			var rsw = Stopwatch.StartNew();
			foreach (var (_, m) in relightQueue) {
				bool fresh = m.LitSun < 0f || m.Parts[0].VB == null;
				if (!fresh && rsw.Elapsed.TotalMilliseconds > 3.0)
					break;
				Relight(m, gd);
			}
			if (playerMoved) {
				lastPlayerLightPos = PlayerLightPos;
				lastPlayerLightColor = PlayerLightColor;
			}

			// evict old meshes
			if (frame % 120 == 0 && meshes.Count > 3000) {
				var remove = new List<int>();
				foreach (var (k, m) in meshes)
					if (frame - m.LastUsed > 600)
						remove.Add(k);
				foreach (int k in remove) {
					meshes[k].DisposeParts();
					meshes.Remove(k);
				}
			}
			if (lightCache.Count > 20000)
				lightCache.Clear();
		}

		public static void DrawOpaque(GraphicsDevice gd, AlphaTestEffect fx)
		{
			gd.Indices = quadIndices;
			foreach (var m in visible) {
				foreach (var part in m.Parts) {
					if (part.Translucent || part.VB == null)
						continue;
					fx.Texture = part.Tex;
					fx.CurrentTechnique.Passes[0].Apply();
					DrawPart(gd, part);
				}
			}
		}

		public static void DrawTranslucent(GraphicsDevice gd, BasicEffect fx, Vector3 camTile)
		{
			gd.Indices = quadIndices;
			visible.Sort((a, b) => Vector3.DistanceSquared(b.CenterTile, camTile).CompareTo(Vector3.DistanceSquared(a.CenterTile, camTile)));
			foreach (var m in visible) {
				foreach (var part in m.Parts) {
					if (!part.Translucent || part.VB == null)
						continue;
					fx.Texture = part.Tex;
					fx.CurrentTechnique.Passes[0].Apply();
					DrawPart(gd, part);
				}
			}
		}

		private static void DrawPart(GraphicsDevice gd, MeshPart part)
		{
			gd.SetVertexBuffer(part.VB);
			int remaining = part.QuadCount;
			int start = 0;
			while (remaining > 0) {
				int n = Math.Min(remaining, MaxQuads);
				gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, start * 4, 0, n * 4, 0, n * 2);
				start += n;
				remaining -= n;
			}
		}
	}
}
