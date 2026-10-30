using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.IO;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace TerraDepth.World
{
	/// <summary>
	/// The 3D world. Size is Main.maxTilesX x Main.maxTilesY x Depth.
	///
	/// Storage model (keeps the world file small):
	///  * The vanilla 2D world is the "base". Slice ZC (the middle) is exactly the base.
	///  * Every other slice is derived procedurally from the base (shifted terrain + 3D caves).
	///  * Only cells that differ from the derived value are stored ("edits") and saved.
	///  * The slice the player stands in is materialized into Main.tile inside a window around
	///    the player, so vanilla physics, NPC AI, liquids, mining and placing all keep working.
	/// </summary>
	public static class Voxel
	{
		public const int CS = 16;
		// The live window must always contain vanilla's NPC spawn ring around the player (up to ~84 tiles sideways and
		// ~47 up/down), so the margins (minimum distance from the player to the window edge) are larger than that.
		public const int WinHalfW = 135, WinHalfH = 90, WinMarginX = 95, WinMarginY = 55;

		public static bool Ready { get; private set; }
		public static int W, H, D, ZC, Seed;
		public static int CW, CH, CD;

		public static WorldPlan Plan { get; private set; }

		// edits per chunk: chunkKey -> (local index -> tile)
		private static readonly Dictionary<int, Dictionary<int, VTile>> edits = new();
		public static readonly object Sync = new();
		public static int EditCount { get; private set; }

		// live window
		public static int WinZ = -1, WinX, WinY, WinW, WinH;
		private static VTile[] winBase;
		// what was written into Main.tile for the live slice, to find what vanilla changed
		private static VTile[] winSlice;
		public static bool WindowOpen => WinZ >= 0;

		/// <summary>Set while vanilla code operates on a slice that is not the window slice.</summary>
		public static int OperatingZ = -1;

		/// <summary>Raised with chunk coordinates when stored voxel content changes.</summary>
		public static event Action<int, int, int> ChunkChanged;

		private static short[] topCache;

		private static object ioLock;
		private static object IOLock => ioLock ??= typeof(WorldFile).GetField("IOLock", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) ?? new object();

		public static void Init(int depth, int seed, WorldPlan plan)
		{
			Reset();
			W = Main.maxTilesX;
			H = Main.maxTilesY;
			D = Math.Max(32, depth);
			ZC = D / 2;
			Seed = seed;
			CW = (W + CS - 1) / CS;
			CH = (H + CS - 1) / CS;
			CD = (D + CS - 1) / CS;
			Plan = plan ?? WorldPlan.Build();
			Gen3D.Init(Plan, seed, D, ZC);
			topCache = new short[W * D];
			Array.Fill(topCache, (short)-1);
			Ready = true;
		}

		public static void Reset()
		{
			Ready = false;
			lock (Sync) {
				edits.Clear();
				EditCount = 0;
			}
			WinZ = -1;
			winBase = null;
			winSlice = null;
			ClearPrefetch();
			topCache = null;
			OperatingZ = -1;
			Gen3D.Reset();
		}

		private static float Smooth(float t)
		{
			t = Math.Clamp(t, 0f, 1f);
			return t * t * (3 - 2 * t);
		}

		public static int ChunkKey(int cx, int cy, int cz) => (cz * CH + cy) * CW + cx;

		public static bool InBounds(int x, int y, int z) => (uint)x < (uint)W && (uint)y < (uint)H && (uint)z < (uint)D;

		public static bool InWindow(int x, int y) => WinZ >= 0 && x >= WinX && x < WinX + WinW && y >= WinY && y < WinY + WinH;

		// ---------------------------------------------------------------- base & derivation

		/// <summary>Vanilla world cell (the copy saved under the live window when it is open).</summary>
		internal static VTile Base(int x, int y)
		{
			if ((uint)x >= (uint)W || y < 0)
				return default;
			if (y >= H)
				y = H - 1;
			VTile[] wb = winBase;
			if (WinZ >= 0 && wb != null && x >= WinX && x < WinX + WinW && y >= WinY && y < WinY + WinH)
				return wb[(x - WinX) * WinH + (y - WinY)];
			return VTile.From(Main.tile[x, y]);
		}

		/// <summary>Content of a cell before player changes.</summary>
		public static VTile Derive(int x, int y, int z)
		{
			if (z == ZC)
				return Base(x, y);
			return Gen3D.Read(Gen3D.Chunk(x >> 4, y >> 4, z >> 4), x, y, z);
		}

		// ---------------------------------------------------------------- access

		private static bool TryEdit(int x, int y, int z, out VTile v)
		{
			if (EditCount > 0 && edits.TryGetValue(ChunkKey(x >> 4, y >> 4, z >> 4), out var dict))
				return dict.TryGetValue(((z & 15) * CS + (y & 15)) * CS + (x & 15), out v);
			v = default;
			return false;
		}

		/// <summary>Stored value (edits or derived), ignoring the live window.</summary>
		public static VTile GetStored(int x, int y, int z)
		{
			if (TryEdit(x, y, z, out VTile v))
				return v;
			return Derive(x, y, z);
		}

		public static VTile Get(int x, int y, int z)
		{
			if (!Ready || !InBounds(x, y, z))
				return default;
			if (z == WinZ && InWindow(x, y))
				return VTile.From(Main.tile[x, y]);
			return GetStored(x, y, z);
		}

		public static bool IsSolid(int x, int y, int z) => Get(x, y, z).IsSolid;

		/// <summary>
		/// Fills dst with voxels of the box [x0,x0+sx) x [y0,y0+sy) x [z0,z0+sz).
		/// Index = ((lz * sx) + lx) * sy + ly.
		/// </summary>
		public static void FillBlock(int x0, int y0, int z0, int sx, int sy, int sz, VTile[] dst)
		{
			for (int lz = 0; lz < sz; lz++) {
				int z = z0 + lz;
				for (int lx = 0; lx < sx; lx++) {
					int x = x0 + lx;
					int baseIdx = (lz * sx + lx) * sy;
					if ((uint)x >= (uint)W || (uint)z >= (uint)D) {
						Array.Clear(dst, baseIdx, sy);
						continue;
					}
					bool live = z == WinZ;
					Gen3D.GenChunk chunk = null;
					int chunkCy = int.MinValue;
					for (int ly = 0; ly < sy; ly++) {
						int y = y0 + ly;
						if ((uint)y >= (uint)H) {
							dst[baseIdx + ly] = default;
							continue;
						}
						if (live && InWindow(x, y))
							dst[baseIdx + ly] = VTile.From(Main.tile[x, y]);
						else if (TryEdit(x, y, z, out VTile e))
							dst[baseIdx + ly] = e;
						else if (z == ZC)
							dst[baseIdx + ly] = Base(x, y);
						else {
							if ((y >> 4) != chunkCy) {
								chunkCy = y >> 4;
								chunk = Gen3D.Chunk(x >> 4, chunkCy, z >> 4);
							}
							dst[baseIdx + ly] = Gen3D.Read(chunk, x, y, z);
						}
					}
				}
			}
		}

		/// <summary>Writes a voxel anywhere: into Main.tile if it is in the live window, else into edits.</summary>
		public static void Set(int x, int y, int z, in VTile v)
		{
			if (!Ready || !InBounds(x, y, z))
				return;
			if (z == WinZ && InWindow(x, y)) {
				v.WriteTo(Main.tile[x, y]);
				NotifyCell(x, y, z);
				return;
			}
			lock (Sync)
				StoreEdit(x, y, z, v, Derive(x, y, z));
		}

		private static void StoreEdit(int x, int y, int z, in VTile cur, in VTile derived, bool notify = true)
		{
			int key = ChunkKey(x >> 4, y >> 4, z >> 4);
			int local = ((z & 15) * CS + (y & 15)) * CS + (x & 15);
			edits.TryGetValue(key, out var dict);
			bool had = false;
			VTile old = default;
			if (dict != null)
				had = dict.TryGetValue(local, out old);
			bool changed = notify && !VTile.SameContent(had ? old : derived, cur);

			if (VTile.SameContent(cur, derived)) {
				if (had) {
					dict.Remove(local);
					EditCount--;
					if (dict.Count == 0)
						edits.Remove(key);
				}
			}
			else {
				if (dict == null)
					edits[key] = dict = new Dictionary<int, VTile>();
				if (!had)
					EditCount++;
				dict[local] = cur;
			}
			if (changed)
				NotifyCell(x, y, z);
		}

		public static void NotifyCell(int x, int y, int z)
		{
			if (topCache != null && (uint)x < (uint)W && (uint)z < (uint)D)
				topCache[z * W + x] = -1;
			int cx = x >> 4, cy = y >> 4, cz = z >> 4;
			var ev = ChunkChanged;
			if (ev == null)
				return;
			ev(cx, cy, cz);
			int lx = x & 15, ly = y & 15, lz = z & 15;
			if (lx == 0) ev(cx - 1, cy, cz);
			if (lx == 15) ev(cx + 1, cy, cz);
			if (ly == 0) ev(cx, cy - 1, cz);
			if (ly == 15) ev(cx, cy + 1, cz);
			if (lz == 0) ev(cx, cy, cz - 1);
			if (lz == 15) ev(cx, cy, cz + 1);
		}

		public static void InvalidateColumns(int x0, int x1, int z)
		{
			if (topCache == null || (uint)z >= (uint)D)
				return;
			for (int x = Math.Max(0, x0); x <= Math.Min(W - 1, x1); x++)
				topCache[z * W + x] = -1;
		}

		/// <summary>Rows above this (the floating island band) never block sky light.</summary>
		public static int SkyIgnoreRow => (int)(Main.worldSurface * 0.42);

		/// <summary>Topmost opaque block in column (x, z) below the island band; trees and plants don't block the sky.</summary>
		public static int ColumnTop(int x, int z)
		{
			if (!Ready || (uint)x >= (uint)W || (uint)z >= (uint)D)
				return H;
			int idx = z * W + x;
			short c = topCache[idx];
			if (c >= 0)
				return c;
			bool live = z == WinZ;
			int top = H - 1;
			Gen3D.GenChunk chunk = null;
			int chunkCy = int.MinValue;
			// floating islands live above this row and must not shade the ground far below them
			for (int y = SkyIgnoreRow; y < H; y++) {
				VTile v;
				if (live && InWindow(x, y))
					v = VTile.From(Main.tile[x, y]);
				else if (TryEdit(x, y, z, out v)) {
				}
				else if (z == ZC)
					v = Base(x, y);
				else {
					if ((y >> 4) != chunkCy) {
						chunkCy = y >> 4;
						chunk = Gen3D.Chunk(x >> 4, chunkCy, z >> 4);
						if (chunk.Cells == null) {
							// whole chunk is open air
							y = chunkCy * 16 + 15;
							continue;
						}
					}
					v = Gen3D.Read(chunk, x, y, z);
				}
				if (v.HasTile && !v.State.IsActuated && v.TileType < TileLoader.TileCount
					&& Main.tileBlockLight[v.TileType] && Main.tileSolid[v.TileType] && !Main.tileSolidTop[v.TileType]) {
					top = y;
					break;
				}
			}
			topCache[idx] = (short)top;
			return top;
		}

		// ---------------------------------------------------------------- window

		/// <summary>Make sure the live window shows slice pz around (px, py). Returns false if it could not (world is saving).</summary>
		public static bool EnsureWindow(int px, int py, int pz, bool force = false)
		{
			if (!Ready)
				return false;
			pz = Math.Clamp(pz, 0, D - 1);
			bool rectOk = WindowOpen
				&& (px >= WinX + WinMarginX || WinX == 0) && (px < WinX + WinW - WinMarginX || WinX + WinW == W)
				&& (py >= WinY + WinMarginY || WinY == 0) && (py < WinY + WinH - WinMarginY || WinY + WinH == H);
			if (rectOk && pz == WinZ && !force)
				return true;
			if (!Monitor.TryEnter(IOLock))
				return false;
			try {
				if (rectOk) {
					// only the depth changed: keep the base copy, swap slice contents
					FlushWindowToEdits();
					WinZ = pz;
					FillWindowSlice();
				}
				else {
					CloseWindowInternal();
					OpenWindowInternal(px, py, pz);
				}
			}
			finally {
				Monitor.Exit(IOLock);
			}
			return true;
		}

		/// <summary>Restores the pure base world into Main.tile.</summary>
		public static void CloseWindow()
		{
			if (!WindowOpen)
				return;
			bool taken = Monitor.TryEnter(IOLock, 2000);
			try {
				CloseWindowInternal();
			}
			finally {
				if (taken)
					Monitor.Exit(IOLock);
			}
		}

		/// <summary>Stores every cell of the live slice that changed since it was written into Main.tile.</summary>
		private static void FlushWindowToEdits()
		{
			int z = WinZ;
			VTile[] snap = winSlice;
			lock (Sync) {
				for (int x = WinX; x < WinX + WinW; x++) {
					int col = (x - WinX) * WinH;
					for (int y = WinY; y < WinY + WinH; y++) {
						VTile cur = VTile.From(Main.tile[x, y]);
						if (snap != null && VTile.SameContent(cur, snap[col + y - WinY]))
							continue;
						StoreEdit(x, y, z, cur, Derive(x, y, z), notify: false);
					}
				}
			}
		}

		private static void CloseWindowInternal()
		{
			if (!WindowOpen)
				return;
			FlushWindowToEdits();
			for (int x = WinX; x < WinX + WinW; x++) {
				int col = (x - WinX) * WinH;
				for (int y = WinY; y < WinY + WinH; y++)
					winBase[col + y - WinY].WriteTo(Main.tile[x, y]);
			}
			WinZ = -1;
			winBase = null;
			winSlice = null;
			ClearPrefetch();
		}

		private static void OpenWindowInternal(int px, int py, int pz)
		{
			WinW = Math.Min(W, WinHalfW * 2);
			WinH = Math.Min(H, WinHalfH * 2);
			WinX = Math.Clamp(px - WinW / 2, 0, W - WinW);
			WinY = Math.Clamp(py - WinH / 2, 0, H - WinH);
			var buf = new VTile[WinW * WinH];
			for (int x = WinX; x < WinX + WinW; x++) {
				int col = (x - WinX) * WinH;
				for (int y = WinY; y < WinY + WinH; y++)
					buf[col + y - WinY] = VTile.From(Main.tile[x, y]);
			}
			winBase = buf;
			WinZ = pz;
			FillWindowSlice();
		}

		/// <summary>Writes the stored content of slice WinZ into Main.tile (base copy must be in place).</summary>
		private static void FillWindowSlice()
		{
			int pz = WinZ;
			VTile[] slice = TakePrefetch(pz) ?? RawSlice(pz, WinX, WinY, WinW, WinH, winBase);
			if (EditCount > 0) {
				for (int x = WinX; x < WinX + WinW; x++) {
					int col = (x - WinX) * WinH;
					for (int y = WinY; y < WinY + WinH; y++)
						if (TryEdit(x, y, pz, out VTile v))
							slice[col + y - WinY] = v;
				}
			}
			for (int x = WinX; x < WinX + WinW; x++) {
				int col = (x - WinX) * WinH;
				for (int y = WinY; y < WinY + WinH; y++)
					slice[col + y - WinY].WriteTo(Main.tile[x, y]);
			}
			winSlice = slice;
			StartPrefetch();
		}

		// ---------------------------------------------------------------- neighbour slice prefetch

		private static readonly Task<VTile[]>[] prefetch = new Task<VTile[]>[2];
		private static readonly int[] prefetchZ = { -1, -1 };
		// window rectangle each prefetch was made for: a slice made for another rectangle must never be used
		private static readonly long[] prefetchRect = { -1, -1 };

		private static long RectStamp => ((long)WinX << 40) ^ ((long)WinY << 20) ^ ((long)WinW << 10) ^ WinH;

		/// <summary>Generated content of slice pz in a rectangle, without player edits. Safe to run off the main thread.</summary>
		private static VTile[] RawSlice(int pz, int x0, int y0, int w, int h, VTile[] baseCopy)
		{
			var slice = new VTile[w * h];
			for (int x = x0; x < x0 + w; x++) {
				int col = (x - x0) * h;
				if (pz == ZC)
					Array.Copy(baseCopy, col, slice, col, h);
				else
					Gen3D.FillColumn(x, pz, y0, h, slice, col, 1, null);
			}
			return slice;
		}

		/// <summary>Generates the slices in front of and behind the player in the background, so stepping in depth is instant.</summary>
		private static void StartPrefetch()
		{
			long rect = RectStamp;
			int x0 = WinX, y0 = WinY, w = WinW, h = WinH;
			VTile[] wb = winBase;
			for (int i = 0; i < 2; i++) {
				int z2 = WinZ + (i == 0 ? 1 : -1);
				if (z2 < 0 || z2 >= D) {
					prefetchZ[i] = -1;
					prefetch[i] = null;
					continue;
				}
				if (prefetchZ[i] == z2 && prefetchRect[i] == rect && prefetch[i] != null)
					continue;
				prefetchZ[i] = z2;
				prefetchRect[i] = rect;
				prefetch[i] = Task.Run(() => RawSlice(z2, x0, y0, w, h, wb));
			}
		}

		private static VTile[] TakePrefetch(int pz)
		{
			long rect = RectStamp;
			for (int i = 0; i < 2; i++) {
				var task = prefetch[i];
				if (prefetchZ[i] != pz || prefetchRect[i] != rect || task == null)
					continue;
				prefetch[i] = null;
				prefetchZ[i] = -1;
				try {
					if (!task.IsCompleted)
						task.Wait(60);
					if (task.IsCompletedSuccessfully)
						return task.Result;
				}
				catch {
					// fall back to generating it now
				}
			}
			return null;
		}

		private static void ClearPrefetch()
		{
			for (int i = 0; i < 2; i++) {
				prefetch[i] = null;
				prefetchZ[i] = -1;
				prefetchRect[i] = -1;
			}
		}


		// ---------------------------------------------------------------- save / load

		private static byte[] Pack(Action<BinaryWriter> write)
		{
			using var ms = new MemoryStream();
			using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
			using (var w = new BinaryWriter(gz))
				write(w);
			return ms.ToArray();
		}

		private static BinaryReader Unpack(byte[] data) => new BinaryReader(new GZipStream(new MemoryStream(data), CompressionMode.Decompress));

		public const int FormatVersion = 3;

		/// <summary>
		/// One-time conversion: the vanilla 2D world becomes the center slice of the 3D world. Generated terrain replaces
		/// everything except the masked vanilla structures and objects, so the middle is no different from any other slice.
		/// </summary>
		public static void ConvertBase()
		{
			var log = ModContent.GetInstance<TerraDepth.Common.TerraDepthMod>().Logger;
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var col = new VTile[H];
			for (int x = 0; x < W; x++) {
				Gen3D.FillColumn(x, ZC, 0, H, col, 0, 1, null, pure: true);
				for (int y = 0; y < H; y++)
					if (Plan.MaskAt(x, y) == WorldPlan.MaskNone)
						col[y].WriteTo(Main.tile[x, y]);
			}
			Gen3D.Clear();
			Array.Fill(topCache, (short)-1);

			// spawn on top of the new ground, town NPCs with it
			int sx = Math.Clamp(Main.spawnTileX, 50, W - 50);
			int sy = 50;
			while (sy < H - 250 && !(Main.tile[sx, sy].HasTile && Main.tileSolid[Main.tile[sx, sy].TileType] && !Main.tileSolidTop[Main.tile[sx, sy].TileType]))
				sy++;
			Main.spawnTileX = sx;
			Main.spawnTileY = sy;
			for (int i = 0; i < Main.maxNPCs; i++) {
				NPC n = Main.npc[i];
				// the Old Man belongs to the dungeon entrance, not to the spawn point
				if (n.active && n.townNPC && n.type != NPCID.OldMan)
					n.position = new Microsoft.Xna.Framework.Vector2(sx * 16 - n.width / 2, sy * 16 - n.height - 2);
			}
			Main.clearMap = true;
			log.Info($"TerraDepth: converted world to 3D generator center slice in {sw.ElapsedMilliseconds} ms (spawn {sx},{sy})");
		}

		public static void Save(TagCompound tag)
		{
			if (!Ready)
				return;
			tag["version"] = FormatVersion;
			tag["vsize"] = VTile.Size;
			tag["depth"] = D;
			tag["seed"] = Seed;
			var planTag = new TagCompound();
			Plan.Save(planTag);
			tag["plan"] = planTag;
			lock (Sync) {
				tag["edits"] = Pack(w => {
					w.Write(EditCount);
					foreach (var (key, dict) in edits) {
						foreach (var (local, v) in dict) {
							w.Write(key);
							w.Write((ushort)local);
							VTile.Write(w, v);
						}
					}
				});
			}
			if (WindowOpen) {
				tag["win"] = new int[] { WinX, WinY, WinW, WinH, WinZ };
				VTile[] buf = winBase;
				tag["winBase"] = Pack(w => {
					foreach (var v in buf)
						VTile.Write(w, v);
				});
			}
		}

		public static void Load(TagCompound tag, int defaultDepth)
		{
			var log = ModContent.GetInstance<TerraDepth.Common.TerraDepthMod>().Logger;
			int version = tag.ContainsKey("version") ? tag.GetInt("version") : 0;
			bool sameLayout = tag.ContainsKey("vsize") && tag.GetInt("vsize") == VTile.Size;
			int depth = tag.ContainsKey("depth") ? tag.GetInt("depth") : defaultDepth;
			int seed = tag.ContainsKey("seed") ? tag.GetInt("seed") : Main.ActiveWorldFileData?.Seed ?? 1;

			// The .wld file holds the live slice inside the saved window; the vanilla cells are in winBase.
			int[] win = tag.ContainsKey("win") ? tag.GetIntArray("win") : null;
			VTile[] savedBase = null;
			if (sameLayout && win != null && win.Length == 5 && tag.ContainsKey("winBase") && win[0] >= 0 && win[1] >= 0
				&& win[0] + win[2] <= Main.maxTilesX && win[1] + win[3] <= Main.maxTilesY && win[4] >= 0) {
				savedBase = new VTile[win[2] * win[3]];
				using var r = Unpack(tag.GetByteArray("winBase"));
				for (int i = 0; i < savedBase.Length; i++)
					savedBase[i] = VTile.Read(r);
			}

			bool current = version == FormatVersion && sameLayout;
			if (!current) {
				// Old generator: its edits mean nothing for the new one. Put the vanilla world back and start fresh.
				if (savedBase != null) {
					for (int x = 0; x < win[2]; x++)
						for (int y = 0; y < win[3]; y++)
							savedBase[x * win[3] + y].WriteTo(Main.tile[win[0] + x, win[1] + y]);
				}
				if (tag.ContainsKey("edits"))
					log.Warn("TerraDepth: world was saved by an older 3D generator, its 3D changes were reset.");
				Init(depth, seed, null);
				ConvertBase();
				return;
			}

			WorldPlan plan = tag.ContainsKey("plan") ? WorldPlan.Load(tag.GetCompound("plan")) : null;
			if (plan == null && savedBase != null) {
				// build the plan from the vanilla world, not from the live slice
				for (int x = 0; x < win[2]; x++)
					for (int y = 0; y < win[3]; y++) {
						Tile t = Main.tile[win[0] + x, win[1] + y];
						VTile live = VTile.From(t);
						savedBase[x * win[3] + y].WriteTo(t);
						savedBase[x * win[3] + y] = live;
					}
				plan = WorldPlan.Build();
				for (int x = 0; x < win[2]; x++)
					for (int y = 0; y < win[3]; y++) {
						Tile t = Main.tile[win[0] + x, win[1] + y];
						VTile vanilla = VTile.From(t);
						savedBase[x * win[3] + y].WriteTo(t);
						savedBase[x * win[3] + y] = vanilla;
					}
			}
			Init(depth, seed, plan);
			if (tag.ContainsKey("edits")) {
				using var r = Unpack(tag.GetByteArray("edits"));
				int count = r.ReadInt32();
				lock (Sync) {
					for (int i = 0; i < count; i++) {
						int key = r.ReadInt32();
						int local = r.ReadUInt16();
						VTile v = VTile.Read(r);
						if (!edits.TryGetValue(key, out var dict))
							edits[key] = dict = new Dictionary<int, VTile>();
						if (!dict.ContainsKey(local))
							EditCount++;
						dict[local] = v;
					}
				}
			}
			if (savedBase != null && win[4] < D) {
				// store the live slice from the .wld as edits and restore the vanilla cells
				WinX = win[0]; WinY = win[1]; WinW = win[2]; WinH = win[3];
				winBase = savedBase;
				WinZ = win[4];
				winSlice = null;
				CloseWindowInternal();
			}
		}
	}
}
