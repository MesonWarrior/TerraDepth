using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace TerraDepth.World
{
	public static class Biome
	{
		public const byte Normal = 0, Desert = 1, Snow = 2, Jungle = 3, Corrupt = 4, Crimson = 5, Hallow = 6, Mushroom = 7, Ocean = 8, Granite = 9, Marble = 10;
		public const int Count = 11;
	}

	/// <summary>
	/// Large-scale layout taken from the vanilla world (biomes along X, surface height, sea level, ore types).
	/// The 3D generator builds real 3D terrain from it, so every world keeps Terraria's biome order.
	/// </summary>
	public sealed class WorldPlan
	{
		public const int Grid = 8;
		public int W, H, GW, GH;
		public short[] Surf;    // smoothed surface row per column
		public short[] Sea;     // water surface row for ocean columns, -1 otherwise
		public byte[] BiomeS;   // surface biome per column
		public float[] Amp;     // hill amplitude per column
		public byte[] BiomeU;   // underground biome, Grid x Grid cells
		public ushort Copper, Iron, Silver, Gold, DungeonBrick;
		public short[] Level;   // large-scale ground level per column (vanilla hills removed)
		/// <summary>Vanilla cells kept in the 3D world: 0 none, 1 structure (3D halls), 2 object pocket.</summary>
		public byte[] Mask;

		public const byte MaskNone = 0, MaskStructure = 1, MaskObject = 2;

		/// <summary>Kinds of vanilla structures, which decide how each is turned into 3D.</summary>
		public static class StructType
		{
			public const byte Dungeon = 1, Temple = 2, Hive = 3, LivingTree = 4, Pyramid = 5, SkyHouse = 6, Island = 7,
				HellHouse = 8, Building = 9, Shimmer = 10;

			/// <summary>Organic shapes are revolved with a round profile, buildings with a square one.</summary>
			public static bool Round(byte t) => t is Hive or LivingTree or Island or Shimmer;
		}

		public sealed class Structure
		{
			public ushort Id;
			public byte Type;
			public int X0, X1, Y0, Y1, Cx;
			public int Radius;        // half depth of the 3D version
			public short[] ColTop, ColBottom; // per column vertical extent (dungeon volume)
		}

		/// <summary>Structure id per cell (0 = none) and the structures themselves.</summary>
		public ushort[] Comp;
		public List<Structure> Structures = new();

		public ushort CompAt(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H ? Comp[y * W + x] : (ushort)0;

		public byte MaskAt(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H ? Mask[y * W + x] : MaskNone;

		public byte Underground(int x, int y)
		{
			int gx = Math.Clamp(x / Grid, 0, GW - 1), gy = Math.Clamp(y / Grid, 0, GH - 1);
			return BiomeU[gy * GW + gx];
		}

		private static byte BiomeOf(ushort t)
		{
			switch (t) {
				case TileID.Sand: case TileID.HardenedSand: case TileID.Sandstone: return Biome.Desert;
				case TileID.SnowBlock: case TileID.IceBlock: case TileID.Slush: return Biome.Snow;
				case TileID.Mud: case TileID.JungleGrass: return Biome.Jungle;
				case TileID.Ebonstone: case TileID.CorruptGrass: case TileID.Ebonsand: case TileID.CorruptHardenedSand:
				case TileID.CorruptSandstone: case TileID.CorruptIce: return Biome.Corrupt;
				case TileID.Crimstone: case TileID.CrimsonGrass: case TileID.Crimsand: case TileID.CrimsonHardenedSand:
				case TileID.CrimsonSandstone: case TileID.FleshIce: return Biome.Crimson;
				case TileID.Pearlstone: case TileID.HallowedGrass: case TileID.Pearlsand: case TileID.HallowHardenedSand:
				case TileID.HallowSandstone: case TileID.HallowedIce: return Biome.Hallow;
				case TileID.MushroomGrass: return Biome.Mushroom;
				case TileID.Granite: return Biome.Granite;
				case TileID.Marble: return Biome.Marble;
			}
			return 255;
		}

		private static bool Ground(Tile t) => t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType]
			&& t.TileType != TileID.Trees && t.TileType != TileID.Cactus && t.TileType != TileID.Cloud && t.TileType != TileID.RainCloud;

		public static WorldPlan Build()
		{
			int W = Main.maxTilesX, H = Main.maxTilesY;
			var p = new WorldPlan { W = W, H = H, GW = (W + Grid - 1) / Grid, GH = (H + Grid - 1) / Grid };
			var surf = new int[W];
			p.Sea = new short[W];
			int rock = (int)Main.rockLayer;
			for (int x = 0; x < W; x++) {
				surf[x] = (int)Main.worldSurface;
				p.Sea[x] = -1;
				int firstLiquid = -1;
				for (int y = 20; y < rock; y++) {
					Tile t = Main.tile[x, y];
					if (firstLiquid < 0 && t.LiquidAmount > 0 && !t.HasTile && y > Main.worldSurface * 0.45)
						firstLiquid = y;
					if (!Ground(t))
						continue;
					// skip floating islands: need a thick body of ground
					int solid = 0;
					for (int k = 0; k < 14 && y + k < H; k++)
						if (Ground(Main.tile[x, y + k]))
							solid++;
					if (solid >= 10) {
						surf[x] = y;
						break;
					}
				}
				bool nearEdge = x < 450 || x > W - 450;
				if (nearEdge && firstLiquid >= 0 && firstLiquid < surf[x])
					p.Sea[x] = (short)firstLiquid;
			}
			p.Surf = new short[W];
			for (int x = 0; x < W; x++) {
				int sum = 0, n = 0;
				for (int k = -6; k <= 6; k++) {
					int xx = Math.Clamp(x + k, 0, W - 1);
					sum += surf[xx];
					n++;
				}
				p.Surf[x] = (short)(sum / n);
			}

			// surface biome per column
			p.BiomeS = new byte[W];
			var counts = new int[Biome.Count];
			for (int x = 0; x < W; x++) {
				Array.Clear(counts);
				for (int xx = Math.Max(0, x - 3); xx <= Math.Min(W - 1, x + 3); xx++)
					for (int y = Math.Max(0, surf[xx] - 3); y < Math.Min(H, surf[xx] + 30); y++) {
						Tile t = Main.tile[xx, y];
						if (!t.HasTile)
							continue;
						byte b = BiomeOf(t.TileType);
						if (b != 255)
							counts[b]++;
					}
				byte best = Biome.Normal;
				int bestN = 25;
				for (int b = 1; b < Biome.Count; b++)
					if (counts[b] > bestN && b != Biome.Granite && b != Biome.Marble) {
						best = (byte)b;
						bestN = counts[b];
					}
				if (p.Sea[x] >= 0)
					best = Biome.Ocean;
				p.BiomeS[x] = best;
			}

			p.ComputeAmp();
			p.BuildMask();
			p.BuildStructures();

			// underground biome grid
			p.BiomeU = new byte[p.GW * p.GH];
			for (int gy = 0; gy < p.GH; gy++)
				for (int gx = 0; gx < p.GW; gx++) {
					Array.Clear(counts);
					int total = 0;
					for (int sy = 0; sy < Grid; sy += 2)
						for (int sx = 0; sx < Grid; sx += 2) {
							int x = gx * Grid + sx, y = gy * Grid + sy;
							if (x >= W || y >= H)
								continue;
							Tile t = Main.tile[x, y];
							if (!t.HasTile)
								continue;
							total++;
							byte b = BiomeOf(t.TileType);
							if (b != 255)
								counts[b]++;
						}
					byte best = Biome.Normal;
					int bestN = Math.Max(2, total / 3);
					for (int b = 1; b < Biome.Count; b++)
						if (counts[b] >= bestN) {
							best = (byte)b;
							bestN = counts[b];
						}
					p.BiomeU[gy * p.GW + gx] = best;
				}

			p.Copper = (ushort)(WorldGen.SavedOreTiers.Copper > 0 ? WorldGen.SavedOreTiers.Copper : TileID.Copper);
			p.Iron = (ushort)(WorldGen.SavedOreTiers.Iron > 0 ? WorldGen.SavedOreTiers.Iron : TileID.Iron);
			p.Silver = (ushort)(WorldGen.SavedOreTiers.Silver > 0 ? WorldGen.SavedOreTiers.Silver : TileID.Silver);
			p.Gold = (ushort)(WorldGen.SavedOreTiers.Gold > 0 ? WorldGen.SavedOreTiers.Gold : TileID.Gold);

			// dungeon brick colour of this world
			var bricks = new int[3];
			for (int x = 0; x < W; x += 2)
				for (int y = (int)Main.worldSurface - 50; y < H - 200; y += 3) {
					if (y < 0)
						continue;
					ushort t = Main.tile[x, y].TileType;
					if (!Main.tile[x, y].HasTile)
						continue;
					if (t == TileID.BlueDungeonBrick) bricks[0]++;
					else if (t == TileID.GreenDungeonBrick) bricks[1]++;
					else if (t == TileID.PinkDungeonBrick) bricks[2]++;
				}
			p.DungeonBrick = bricks[1] > bricks[0] && bricks[1] >= bricks[2] ? TileID.GreenDungeonBrick
				: bricks[2] > bricks[0] ? TileID.PinkDungeonBrick : TileID.BlueDungeonBrick;
			return p;
		}

		private static bool StructureTile(ushort t)
		{
			if (t >= TileLoader.TileCount)
				return false;
			if (Main.tileDungeon[t] || TileID.Sets.Platforms[t])
				return true;
			switch (t) {
				case TileID.LihzahrdBrick: case TileID.GrayBrick: case TileID.GoldBrick: case TileID.ObsidianBrick:
				case TileID.HellstoneBrick: case TileID.SandstoneBrick: case TileID.WoodBlock: case TileID.BorealWood:
				case TileID.RichMahogany: case TileID.LivingWood: case TileID.LeafBlock: case TileID.Sunplate:
				case TileID.Hive: case TileID.Larva:
					return true;
			}
			return false;
		}

		private static bool StructureWall(ushort w)
		{
			if (w == 0 || w >= WallLoader.WallCount)
				return false;
			if (Main.wallDungeon[w] || Main.wallHouse[w])
				return true;
			switch (w) {
				case WallID.LihzahrdBrickUnsafe: case WallID.Wood: case WallID.GrayBrick: case WallID.GoldBrick:
				case WallID.HellstoneBrickUnsafe: case WallID.ObsidianBrickUnsafe: case WallID.Glass: case WallID.Planked:
				case WallID.SandstoneBrick: case WallID.RichMaogany: case WallID.LivingLeaf: case WallID.LivingWood:
				case WallID.DiscWall: case WallID.HiveUnsafe: case WallID.BorealWood: case WallID.LivingWoodUnsafe:
					return true;
			}
			return false;
		}

		/// <summary>Things worth keeping from vanilla that cannot be generated: chests, orbs, altars, crystals, pots, statues...</summary>
		private static bool ObjectTile(ushort t)
		{
			if (t >= TileLoader.TileCount || !Main.tileFrameImportant[t])
				return t == TileID.Pots;
			if (t == TileID.Pots || t == TileID.Heart || t == TileID.ShadowOrbs || t == TileID.DemonAltar)
				return true;
			if (Main.tileCut[t])
				return false;
			switch (t) {
				case TileID.Trees: case TileID.PalmTree: case TileID.Cactus: case TileID.Saplings: case TileID.Sunflower:
				case TileID.SmallPiles: case TileID.LargePiles: case TileID.LargePiles2: case TileID.Stalactite:
				case TileID.ExposedGems: case TileID.Coral: case TileID.BeachPiles: case TileID.LilyPad: case TileID.Cattail:
				case TileID.Seaweed: case TileID.DyePlants: case TileID.ImmatureHerbs: case TileID.MatureHerbs:
				case TileID.BloomingHerbs: case TileID.MushroomTrees: case TileID.Plants2:
					return false;
			}
			return !(t >= TileID.TreeTopaz && t <= TileID.TreeAmber) && t != TileID.TreeAsh && t != TileID.VanityTreeSakura && t != TileID.VanityTreeYellowWillow;
		}

		private void BuildMask()
		{
			Mask = new byte[W * H];
			for (int x = 0; x < W; x++) {
				int floating = Surf[x] - 30;
				for (int y = 0; y < H; y++) {
					Tile t = Main.tile[x, y];
					bool keep = StructureWall(t.WallType) || t.HasTile && StructureTile(t.TileType)
						|| t.LiquidAmount > 0 && t.LiquidType == LiquidID.Shimmer
						|| t.HasTile && y < floating && Main.tileSolid[t.TileType]; // sky islands
					if (keep)
						Mask[y * W + x] = MaskStructure;
				}
			}
			// pockets around loose objects (with the floor they stand on)
			for (int x = 0; x < W; x++)
				for (int y = 0; y < H; y++) {
					Tile t = Main.tile[x, y];
					if (!t.HasTile || !ObjectTile(t.TileType) || Mask[y * W + x] != MaskNone)
						continue;
					for (int xx = Math.Max(0, x - 2); xx <= Math.Min(W - 1, x + 2); xx++)
						for (int yy = Math.Max(0, y - 3); yy <= Math.Min(H - 1, y + 1); yy++)
							if (Mask[yy * W + xx] == MaskNone)
								Mask[yy * W + xx] = MaskObject;
				}
		}

		private static byte ClassOf(Tile t, int y, int surf)
		{
			ushort w = t.WallType;
			ushort tt = t.HasTile ? t.TileType : (ushort)0;
			bool hasT = t.HasTile && tt < TileLoader.TileCount;
			if (w > 0 && w < WallLoader.WallCount && Main.wallDungeon[w] || hasT && Main.tileDungeon[tt])
				return StructType.Dungeon;
			if (w == WallID.LihzahrdBrickUnsafe || hasT && tt == TileID.LihzahrdBrick)
				return StructType.Temple;
			if (w == WallID.HiveUnsafe || hasT && (tt == TileID.Hive || tt == TileID.Larva))
				return StructType.Hive;
			if (w == WallID.LivingWood || w == WallID.LivingWoodUnsafe || w == WallID.LivingLeaf || hasT && (tt == TileID.LivingWood || tt == TileID.LeafBlock))
				return StructType.LivingTree;
			if (w == WallID.SandstoneBrick || hasT && tt == TileID.SandstoneBrick)
				return StructType.Pyramid;
			if (w == WallID.HellstoneBrickUnsafe || w == WallID.ObsidianBrickUnsafe || hasT && (tt == TileID.ObsidianBrick || tt == TileID.HellstoneBrick))
				return StructType.HellHouse;
			if (t.LiquidAmount > 0 && t.LiquidType == LiquidID.Shimmer)
				return StructType.Shimmer;
			bool sky = y < surf - 30;
			if (sky && (w == WallID.DiscWall || w == WallID.Glass || hasT && tt == TileID.Sunplate || w > 0 && w < WallLoader.WallCount && Main.wallHouse[w]))
				return StructType.SkyHouse;
			if (sky)
				return StructType.Island;
			return StructType.Building;
		}

		/// <summary>Connected groups of structure cells of one kind, each turned into one 3D structure.</summary>
		private void BuildStructures()
		{
			Comp = new ushort[W * H];
			Structures.Clear();
			var cls = new byte[W * H];
			for (int x = 0; x < W; x++)
				for (int y = 0; y < H; y++)
					if (Mask[y * W + x] == MaskStructure)
						cls[y * W + x] = ClassOf(Main.tile[x, y], y, Surf[x]);
			var stack = new Stack<int>();
			for (int start = 0; start < W * H; start++) {
				if (cls[start] == 0 || Comp[start] != 0 || Structures.Count >= 60000)
					continue;
				byte type = cls[start];
				var st = new Structure { Id = (ushort)(Structures.Count + 1), Type = type, X0 = int.MaxValue, Y0 = int.MaxValue, X1 = -1, Y1 = -1 };
				Comp[start] = st.Id;
				stack.Push(start);
				int count = 0;
				while (stack.Count > 0) {
					int i = stack.Pop();
					int x = i % W, y = i / W;
					count++;
					st.X0 = Math.Min(st.X0, x); st.X1 = Math.Max(st.X1, x);
					st.Y0 = Math.Min(st.Y0, y); st.Y1 = Math.Max(st.Y1, y);
					for (int dy = -1; dy <= 1; dy++)
						for (int dx = -1; dx <= 1; dx++) {
							int nx = x + dx, ny = y + dy;
							if ((uint)nx >= (uint)W || (uint)ny >= (uint)H)
								continue;
							int j = ny * W + nx;
							if (Comp[j] == 0 && cls[j] == type) {
								Comp[j] = st.Id;
								stack.Push(j);
							}
						}
				}
				Structures.Add(st);
				if (count < 6) {
					// specks stay a single-slice detail
					st.Type = 0;
				}
			}
			FinishStructures();
		}

		/// <summary>Derived data (centers, radii, dungeon column extents).</summary>
		private void FinishStructures()
		{
			int depthHalf = Math.Max(8, Main.maxTilesX * 25 / 100 / 2);
			foreach (var st in Structures) {
				st.Cx = (st.X0 + st.X1) / 2;
				st.Radius = Math.Max(2, (st.X1 - st.X0 + 1) / 2);
				if (st.Type != StructType.Dungeon)
					continue;
				int n = st.X1 - st.X0 + 1;
				st.ColTop = new short[n];
				st.ColBottom = new short[n];
				for (int i = 0; i < n; i++) {
					st.ColTop[i] = short.MaxValue;
					st.ColBottom[i] = -1;
					for (int y = st.Y0; y <= st.Y1; y++)
						if (Comp[y * W + st.X0 + i] == st.Id) {
							st.ColTop[i] = (short)Math.Min(st.ColTop[i], y);
							st.ColBottom[i] = (short)y;
						}
				}
			}
		}

		/// <summary>Hill amplitude per column, smoothed so biome borders blend.</summary>
		private void ComputeAmp()
		{
			var raw = new float[W];
			for (int x = 0; x < W; x++) {
				raw[x] = BiomeS[x] switch {
					Biome.Snow => 34f, Biome.Jungle => 22f, Biome.Desert => 9f, Biome.Corrupt => 20f, Biome.Crimson => 20f,
					Biome.Mushroom => 10f, Biome.Ocean => 0f, _ => 16f
				};
			}
			Amp = new float[W];
			for (int x = 0; x < W; x++) {
				float s = 0;
				int n = 0;
				for (int k = -40; k <= 40; k += 4) {
					s += raw[Math.Clamp(x + k, 0, W - 1)];
					n++;
				}
				Amp[x] = s / n;
			}

			// large-scale ground level: vanilla hills are replaced by 3D noise, only the broad shape (oceans, raised areas) stays
			Level = new short[W];
			double mean = 0;
			for (int x = 0; x < W; x++)
				mean += Surf[x];
			mean /= W;
			for (int x = 0; x < W; x++) {
				float sum = 0;
				int n = 0;
				for (int k = -60; k <= 60; k += 3) {
					sum += Surf[Math.Clamp(x + k, 0, W - 1)];
					n++;
				}
				float smooth = sum / n;
				bool ocean = Sea[x] >= 0;
				Level[x] = (short)(ocean ? smooth : mean + (smooth - mean) * 0.45);
			}
		}

		public void Save(TagCompound tag)
		{
			tag["w"] = W;
			tag["h"] = H;
			tag["surf"] = ToBytes(Surf);
			tag["sea"] = ToBytes(Sea);
			tag["biomeS"] = BiomeS;
			tag["biomeU"] = BiomeU;
			tag["ores"] = new int[] { Copper, Iron, Silver, Gold, DungeonBrick };
			using var ms = new MemoryStream();
			using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true))
				gz.Write(Mask, 0, Mask.Length);
			tag["mask"] = ms.ToArray();
			var cb = new byte[Comp.Length * 2];
			Buffer.BlockCopy(Comp, 0, cb, 0, cb.Length);
			using var ms2 = new MemoryStream();
			using (var gz = new GZipStream(ms2, CompressionLevel.Fastest, true))
				gz.Write(cb, 0, cb.Length);
			tag["comp"] = ms2.ToArray();
			var list = new List<int>();
			foreach (var st in Structures) {
				list.Add(st.Type); list.Add(st.X0); list.Add(st.X1); list.Add(st.Y0); list.Add(st.Y1);
			}
			tag["structs"] = list.ToArray();
		}

		public static WorldPlan Load(TagCompound tag)
		{
			if (tag == null || !tag.ContainsKey("w") || tag.GetInt("w") != Main.maxTilesX || tag.GetInt("h") != Main.maxTilesY)
				return null;
			int W = Main.maxTilesX, H = Main.maxTilesY;
			var p = new WorldPlan { W = W, H = H, GW = (W + Grid - 1) / Grid, GH = (H + Grid - 1) / Grid };
			p.Surf = FromBytes(tag.GetByteArray("surf"), p.W);
			p.Sea = FromBytes(tag.GetByteArray("sea"), p.W);
			p.BiomeS = tag.GetByteArray("biomeS");
			p.BiomeU = tag.GetByteArray("biomeU");
			int[] o = tag.GetIntArray("ores");
			if (o.Length == 5) {
				p.Copper = (ushort)o[0]; p.Iron = (ushort)o[1]; p.Silver = (ushort)o[2]; p.Gold = (ushort)o[3]; p.DungeonBrick = (ushort)o[4];
			}
			if (p.BiomeS.Length != p.W || p.BiomeU.Length != p.GW * p.GH || !tag.ContainsKey("mask") || !tag.ContainsKey("comp"))
				return null;
			p.Mask = new byte[p.W * p.H];
			using (var gz = new GZipStream(new MemoryStream(tag.GetByteArray("mask")), CompressionMode.Decompress))
				gz.ReadExactly(p.Mask, 0, p.Mask.Length);
			var cb = new byte[p.W * p.H * 2];
			using (var gz = new GZipStream(new MemoryStream(tag.GetByteArray("comp")), CompressionMode.Decompress))
				gz.ReadExactly(cb, 0, cb.Length);
			p.Comp = new ushort[p.W * p.H];
			Buffer.BlockCopy(cb, 0, p.Comp, 0, cb.Length);
			int[] sl = tag.GetIntArray("structs");
			for (int i = 0; i + 4 < sl.Length; i += 5)
				p.Structures.Add(new Structure { Id = (ushort)(i / 5 + 1), Type = (byte)sl[i], X0 = sl[i + 1], X1 = sl[i + 2], Y0 = sl[i + 3], Y1 = sl[i + 4] });
			p.FinishStructures();
			p.ComputeAmp();
			return p;
		}

		private static byte[] ToBytes(short[] a)
		{
			var b = new byte[a.Length * 2];
			Buffer.BlockCopy(a, 0, b, 0, b.Length);
			return b;
		}

		private static short[] FromBytes(byte[] b, int n)
		{
			var a = new short[n];
			Buffer.BlockCopy(b, 0, a, 0, Math.Min(b.Length, n * 2));
			return a;
		}
	}
}
