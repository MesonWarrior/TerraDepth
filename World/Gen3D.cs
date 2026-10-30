using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraDepth.World
{
	/// <summary>
	/// Procedural 3D world generator. Deterministic from the seed and the <see cref="WorldPlan"/>, so nothing but player
	/// changes has to be saved.
	///
	/// Layout:
	///  * Terrain is a 3D density field: a 2D noise heightmap over X/Z plus 3D noise near the surface (cliffs, overhangs,
	///    arches), mountains in snow, dunes in deserts, chasms in the corruption.
	///  * Caves are 3D: spaghetti tunnels, caverns, aquifers of water and lava; ores and gems are 3D veins.
	///  * Biomes are regions on the X/Z plane: vanilla order along X, borders warped strongly over Z.
	///  * Only vanilla structures and objects (dungeon, temple, islands, cabins, hives, chests, orbs, altars, crystals)
	///    are taken from the vanilla world, see <see cref="WorldPlan.Mask"/>. Structures get 3D halls ±10 slices deep.
	/// </summary>
	public static class Gen3D
	{
		public const byte KindGen = 0, KindBase = 1;

		public sealed class GenChunk
		{
			public VTile[] Cells; // null when the whole chunk is generated air
			public byte[] Kinds;  // null when every cell is generated
			public int Stamp;
		}

		private struct Column
		{
			public int H;          // heightmap row
			public int A;          // overhang amplitude (rows of 3D noise around H)
			public int Top;        // first solid row from above
			public byte BiomeS;
			public int Sea;
			public int DirtBottom;
			public bool Entrance;
			public bool SurfaceCarved;
			public int TreeTop;    // first tree row, int.MaxValue when no tree
			public bool Plant;
			public int Dz;
			public bool Island;
			public int IslandCenter;
		}

		private static WorldPlan plan;
		private static Perlin nHeight, nLarge, nCaveA, nCaveB, nCavern, nOre, nMisc, nIsland, nWarp, nBorder, nDensity;
		private static int seed, W, H, D, ZC, underworld, rock, surface, lavaLine;
		private static readonly Dictionary<int, GenChunk> cache = new();
		private static readonly object cacheLock = new();
		private static int stamp;
		private const int CacheMax = 2048;

		public static bool Ready => plan != null;
		public static WorldPlan Plan => plan;

		public static void Init(WorldPlan p, int worldSeed, int depth, int zc)
		{
			plan = p;
			seed = worldSeed;
			W = Main.maxTilesX;
			H = Main.maxTilesY;
			D = depth;
			ZC = zc;
			underworld = H - 200;
			rock = (int)Main.rockLayer;
			surface = (int)Main.worldSurface;
			lavaLine = rock + (int)((underworld - rock) * 0.55);
			nHeight = new Perlin(seed ^ 0x11);
			nLarge = new Perlin(seed ^ 0x22);
			nCaveA = new Perlin(seed ^ 0x33);
			nCaveB = new Perlin(seed ^ 0x44);
			nCavern = new Perlin(seed ^ 0x55);
			nOre = new Perlin(seed ^ 0x66);
			nMisc = new Perlin(seed ^ 0x77);
			nIsland = new Perlin(seed ^ 0x88);
			nWarp = new Perlin(seed ^ 0x99);
			nDensity = new Perlin(seed ^ 0xBB);
			BuildStructureIndex();
			nBorder = new Perlin(seed ^ 0xAA);
			Clear();
		}

		public static void Reset()
		{
			plan = null;
			Clear();
		}

		public static void Clear()
		{
			lock (cacheLock)
				cache.Clear();
		}

		private static uint Hash(int a, int b, int c)
		{
			unchecked {
				uint h = (uint)seed * 0x9E3779B1u;
				h ^= (uint)a * 0x85EBCA6Bu; h = (h << 13) | (h >> 19);
				h ^= (uint)b * 0xC2B2AE35u; h = (h << 15) | (h >> 17);
				h ^= (uint)c * 0x27D4EB2Fu;
				h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
				return h;
			}
		}

		private static float Hash01(int a, int b, int c) => (Hash(a, b, c) & 0xFFFFFF) / 16777216f;

		private static float Smooth(float t)
		{
			t = Math.Clamp(t, 0f, 1f);
			return t * t * (3 - 2 * t);
		}

		// ------------------------------------------------------------------ columns

		/// <summary>Biome regions: the vanilla order along X, warped by large 2D noise so borders curve across depth.</summary>
		private static int BiomeAt(int x, int z)
		{
			float edge = Math.Clamp(Math.Min(x, W - 1 - x) / 350f, 0f, 1f); // oceans stay at the world edges
			double w = nWarp.Fbm(x * 0.0021, z * 0.0035, 0.17, 3) * 170 + nBorder.Noise(x * 0.02, z * 0.02, 0.6) * 14;
			return plan.BiomeS[Math.Clamp(x + (int)(w * edge), 0, W - 1)];
		}

		private static int OverhangAmp(byte b) => b switch {
			Biome.Snow => 12, Biome.Jungle => 8, Biome.Corrupt => 7, Biome.Crimson => 7, Biome.Normal => 6, Biome.Hallow => 6,
			Biome.Mushroom => 4, Biome.Desert => 2, _ => 0
		};

		private static float HillAmp(byte b) => b switch {
			Biome.Snow => 30f, Biome.Jungle => 22f, Biome.Desert => 9f, Biome.Corrupt => 20f, Biome.Crimson => 20f,
			Biome.Mushroom => 10f, Biome.Ocean => 0f, _ => 17f
		};

		/// <summary>Surface density: solid when positive.</summary>
		private static bool SurfaceSolid(int x, int y, int z, in Column c)
		{
			if (y < c.H - c.A - 1)
				return false;
			if (y > c.H + c.A + 1)
				return true;
			if (c.A == 0)
				return y >= c.H;
			double n = nDensity.Fbm(x * 0.035, y * 0.05, z * 0.035, 2);
			return (y - c.H) + n * c.A * 1.6 > 0;
		}

		private static Column MakeColumn(int x, int z)
		{
			Column c = default;
			c.Dz = Math.Abs(z - ZC);
			c.BiomeS = (byte)BiomeAt(x, z);
			c.Sea = plan.Sea[x];
			// hill amplitude blended over neighbouring biomes so regions don't end in steps
			float amp = 0f;
			for (int k = -2; k <= 2; k++)
				amp += HillAmp((byte)BiomeAt(x + k * 18, z + k * 11));
			amp /= 5f;
			float v = 0f;
			if (c.BiomeS != Biome.Ocean) {
				v = (float)nHeight.Fbm(x * 0.0055, z * 0.0055, 0.31, 4) * amp;
				v += (float)nLarge.Fbm(x * 0.0014, z * 0.0021, 0.73, 2) * 26f;
				if (c.BiomeS == Biome.Snow) {
					float r = 1f - Math.Abs((float)nLarge.Noise(x * 0.009, z * 0.009, 4.2));
					v += r * r * amp * 1.3f;
				}
				else if (c.BiomeS == Biome.Desert)
					v += Math.Abs((float)nMisc.Noise(x * 0.03, z * 0.012, 9.1)) * 7f;
				// fade hills out toward the shore
				if (c.Sea < 0) {
					float shore = Math.Clamp(Math.Min(x, W - 1 - x) / 500f, 0f, 1f);
					v *= 0.35f + 0.65f * shore;
				}
			}
			int level = plan.Level[x];
			c.H = Math.Clamp(level - (int)Math.Round(v), 60, underworld - 60);
			c.A = OverhangAmp(c.BiomeS);
			if (c.Sea >= 0)
				c.A = 0;
			c.Top = c.H;
			for (int y = c.H - c.A - 1; y <= c.H + c.A + 1; y++)
				if (SurfaceSolid(x, y, z, c)) {
					c.Top = y;
					break;
				}
			c.DirtBottom = Math.Max(c.Top + 8, rock + (int)(nMisc.Noise(x * 0.02, z * 0.02, 3.3) * 14));
			c.Entrance = nMisc.Noise(x * 0.012, z * 0.012, 7.7) > 0.42;
			c.SurfaceCarved = CaveOpen(x, c.Top, z, 0, c.Entrance) || Chasm(x, c.Top, z, c);
			c.TreeTop = int.MaxValue;
			if (!c.SurfaceCarved && c.Sea < 0 && GrassBiome(c.BiomeS)) {
				// one tree candidate per 6x6 cell, jittered; density varies like forests and clearings
				int gx = x / 6, gz = z / 6;
				uint h = Hash(gx, gz, 1);
				int tx = gx * 6 + (int)(h % 6), tz = gz * 6 + (int)((h >> 8) % 6);
				float forest = 0.5f + 0.5f * (float)nMisc.Noise(x * 0.008, z * 0.008, 55.0);
				float chance = c.BiomeS switch { Biome.Jungle => 0.85f, Biome.Snow => 0.45f, Biome.Mushroom => 0.0f, _ => 0.75f } * forest;
				if (tx == x && tz == z && ((h >> 16) & 255) < chance * 255) {
					int th = 7 + (int)((h >> 24) % 9) + (c.BiomeS == Biome.Jungle ? 6 : 0);
					c.TreeTop = c.Top - th;
				}
				else
					c.Plant = Hash01(x, z, 2) < 0.3f;
			}
			c.Island = c.Sea < 0 && nIsland.Noise(x * 0.0045, z * 0.0045, 0.5) > 0.38;
			c.IslandCenter = (int)(surface * 0.33) + (int)(nIsland.Noise(x * 0.01, z * 0.01, 2.5) * 18);
			return c;
		}

		private static bool GrassBiome(byte b) => b is Biome.Normal or Biome.Jungle or Biome.Corrupt or Biome.Crimson or Biome.Hallow or Biome.Snow or Biome.Mushroom;

		// ------------------------------------------------------------------ caves

		private static bool CaveOpen(int x, int y, int z, int d, bool entrance)
		{
			if (y >= underworld - 8)
				return false;
			if (d < 3 && !(entrance && d >= 0))
				return false;
			float depthF = Math.Clamp((y - surface) / 400f, 0f, 1f);
			double a = nCaveA.Noise(x * 0.019, y * 0.024, z * 0.019);
			double b = nCaveB.Noise(x * 0.019, y * 0.024, z * 0.019);
			double r = 0.065 + 0.045 * depthF;
			if (a * a + b * b < r * r)
				return true;
			if (y > rock) {
				double c = nCavern.Fbm(x * 0.012, y * 0.017, z * 0.012, 2);
				if (c > 0.33 - 0.09 * depthF)
					return true;
			}
			else if (d > 12 && d < 90) {
				// small pockets in the dirt layer
				if (nCavern.Noise(x * 0.045, y * 0.06, z * 0.045) > 0.52)
					return true;
			}
			return false;
		}

		private static bool Chasm(int x, int y, int z, in Column c)
		{
			if (c.BiomeS != Biome.Corrupt)
				return false;
			int d = y - c.Top;
			if (d < -2 || d > 140)
				return false;
			double n = nMisc.Noise(x * 0.06, z * 0.06, 13.3);
			return n > 0.5 - d * 0.0012;
		}

		// ------------------------------------------------------------------ materials

		private static ushort Grass(byte b) => b switch {
			Biome.Jungle => TileID.JungleGrass, Biome.Corrupt => TileID.CorruptGrass, Biome.Crimson => TileID.CrimsonGrass,
			Biome.Hallow => TileID.HallowedGrass, Biome.Mushroom => TileID.MushroomGrass, Biome.Snow => TileID.SnowBlock,
			Biome.Desert or Biome.Ocean => TileID.Sand, _ => TileID.Grass
		};

		/// <summary>Biome that decides materials at this depth: surface biome near the top, underground regions below.</summary>
		private static byte LayerBiome(int x, int y, int z, int d, in Column c)
		{
			if (d < 14)
				return c.BiomeS;
			return d < 22 + (int)(nMisc.Noise(x * 0.05, y * 0.05, z * 0.05) * 8) ? c.BiomeS : Underground(x, y, z);
		}

		private static ushort Material(int x, int y, int z, int d, in Column c, byte b)
		{
			if (b == Biome.Ocean)
				b = c.BiomeS == Biome.Ocean && d < 30 ? Biome.Ocean : Biome.Normal;
			bool upper = y < c.DirtBottom;
			double patch = nMisc.Noise(x * 0.07, y * 0.07, z * 0.07 + 40.0);
			switch (b) {
				case Biome.Desert:
					if (d < 6) return TileID.Sand;
					return patch > 0.15 ? TileID.Sandstone : TileID.HardenedSand;
				case Biome.Ocean:
					return d < 25 ? TileID.Sand : TileID.Stone;
				case Biome.Snow:
					if (d == 0) return TileID.SnowBlock;
					return upper ? (patch > 0.35 ? TileID.IceBlock : TileID.SnowBlock) : (patch > -0.2 ? TileID.IceBlock : TileID.SnowBlock);
				case Biome.Jungle:
					if (d == 0) return TileID.JungleGrass;
					return !upper && patch > 0.4 ? TileID.Stone : TileID.Mud;
				case Biome.Mushroom:
					return d == 0 ? TileID.MushroomGrass : TileID.Mud;
				case Biome.Corrupt:
					if (d == 0) return TileID.CorruptGrass;
					return upper ? (patch > 0.3 ? TileID.Ebonstone : TileID.Dirt) : TileID.Ebonstone;
				case Biome.Crimson:
					if (d == 0) return TileID.CrimsonGrass;
					return upper ? (patch > 0.3 ? TileID.Crimstone : TileID.Dirt) : TileID.Crimstone;
				case Biome.Hallow:
					if (d == 0) return TileID.HallowedGrass;
					return upper ? (patch > 0.3 ? TileID.Pearlstone : TileID.Dirt) : TileID.Pearlstone;
				case Biome.Granite:
					return TileID.Granite;
				case Biome.Marble:
					return TileID.Marble;
			}
			if (d == 0)
				return TileID.Grass;
			if (upper) {
				if (d < 45 && nOre.Noise(x * 0.09, y * 0.09, z * 0.09 + 77.0) > 0.55)
					return TileID.ClayBlock;
				return patch > 0.42 ? TileID.Stone : TileID.Dirt;
			}
			if (patch > 0.5)
				return TileID.Dirt;
			if (y > rock + 60 && nOre.Noise(x * 0.08, y * 0.08, z * 0.08 + 133.0) > 0.58)
				return TileID.Silt;
			return TileID.Stone;
		}

		/// <summary>Underground biome regions, warped in all three axes so they are volumes, not stripes.</summary>
		private static byte Underground(int x, int y, int z)
		{
			double wx = nWarp.Fbm(x * 0.004, y * 0.005, z * 0.006 + 3.1, 2) * 90;
			double wy = nWarp.Noise(x * 0.006, y * 0.006, z * 0.008 + 9.7) * 25;
			return plan.Underground(x + (int)wx, y + (int)wy);
		}

		private static bool OreHost(ushort t) => t is TileID.Stone or TileID.Dirt or TileID.Mud or TileID.Ebonstone or TileID.Crimstone
			or TileID.Pearlstone or TileID.IceBlock or TileID.SnowBlock or TileID.Granite or TileID.Marble or TileID.ClayBlock;

		private static ushort Ore(int x, int y, int z, ushort host)
		{
			if (!OreHost(host))
				return host;
			if (nOre.Noise(x * 0.17, y * 0.17, z * 0.17) < 0.6)
				return host;
			int r = (int)(Hash(x >> 2, y >> 2, z >> 2) & 255);
			if (y < surface)
				return r < 215 ? plan.Copper : plan.Iron;
			if (y < rock)
				return r < 100 ? plan.Copper : r < 215 ? plan.Iron : plan.Silver;
			if (r < 60) return plan.Iron;
			if (r < 160) return plan.Silver;
			return plan.Gold;
		}

		private static ushort Gem(int x, int y, int z)
		{
			uint h = Hash(x, y, z ^ 0x5151);
			if ((h & 2047) > 2)
				return 0;
			return ((h >> 12) % 6) switch {
				0 => TileID.Amethyst, 1 => TileID.Topaz, 2 => TileID.Sapphire, 3 => TileID.Emerald, 4 => TileID.Ruby, _ => TileID.Diamond
			};
		}

		private static ushort CaveWall(int x, int y, in Column c, byte b)
		{
			return b switch {
				Biome.Jungle => WallID.MudUnsafe, Biome.Snow => y > rock ? WallID.IceUnsafe : WallID.SnowWallUnsafe,
				Biome.Desert => y > rock ? WallID.Sandstone : WallID.HardenedSand, Biome.Corrupt => WallID.EbonstoneUnsafe,
				Biome.Crimson => WallID.CrimstoneUnsafe, Biome.Mushroom => WallID.MushroomUnsafe,
				Biome.Granite => WallID.GraniteUnsafe, Biome.Marble => WallID.MarbleUnsafe,
				_ => y < c.DirtBottom ? WallID.DirtUnsafe : WallID.Stone
			};
		}

		private static void Block(ref VTile v, ushort type, int x, int y, int z)
		{
			v.SetBlock(type);
			int k = (int)(Hash(x, y, z) % 3);
			v.State.TileFrameX = (short)(18 + 18 * k);
		}

		// ------------------------------------------------------------------ cells

		private static VTile GenCellBase(int x, int y, int z, in Column c)
		{
			VTile v = default;
			if (y >= underworld)
				return Underworld(x, y, z);
			int d = y - c.Top;
			bool solid = SurfaceSolid(x, y, z, c);
			if (!solid) {
				if (c.Sea >= 0 && y >= c.Sea) {
					v.Liquid.Amount = 255;
					v.Liquid.LiquidType = LiquidID.Water;
					return v;
				}
				if (y < c.Top) {
					if (c.TreeTop != int.MaxValue && y >= c.TreeTop) {
						v.Type.Type = TileID.Trees;
						v.State.HasTile = true;
						if (y == c.TreeTop) {
							v.State.TileFrameX = 22;
							v.State.TileFrameY = (short)(198 + 22 * (int)(Hash(x, z, 3) % 3));
						}
						else {
							v.State.TileFrameX = 0;
							v.State.TileFrameY = (short)(22 * (int)(Hash(x, y, z) % 3));
						}
						return v;
					}
					if (y == c.Top - 1 && c.Plant)
						return Plant(x, z, c.BiomeS);
					if (c.Island && Math.Abs(y - c.IslandCenter) < 22)
						return Island(x, y, z, c);
				}
				return v; // air: open sky or under an overhang
			}
			if (CaveOpen(x, y, z, d, c.Entrance) || Chasm(x, y, z, c)) {
				if (d > 10)
					v.Wall.Type = CaveWall(x, y, c, LayerBiome(x, y, z, d, c));
				if (y > rock && y < underworld - 15) {
					uint h = Hash(x >> 6, 0x4AA, z >> 6);
					int level = rock + (int)((h >> 8) & 255) + 20;
					if ((h & 255) < 75 && y >= level) {
						v.Liquid.Amount = 255;
						v.Liquid.LiquidType = y > lavaLine && ((h >> 16) & 1) == 0 ? LiquidID.Lava : LiquidID.Water;
					}
				}
				return v;
			}
			// exposed top of the 3D surface gets grass, also on ledges under overhangs
			bool exposed = d <= c.A * 2 + 2 && !SurfaceSolid(x, y - 1, z, c);
			byte lb = LayerBiome(x, y, z, d, c);
			ushort t = Material(x, y, z, exposed ? 0 : Math.Max(1, d), c, lb);
			if (y > rock && t == TileID.Stone) {
				ushort g = Gem(x, y, z);
				if (g != 0)
					t = g;
			}
			t = Ore(x, y, z, t);
			Block(ref v, t, x, y, z);
			if (d > 2)
				v.Wall.Type = CaveWall(x, y, c, lb);
			return v;
		}

		/// <summary>A generated cell, with pots and life crystals standing on cave floors.</summary>
		private static VTile GenCell(int x, int y, int z, in Column c)
		{
			VTile v = GenCellBase(x, y, z, c);
			if (!v.HasTile && v.LiquidAmount == 0 && y > c.Top + 15 && y < H - 6)
				FloorObject(ref v, x, y, z, c);
			return v;
		}

		// ------------------------------------------------------------------ cave floor objects (vanilla WorldGen.PlacePot / AddLifeCrystal rules)

		private const float PotChance = 0.07f, CrystalChance = 0.0045f;

		private static bool OpenCell(int x, int y, int z, in Column c)
		{
			VTile v = GenCellBase(x, y, z, c);
			return !v.HasTile && v.LiquidAmount == 0;
		}

		/// <summary>
		/// 2x2 objects anchored by their bottom-left cell (ax, ay) with ax even. They need open space and a solid,
		/// full floor under both columns, like vanilla's PlacePot and AddLifeCrystal.
		/// </summary>
		private static void FloorObject(ref VTile v, int x, int y, int z, in Column c)
		{
			int ax = x & ~1;
			if (ax + 1 >= W)
				return;
			for (int row = 0; row < 2; row++) {
				int ay = y + row; // row 0: this cell is the bottom row, row 1: the top row
				uint h = Hash(ax >> 1, ay, z ^ 0x50F);
				float r = (h & 0xFFFFFF) / 16777216f;
				bool crystal = r < CrystalChance && ay > rock;
				bool pot = !crystal && r < PotChance + CrystalChance;
				if (!pot && !crystal)
					continue;
				if (ay >= underworld && crystal)
					continue;
				Column c2 = MakeColumn(x == ax ? ax + 1 : ax, z);
				Column cl = x == ax ? c : c2, cr = x == ax ? c2 : c;
				if (!OpenCell(ax, ay, z, cl) || !OpenCell(ax + 1, ay, z, cr) || !OpenCell(ax, ay - 1, z, cl) || !OpenCell(ax + 1, ay - 1, z, cr))
					continue;
				VTile fl = GenCellBase(ax, ay + 1, z, cl), fr = GenCellBase(ax + 1, ay + 1, z, cr);
				if (!fl.IsCube || !fr.IsCube)
					continue;
				int k = x - ax;
				v.Type.Type = crystal ? TileID.Heart : TileID.Pots;
				v.State.HasTile = true;
				if (crystal) {
					v.State.TileFrameX = (short)(k * 18);
					v.State.TileFrameY = (short)((1 - row) * 18);
				}
				else {
					// both halves must agree, so the style comes from the left column only
					int style = PotStyle(fl.TileType, GenCellBase(ax, ay, z, cl).WallType, ay);
					int variant = (int)((h >> 24) % 3);
					v.State.TileFrameX = (short)(k * 18 + variant * 36);
					v.State.TileFrameY = (short)((1 - row) * 18 + style * 36);
				}
				return;
			}
		}

		/// <summary>Pot style by floor and wall, as in vanilla world generation.</summary>
		private static int PotStyle(ushort floor, ushort wall, int y)
		{
			int r = (int)(Hash(floor, wall, y) % 3);
			if (y > H - 200)
				return 13 + r;
			if (wall == WallID.Sandstone || wall == WallID.HardenedSand)
				return 34 + r;
			switch (floor) {
				case TileID.SnowBlock: case TileID.IceBlock: case TileID.CorruptIce: return 4 + r;
				case TileID.JungleGrass: case TileID.Mud: return 7 + r;
				case TileID.Demonite: case TileID.CorruptGrass: case TileID.Ebonstone: return 16 + r;
				case TileID.CrimsonGrass: case TileID.Crimstone: case TileID.Crimtane: case TileID.FleshIce: return 22 + r;
				case TileID.Marble: return 31 + r;
				case TileID.LihzahrdBrick: return 28 + r;
			}
			return (int)(Hash(floor, y, wall + 7) % 4);
		}

		private static VTile Plant(int x, int z, byte b)
		{
			VTile v = default;
			ushort t;
			int frames;
			switch (b) {
				case Biome.Jungle: t = TileID.JunglePlants; frames = 20; break;
				case Biome.Corrupt: t = TileID.CorruptPlants; frames = 20; break;
				case Biome.Crimson: t = TileID.CrimsonPlants; frames = 20; break;
				case Biome.Hallow: t = TileID.HallowedPlants; frames = 20; break;
				case Biome.Mushroom: t = TileID.MushroomPlants; frames = 5; break;
				case Biome.Normal: t = TileID.Plants; frames = 40; break;
				default: return v;
			}
			v.Type.Type = t;
			v.State.HasTile = true;
			v.State.TileFrameX = (short)(18 * (int)(Hash(x, z, 4) % (uint)frames));
			v.State.TileFrameY = 0;
			return v;
		}

		private static bool IslandSolid(int x, int y, int z, in Column c)
		{
			float dy = y - c.IslandCenter;
			// flat top, rounded bottom
			float shape = dy < 0 ? -dy * 0.16f : dy * 0.07f;
			double n = nIsland.Fbm(x * 0.03, y * 0.05, z * 0.03, 2) + (nIsland.Noise(x * 0.0045, z * 0.0045, 0.5) - 0.38) * 2.2;
			return n - shape > 0.18;
		}

		private static VTile Island(int x, int y, int z, in Column c)
		{
			VTile v = default;
			if (!IslandSolid(x, y, z, c))
				return v;
			bool top = !IslandSolid(x, y - 1, z, c);
			bool bottom = !IslandSolid(x, y + 3, z, c);
			Block(ref v, top ? TileID.Grass : bottom ? TileID.Cloud : TileID.Dirt, x, y, z);
			return v;
		}

		private static VTile Underworld(int x, int y, int z)
		{
			VTile v = default;
			int u = y - underworld;
			int ceil = 28 + (int)(nMisc.Noise(x * 0.02, z * 0.02, 21.0) * 12);
			int floor = 200 - (48 + (int)(nMisc.Noise(x * 0.02, z * 0.02, 37.0) * 12));
			bool solid;
			if (u < ceil || u > floor || u >= 194)
				solid = true;
			else
				solid = nCavern.Fbm(x * 0.022, y * 0.035, z * 0.022, 2) > 0.38;
			if (solid) {
				ushort t = u < 6 && Hash01(x, y, z) < (6 - u) / 6f ? TileID.Stone : TileID.Ash;
				if (t == TileID.Ash && nOre.Noise(x * 0.15, y * 0.15, z * 0.15 + 500.0) > 0.55)
					t = TileID.Hellstone;
				Block(ref v, t, x, y, z);
				return v;
			}
			if (u > 200 - 78) {
				v.Liquid.Amount = 255;
				v.Liquid.LiquidType = LiquidID.Lava;
			}
			return v;
		}

		// ------------------------------------------------------------------ vanilla core

		/// <summary>Vanilla cell copied into a non-center slice: blocks and platforms stay, objects only exist in the middle slice.</summary>
		public static VTile StripBase(VTile b, int dz)
		{
			if (dz == 0 || !b.HasTile)
				return b;
			ushort t = b.TileType;
			if (t < TileLoader.TileCount && (b.IsCube || TileID.Sets.Platforms[t]))
				return b;
			b.ClearTile();
			return b;
		}

		// ------------------------------------------------------------------ 3D structures

		private const int Bucket = 64;
		private static List<WorldPlan.Structure>[] structBuckets;

		private static void BuildStructureIndex()
		{
			structBuckets = new List<WorldPlan.Structure>[W / Bucket + 1];
			foreach (var st in plan.Structures) {
				if (st.Type == 0)
					continue;
				int a, b;
				if (st.Type == WorldPlan.StructType.Dungeon) {
					a = st.X0; b = st.X1;
				}
				else {
					int r = RevolveRadius(st);
					a = st.Cx - r; b = st.Cx + r;
				}
				for (int k = Math.Max(0, a / Bucket); k <= Math.Min(structBuckets.Length - 1, b / Bucket); k++)
					(structBuckets[k] ??= new List<WorldPlan.Structure>()).Add(st);
			}
		}

		private static int RevolveRadius(WorldPlan.Structure st) => Math.Min(st.Radius, D / 2 - 2);

		/// <summary>
		/// 3D version of a vanilla structure at a cell outside the middle slice.
		/// Buildings and organic shapes are revolved around their vertical axis (square or round profile), so any slice
		/// through them shows the vanilla cross-section at that distance from the axis: cabins become rooms walled on all
		/// sides, pyramids square pyramids, living trees round trunks and crowns. The dungeon becomes a 3D maze.
		/// </summary>
		private static bool StructureCell(List<WorldPlan.Structure> list, int x, int y, int z, int dz, out VTile v)
		{
			v = default;
			foreach (var st in list) {
				if (y < st.Y0 - 1 || y > st.Y1 + 1)
					continue;
				if (st.Type == WorldPlan.StructType.Dungeon) {
					if (DungeonCell(st, x, y, z, dz, out v))
						return true;
					continue;
				}
				int R = RevolveRadius(st);
				if (dz > R)
					continue;
				int dx = x - st.Cx;
				int r = WorldPlan.StructType.Round(st.Type) ? (int)Math.Round(Math.Sqrt(dx * dx + dz * dz)) : Math.Max(Math.Abs(dx), dz);
				if (r > R)
					continue;
				int sx = st.Cx + (dx >= 0 ? r : -r);
				if (sx < st.X0 || sx > st.X1 || plan.CompAt(sx, y) != st.Id)
					continue;
				v = StripBase(Voxel.Base(sx, y), dz);
				return true;
			}
			return false;
		}

		private const int MazeX = 14, MazeY = 11, MazeZ = 14;

		private static bool MazeRoom(WorldPlan.Structure st, int i, int j, int k, out int hx, out int hy, out int hz)
		{
			uint h = Hash(i + st.Id * 7919, j, k ^ 0x3D1);
			hx = 3 + (int)((h >> 8) % 3);
			hy = 2 + (int)((h >> 12) % 2);
			hz = 3 + (int)((h >> 16) % 3);
			return (h & 255) < 165 || k == 0 && (h & 255) < 220; // the layer through the vanilla dungeon is denser, so it connects
		}

		private static bool MazeEdge(WorldPlan.Structure st, int i, int j, int k, int axis)
		{
			uint h = Hash(i * 3 + axis + st.Id * 131, j, k ^ 0x77E);
			int limit = axis == 1 ? 90 : 190;
			return (h & 255) < limit;
		}

		private static int NodeX(WorldPlan.Structure st, int i) => st.X0 + i * MazeX + MazeX / 2;
		private static int NodeY(WorldPlan.Structure st, int j) => st.Y0 + j * MazeY + MazeY / 2;
		private static int NodeZ(int k) => ZC + k * MazeZ;

		private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

		/// <summary>Open space of the dungeon maze (rooms, corridors along X and Z, shafts along Y). platform: shaft ledge.</summary>
		private static bool MazeOpen(WorldPlan.Structure st, int x, int y, int z, out bool platform)
		{
			platform = false;
			int i = FloorDiv(x - st.X0, MazeX), j = FloorDiv(y - st.Y0, MazeY), k = FloorDiv(z - ZC + MazeZ / 2, MazeZ);
			int cx = NodeX(st, i), cy = NodeY(st, j), cz = NodeZ(k);
			bool here = MazeRoom(st, i, j, k, out int hx, out int hy, out int hz);
			if (here && Math.Abs(x - cx) <= hx && Math.Abs(y - cy) <= hy && Math.Abs(z - cz) <= hz)
				return true;
			// corridors along X (3 wide, 4 tall, on the room floor line)
			if (Math.Abs(z - cz) <= 1 && y >= cy - 1 && y <= cy + 2) {
				int ia = x >= cx ? i : i - 1;
				if (MazeRoom(st, ia, j, k, out _, out _, out _) && MazeRoom(st, ia + 1, j, k, out _, out _, out _) && MazeEdge(st, ia, j, k, 0))
					return true;
			}
			// corridors along Z
			if (Math.Abs(x - cx) <= 1 && y >= cy - 1 && y <= cy + 2) {
				int ka = z >= cz ? k : k - 1;
				if (MazeRoom(st, i, j, ka, out _, out _, out _) && MazeRoom(st, i, j, ka + 1, out _, out _, out _) && MazeEdge(st, i, j, ka, 2))
					return true;
			}
			// shafts along Y with platform ledges
			if (Math.Abs(x - cx) <= 1 && Math.Abs(z - cz) <= 1) {
				int ja = y >= cy ? j : j - 1;
				if (MazeRoom(st, i, ja, k, out _, out _, out _) && MazeRoom(st, i, ja + 1, k, out _, out _, out _) && MazeEdge(st, i, ja, k, 1)) {
					platform = (y - NodeY(st, ja)) % 4 == 0 && x == cx;
					return true;
				}
			}
			return false;
		}

		private static ushort DungeonWall => plan.DungeonBrick == TileID.GreenDungeonBrick ? WallID.GreenDungeonUnsafe
			: plan.DungeonBrick == TileID.PinkDungeonBrick ? WallID.PinkDungeonUnsafe : WallID.BlueDungeonUnsafe;

		private static bool DungeonCell(WorldPlan.Structure st, int x, int y, int z, int dz, out VTile v)
		{
			v = default;
			if (x < st.X0 || x > st.X1 || st.ColTop == null)
				return false;
			int col = x - st.X0;
			int top = st.ColTop[col], bottom = st.ColBottom[col];
			if (bottom < 0 || y < top - 1 || y > bottom + 1)
				return false;
			// egg-shaped volume around the vanilla dungeon: deep in the middle, thinner toward its ends
			float hw = Math.Max(4f, (st.X1 - st.X0) / 2f);
			float ex = (x - st.Cx) / hw;
			int rz = Math.Min(st.Radius, D / 2 - 6);
			float zr = Math.Max(6f, rz * MathF.Sqrt(Math.Max(0f, 1f - ex * ex)));
			if (dz > zr)
				return false;
			bool inner = dz <= zr - 2 && y > top && y < bottom;
			if (inner && MazeOpen(st, x, y, z, out bool platform)) {
				v.Wall.Type = DungeonWall;
				if (platform) {
					v.Type.Type = TileID.Platforms;
					v.State.HasTile = true;
					v.State.TileFrameX = 0;
					v.State.TileFrameY = 0;
				}
				return true;
			}
			Block(ref v, plan.DungeonBrick, x, y, z);
			v.Wall.Type = DungeonWall;
			return true;
		}

		// ------------------------------------------------------------------ public access

		/// <summary>
		/// Fills cells [y0, y0+n) of column (x, z). kinds may be null; base cells are resolved either way.
		/// pure ignores the vanilla mask (used to build the center slice itself).
		/// </summary>
		public static void FillColumn(int x, int z, int y0, int n, VTile[] dst, int dstIndex, int stride, byte[] kinds, bool pure = false)
		{
			Column c = MakeColumn(x, z);
			List<WorldPlan.Structure> structs = pure || c.Dz == 0 ? null : structBuckets[Math.Clamp(x / Bucket, 0, structBuckets.Length - 1)];
			for (int i = 0; i < n; i++) {
				int y = y0 + i;
				int idx = dstIndex + i * stride;
				if ((uint)y >= (uint)H) {
					dst[idx] = default;
					if (kinds != null) kinds[idx] = KindGen;
					continue;
				}
				byte m = pure ? WorldPlan.MaskNone : plan.MaskAt(x, y);
				if (m != WorldPlan.MaskNone && (c.Dz == 0 || m == WorldPlan.MaskObject && c.Dz <= 2)) {
					// the vanilla slice itself, and small pockets around loose objects (chests, crystals, altars)
					dst[idx] = StripBase(Voxel.Base(x, y), c.Dz);
					if (kinds != null) kinds[idx] = KindBase;
					continue;
				}
				if (structs != null && StructureCell(structs, x, y, z, c.Dz, out VTile sv))
					dst[idx] = sv;
				else
					dst[idx] = GenCell(x, y, z, c);
				if (kinds != null) kinds[idx] = KindGen;
			}
		}

		public static GenChunk Chunk(int cx, int cy, int cz)
		{
			int key = Voxel.ChunkKey(cx, cy, cz);
			lock (cacheLock) {
				if (cache.TryGetValue(key, out var ch)) {
					ch.Stamp = ++stamp;
					return ch;
				}
			}
			var g = Build(cx, cy, cz);
			lock (cacheLock) {
				g.Stamp = ++stamp;
				cache[key] = g;
				if (cache.Count > CacheMax)
					Evict();
			}
			return g;
		}

		private static void Evict()
		{
			var list = new List<KeyValuePair<int, GenChunk>>(cache);
			list.Sort((a, b) => a.Value.Stamp.CompareTo(b.Value.Stamp));
			for (int i = 0; i < list.Count / 2; i++)
				cache.Remove(list[i].Key);
		}

		private static GenChunk Build(int cx, int cy, int cz)
		{
			var cells = new VTile[4096];
			var kinds = new byte[4096];
			for (int lz = 0; lz < 16; lz++)
				for (int lx = 0; lx < 16; lx++) {
					int x = cx * 16 + lx, z = cz * 16 + lz;
					int baseIdx = (lz * 16 + lx) * 16;
					if ((uint)x >= (uint)W || (uint)z >= (uint)D)
						continue;
					FillColumn(x, z, cy * 16, 16, cells, baseIdx, 1, kinds);
				}
			bool anyBase = false, anyGen = false;
			for (int i = 0; i < 4096; i++) {
				if (kinds[i] == KindBase)
					anyBase = true;
				else if (cells[i].HasTile || cells[i].LiquidAmount > 0 || cells[i].WallType > 0)
					anyGen = true;
			}
			return new GenChunk { Cells = anyGen || anyBase ? cells : null, Kinds = anyBase ? kinds : null };
		}

		/// <summary>Index inside a chunk for local coordinates.</summary>
		public static int Local(int lx, int ly, int lz) => (lz * 16 + lx) * 16 + ly;

		public static VTile Read(GenChunk ch, int x, int y, int z)
		{
			if (ch.Cells == null)
				return default;
			int i = Local(x & 15, y & 15, z & 15);
			if (ch.Kinds != null && ch.Kinds[i] == KindBase)
				return StripBase(Voxel.Base(x, y), Math.Abs(z - ZC));
			return ch.Cells[i];
		}
	}
}
