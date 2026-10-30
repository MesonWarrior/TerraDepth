using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraDepth.Rendering
{
	/// <summary>Resolves Terraria spritesheets and frames for 3D faces. Main thread only.</summary>
	public static class TileTextures
	{
		private static Texture2D white;
		private static readonly Dictionary<int, Texture2D> grassTops = new();

		public static Texture2D White
		{
			get
			{
				if (white == null || white.IsDisposed) {
					white = new Texture2D(Main.instance.GraphicsDevice, 1, 1);
					white.SetData(new[] { Color.White });
				}
				return white;
			}
		}

		public static Texture2D Tile(int type)
		{
			if (type < 0 || type >= TextureAssets.Tile.Length)
				return null;
			Main.instance.LoadTiles(type);
			var a = TextureAssets.Tile[type];
			return a?.Value;
		}

		public static Texture2D Wall(int type)
		{
			if (type <= 0 || type >= TextureAssets.Wall.Length)
				return null;
			Main.instance.LoadWall(type);
			return TextureAssets.Wall[type]?.Value;
		}

		public static Texture2D TreeTop(int style)
		{
			if (style < 0 || style >= TextureAssets.TreeTop.Length || TextureAssets.TreeTop[style] == null)
				style = 0;
			return TextureAssets.TreeTop[style].Value;
		}

		public static Texture2D TreeBranch(int style)
		{
			if (style < 0 || style >= TextureAssets.TreeBranch.Length || TextureAssets.TreeBranch[style] == null)
				style = 0;
			return TextureAssets.TreeBranch[style].Value;
		}

		public static bool IsGrass(int type) => type < TileLoader.TileCount && (TileID.Sets.Grass[type] || TileID.Sets.SpreadOverground[type] && type != TileID.Mud && type != TileID.Dirt);

		/// <summary>Grass tiles with a hand-drawn top in the GrassTextures folder.</summary>
		private static readonly Dictionary<int, string> CustomTopNames = new() {
			[TileID.Grass] = "2_Grass",
			[TileID.CorruptGrass] = "23_CorruptGrass",
			[TileID.JungleGrass] = "60_JungleGrass",
			[TileID.MushroomGrass] = "70_MushroomGrass",
			[TileID.HallowedGrass] = "109_HallowedGrass",
			[TileID.CrimsonGrass] = "199_CrimsonGrass",
			[TileID.GolfGrass] = "477_GolfGrass",
			[TileID.GolfGrassHallowed] = "492_GolfGrassHallowed",
			[TileID.AshGrass] = "633_AshGrass",
			[TileID.CorruptJungleGrass] = "661_CorruptJungleGrass",
			[TileID.CrimsonJungleGrass] = "662_CrimsonJungleGrass",
		};

		/// <summary>
		/// Top texture for grass blocks: the hand-drawn one from GrassTextures if there is one, otherwise made of the
		/// two body colours of the grass band on the block's side.
		/// </summary>
		public static Texture2D GrassTop(int type)
		{
			if (grassTops.TryGetValue(type, out var tex) && tex != null && !tex.IsDisposed)
				return tex;
			// hand-drawn top from GrassTextures/<id>_<name>.png, packed as Assets/GrassTop (stretched over the face without smoothing)
			if (CustomTopNames.TryGetValue(type, out string file)
				&& ModContent.RequestIfExists("TerraDepth/Assets/GrassTop/" + file, out ReLogic.Content.Asset<Texture2D> custom, ReLogic.Content.AssetRequestMode.ImmediateLoad)) {
				grassTops[type] = custom.Value;
				return custom.Value;
			}
			Texture2D sheet = Tile(type);
			if (sheet == null)
				return White;
			// The two body colours of the grass band on a block's side (the "top open" frames, rows 0-12):
			// the lighter one is the main colour of the top, the darker one the secondary colour.
			var px = new List<Color>();
			var top = new List<Color>();
			try {
				var buf = new Color[16 * 12];
				for (int f = 0; f < 3; f++) {
					var rect = new Rectangle(18 + 18 * f, 0, 16, 12);
					if (rect.Right > sheet.Width || rect.Bottom > sheet.Height)
						continue;
					sheet.GetData(0, rect, buf, 0, buf.Length);
					for (int i = 0; i < buf.Length; i++) {
						if (buf[i].A <= 240)
							continue;
						px.Add(buf[i]);
						if (i / 16 < 4)
							top.Add(buf[i]);
					}
				}
			}
			catch {
				// some sheets cannot be read back
			}
			Color main = new(40, 150, 70), second = new(25, 100, 50);
			int mainN = 3, secondN = 1;
			if (px.Count > 0) {
				// hue of the grass: the most common colour among the top rows (grass only, no soil there)
				Color refColor = MostCommon(top.Count > 0 ? top : px, _ => true);
				float refHue = Main.rgbToHsl(refColor).X;
				bool Grassy(Color c)
				{
					Vector3 hsl = Main.rgbToHsl(c);
					float dh = Math.Abs(hsl.X - refHue);
					return Math.Min(dh, 1f - dh) * 360f <= 30f && hsl.Y > 0.15f;
				}
				var counts = new Dictionary<uint, (Color c, int n)>();
				foreach (var c in px)
					if (Grassy(c))
						counts[c.PackedValue] = counts.TryGetValue(c.PackedValue, out var e) ? (c, e.n + 1) : (c, 1);
				var ranked = new List<(Color c, int n)>(counts.Values);
				ranked.Sort((x, y) => y.n.CompareTo(x.n));
				if (ranked.Count >= 2) {
					static float Bright(Color c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
					var (a1, n1) = ranked[0];
					var (a2, n2) = ranked[1];
					bool firstLighter = Bright(a1) >= Bright(a2);
					main = firstLighter ? a1 : a2;
					second = firstLighter ? a2 : a1;
					mainN = firstLighter ? n1 : n2;
					secondN = firstLighter ? n2 : n1;
				}
				else if (ranked.Count == 1) {
					main = ranked[0].c;
					second = Color.Lerp(main, Color.Black, 0.3f);
				}
			}
			float pMain = mainN / (float)(mainN + secondN);
			// 8x8 art pixels (2x2 texels each), like the vanilla sprites
			var rng = new Random(type * 7919);
			var data = new Color[16 * 16];
			for (int ay = 0; ay < 8; ay++)
				for (int ax = 0; ax < 8; ax++) {
					Color c = rng.NextDouble() < pMain ? main : second;
					for (int k = 0; k < 4; k++)
						data[(ay * 2 + (k >> 1)) * 16 + ax * 2 + (k & 1)] = c;
				}
			tex = new Texture2D(Main.instance.GraphicsDevice, 16, 16);
			tex.SetData(data);
			grassTops[type] = tex;
			return tex;
		}

		private static Color MostCommon(List<Color> list, Func<Color, bool> filter)
		{
			var counts = new Dictionary<uint, int>();
			Color best = list[0];
			int bestN = 0;
			foreach (var c in list) {
				if (!filter(c))
					continue;
				int n = counts.TryGetValue(c.PackedValue, out int k) ? k + 1 : 1;
				counts[c.PackedValue] = n;
				if (n > bestN) {
					bestN = n;
					best = c;
				}
			}
			return best;
		}

		public static void Unload()
		{
			white?.Dispose();
			white = null;
			foreach (var (type, t) in grassTops)
				if (!CustomTopNames.ContainsKey(type))
					t?.Dispose();
			grassTops.Clear();
		}
	}
}
