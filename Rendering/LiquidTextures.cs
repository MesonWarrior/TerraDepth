using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;

namespace TerraDepth.Rendering
{
	/// <summary>
	/// Vanilla liquid textures (Images/Misc/water_N), animated like vanilla's LiquidRenderer. Each water_N sheet holds 16
	/// animation frames of 48x80 px stacked vertically; the current frame of each liquid is copied every frame into a small
	/// target the chunk meshes sample, so the meshes never need new UVs for the animation.
	/// Frame layout (vanilla FrameOffset): (16, 0) is a cell with a surface on top, (16, 48) a cell inside the liquid.
	/// </summary>
	public static class LiquidTextures
	{
		public const int FrameW = 48, FrameH = 80, Frames = 16;
		private static readonly RenderTarget2D[] targets = new RenderTarget2D[LiquidID.Count];
		private static SpriteBatch batch;
		private static float frameState;
		private static int drawnFrame = -1, drawnWater = -1;

		/// <summary>Vanilla's opacity per liquid (LiquidRenderer.DEFAULT_OPACITY).</summary>
		public static float Opacity(int liquid) => liquid switch {
			LiquidID.Lava => 0.95f,
			LiquidID.Honey => 0.95f,
			LiquidID.Shimmer => 0.75f,
			_ => 0.6f
		};

		/// <summary>The animated texture of a liquid; null until <see cref="Update"/> ran once.</summary>
		public static Texture2D Get(int liquid) => targets[Math.Clamp(liquid, 0, targets.Length - 1)];

		private static int Sheet(int liquid)
		{
			switch (liquid) {
				case LiquidID.Lava: return 1;
				case LiquidID.Honey: return 11;
				case LiquidID.Shimmer: return 14;
				default:
					int s = Main.waterStyle;
					return s >= 0 && s < TextureAssets.Liquid.Length && TextureAssets.Liquid[s] != null ? s : 0;
			}
		}

		/// <summary>
		/// Advances the animation like LiquidRenderer.Update and copies the current frames. Call outside any render pass.
		/// Returns true when a target had to be recreated, so meshes that referenced the old one must be rebuilt.
		/// </summary>
		public static bool Update(GraphicsDevice gd, GameTime time)
		{
			bool recreated = false;
			if (!Main.gamePaused && Main.hasFocus) {
				float speed = Main.windSpeedCurrent * 25f;
				speed = speed < 0f ? speed - 6f : speed + 6f;
				frameState += speed * (float)time.ElapsedGameTime.TotalSeconds;
				if (frameState < 0f)
					frameState += Frames;
				frameState %= Frames;
			}
			int frame = Math.Clamp((int)frameState, 0, Frames - 1);
			bool missing = false;
			foreach (var t in targets)
				missing |= t == null || t.IsDisposed;
			// the targets only change when the animation steps or the water style changes
			if (!missing && frame == drawnFrame && Sheet(0) == drawnWater)
				return false;
			drawnFrame = frame;
			drawnWater = Sheet(0);
			batch ??= new SpriteBatch(gd);
			for (int l = 0; l < targets.Length; l++) {
				if (targets[l] == null || targets[l].IsDisposed) {
					recreated |= targets[l] != null;
					targets[l] = new RenderTarget2D(gd, FrameW, FrameH, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
				}
				var asset = TextureAssets.Liquid[Sheet(l)];
				if (!asset.IsLoaded)
					asset = Main.Assets.Request<Texture2D>(asset.Name, AssetRequestMode.ImmediateLoad);
				Texture2D sheet = asset.Value;
				if (sheet == null)
					continue;
				int y = Math.Min(frame * FrameH, Math.Max(0, sheet.Height - FrameH));
				gd.SetRenderTarget(targets[l]);
				gd.Clear(Color.Transparent);
				batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
				batch.Draw(sheet, Vector2.Zero, new Rectangle(0, y, FrameW, FrameH), Color.White);
				batch.End();
			}
			return recreated;
		}

		public static void Unload()
		{
			for (int l = 0; l < targets.Length; l++) {
				targets[l]?.Dispose();
				targets[l] = null;
			}
			batch?.Dispose();
			batch = null;
			drawnFrame = drawnWater = -1;
		}
	}
}
