using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TerraDepth.Common;
using TerraDepth.Players;
using TerraDepth.World;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;

namespace TerraDepth.Rendering
{
	/// <summary>Renders the 3D scene into an off-screen target at the start of each frame; the HUD layer then puts it on screen.</summary>
	public static class Renderer3D
	{
		public static RenderTarget2D Target;
		public static bool FrameReady;
		private static AlphaTestEffect alphaFx;
		private static BasicEffect basicFx, lineFx;
		private static readonly HashSet<string> logged = new();
		private static readonly VertexPositionColor[] lines = new VertexPositionColor[24];

		public static void LogOnce(string what, Exception e)
		{
			if (logged.Add(what))
				ModContent.GetInstance<TerraDepthMod>().Logger.Error($"TerraDepth {what} failed", e);
		}

		public static void Unload()
		{
			Target?.Dispose();
			Target = null;
			alphaFx?.Dispose();
			basicFx?.Dispose();
			lineFx?.Dispose();
			alphaFx = null;
			basicFx = lineFx = null;
			ChunkRenderer.Unload();
			EntityRenderer.Unload();
			TileTextures.Unload();
			LiquidTextures.Unload();
		}

		public static void OnWorldChange()
		{
			ChunkRenderer.Clear();
			FrameReady = false;
		}

		private static readonly Dictionary<int, Vector3> horizonCache = new();

		/// <summary>Colour of vanilla's sky gradient near the horizon, tinted like vanilla tints it (the fog and sky colour).</summary>
		private static Color Horizon()
		{
			int bg = Math.Clamp(Main.background, 0, TextureAssets.Background.Length - 1);
			if (!horizonCache.TryGetValue(bg, out Vector3 h)) {
				h = new Vector3(0.55f, 0.75f, 1f);
				try {
					Main.instance.LoadBackground(bg);
					Texture2D tex = TextureAssets.Background[bg].Value;
					var px = new Color[1];
					tex.GetData(0, new Rectangle(tex.Width / 2, Math.Max(0, tex.Height - 2), 1, 1), px, 0, 1);
					h = px[0].ToVector3();
				}
				catch {
					// keep the default
				}
				horizonCache[bg] = h;
			}
			return new Color(h * Main.ColorOfTheSkies.ToVector3());
		}

		private static Color SkyColor(Vector3 camTile)
		{
			if (camTile.Y > Main.maxTilesY - 200)
				return new Color(40, 10, 5);
			Color sky = Horizon();
			float surface = (float)Main.worldSurface;
			float k = MathHelper.Clamp((camTile.Y - surface) / 60f, 0f, 1f);
			return Color.Lerp(sky, new Color(8, 7, 10), k);
		}

		public static void OnPreDraw(GameTime gameTime)
		{
			FrameReady = false;
			if (Main.gameMenu || Main.dedServ || !DepthWorldSystem.Active || Main.mapFullscreen)
				return;
			GraphicsDevice gd = Main.instance.GraphicsDevice;
			RenderTargetBinding[] old = null;
			try {
				old = gd.GetRenderTargets();
				int w = gd.PresentationParameters.BackBufferWidth, h = gd.PresentationParameters.BackBufferHeight;
				if (Target == null || Target.IsDisposed || Target.Width != w || Target.Height != h) {
					Target?.Dispose();
					Target = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.Depth24, 0, RenderTargetUsage.PreserveContents);
				}
				long t = Perf.Now;
				Player p = Main.LocalPlayer;
				// a chest open from another slice stands in Main.tile for vanilla's UI only, not in the 3D world
				p.GetModPlayer<DepthPlayer>().RemoveChestSwap();
				Camera3D.Update(p, w / (float)h);
				ChunkRenderer.UpdateLightingInputs(p);
				t = Perf.Lap("camera and light inputs", t);
				EntityRenderer.Prepare(gd, Camera3D.EyeTile);
				t = Perf.Lap("players, NPCs, minions to textures", t);
				if (LiquidTextures.Update(gd, gameTime))
					ChunkRenderer.Clear();
				t = Perf.Lap("liquid animation", t);

				gd.SetRenderTarget(Target);
				Vector3 cam = Camera3D.EyeTile;
				Color sky = SkyColor(cam);
				gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, sky, 1f, 0);

				alphaFx ??= new AlphaTestEffect(gd);
				basicFx ??= new BasicEffect(gd);
				lineFx ??= new BasicEffect(gd) { VertexColorEnabled = true, TextureEnabled = false, LightingEnabled = false };

				var cfg = ModContent.GetInstance<DepthClientConfig>();
				float fogEnd = cfg.RenderDistance;
				alphaFx.World = Matrix.Identity;
				alphaFx.View = Camera3D.View;
				alphaFx.Projection = Camera3D.Projection;
				alphaFx.VertexColorEnabled = true;
				alphaFx.AlphaFunction = CompareFunction.Greater;
				alphaFx.ReferenceAlpha = 110;
				alphaFx.DiffuseColor = Vector3.One;
				alphaFx.Alpha = 1f;
				alphaFx.FogEnabled = true;
				alphaFx.FogColor = sky.ToVector3();
				alphaFx.FogStart = fogEnd * 0.55f;
				alphaFx.FogEnd = fogEnd;

				gd.BlendState = BlendState.Opaque;
				gd.RasterizerState = RasterizerState.CullNone;
				gd.SamplerStates[0] = SamplerState.PointClamp;

				// sky bodies: no depth
				gd.DepthStencilState = DepthStencilState.None;
				DrawSkyBodies(gd, cam);

				gd.DepthStencilState = DepthStencilState.Default;
				t = Perf.Lap("sky", t);
				ChunkRenderer.Update(gd, cam);
				t = Perf.Lap("chunk build and relight", t);
				ChunkRenderer.DrawOpaque(gd, alphaFx);
				t = Perf.Lap("draw blocks", t);

				EntityRenderer.Collect(cam);
				gd.SamplerStates[0] = SamplerState.PointClamp;
				EntityRenderer.DrawOpaque(gd, alphaFx);
				t = Perf.Lap("entities", t);

				// liquids
				basicFx.World = Matrix.Identity;
				basicFx.View = Camera3D.View;
				basicFx.Projection = Camera3D.Projection;
				basicFx.TextureEnabled = true;
				basicFx.VertexColorEnabled = true;
				basicFx.LightingEnabled = false;
				basicFx.FogEnabled = true;
				basicFx.FogColor = sky.ToVector3();
				basicFx.FogStart = fogEnd * 0.55f;
				basicFx.FogEnd = fogEnd;
				basicFx.Alpha = 1f;
				// vanilla-rendered and translucent sprites, then liquids
				gd.BlendState = BlendState.AlphaBlend;
				gd.DepthStencilState = DepthStencilState.DepthRead;
				EntityRenderer.DrawBlended(gd, basicFx);
				gd.BlendState = BlendState.AlphaBlend;
				gd.DepthStencilState = DepthStencilState.DepthRead;
				gd.SamplerStates[0] = SamplerState.PointClamp;
				ChunkRenderer.DrawTranslucent(gd, basicFx, cam);

				DrawSelection(gd);
				gd.BlendState = BlendState.Opaque;
				EntityRenderer.DrawHeldItem(gd, alphaFx);
				Perf.Lap("liquids, sprites, held item", t);
				FrameReady = true;
			}
			catch (Exception e) {
				LogOnce("3D render", e);
			}
			finally {
				Perf.Frame();
				if (!Main.gameMenu)
					Main.LocalPlayer.GetModPlayer<DepthPlayer>().ApplyChestSwap();
				if (old != null && old.Length > 0)
					gd.SetRenderTargets(old);
				else
					gd.SetRenderTarget(null);
				gd.BlendState = BlendState.AlphaBlend;
				gd.DepthStencilState = DepthStencilState.None;
				gd.RasterizerState = RasterizerState.CullCounterClockwise;
				gd.SamplerStates[0] = SamplerState.LinearClamp;
			}
		}

		private static void DrawSkyBodies(GraphicsDevice gd, Vector3 cam)
		{
			if (cam.Y > Main.worldSurface + 30)
				return;
			Vector3 eye = Camera3D.ToRender(cam);
			float dist = Camera3D.FarPlane * 0.85f;
			double len = Main.dayTime ? Main.dayLength : Main.nightLength;
			float t = (float)(Main.time / len);
			float a = MathHelper.Pi * t;
			Vector3 dir = Vector3.Normalize(new Vector3(-(float)Math.Cos(a), (float)Math.Sin(a) * 0.9f + 0.05f, -0.45f));
			Vector3 pos = eye + dir * dist;
			alphaFx.FogEnabled = false;
			try {
				if (Main.dayTime) {
					Texture2D sun = TextureAssets.Sun.Value;
					alphaFx.Texture = sun;
					EntityRenderer.DrawBillboard(gd, alphaFx, sun, sun.Bounds, pos, dist * 0.12f, Color.White);
				}
				else {
					int type = Math.Clamp(Main.moonType, 0, TextureAssets.Moon.Length - 1);
					Texture2D moon = TextureAssets.Moon[type].Value;
					int fw = moon.Width, fh = moon.Height / 8;
					var src = new Rectangle(0, fh * (Main.moonPhase % 8), fw, fh);
					alphaFx.Texture = moon;
					EntityRenderer.DrawBillboard(gd, alphaFx, moon, src, pos, dist * 0.1f, Color.White);
				}
			}
			finally {
				alphaFx.FogEnabled = true;
			}
		}

		private static void DrawSelection(GraphicsDevice gd)
		{
			if (!Input3D.HasTarget || !Input3D.LookActive)
				return;
			var h = Input3D.Target;
			Vector3 a = new(h.X - 0.005f, h.Y - 0.005f, h.Z - 0.005f), b = new(h.X + 1.005f, h.Y + 1.005f, h.Z + 1.005f);
			Vector3[] c = {
				new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, a.Y, b.Z), new(a.X, a.Y, b.Z),
				new(a.X, b.Y, a.Z), new(b.X, b.Y, a.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z)
			};
			int[] e = { 0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
			Color col = new Color(20, 20, 20, 220);
			for (int i = 0; i < 24; i++)
				lines[i] = new VertexPositionColor(Camera3D.ToRender(c[e[i]]), col);
			lineFx.World = Matrix.Identity;
			lineFx.View = Camera3D.View;
			lineFx.Projection = Camera3D.Projection;
			gd.BlendState = BlendState.AlphaBlend;
			gd.DepthStencilState = DepthStencilState.DepthRead;
			lineFx.CurrentTechnique.Passes[0].Apply();
			gd.DrawUserPrimitives(PrimitiveType.LineList, lines, 0, 12);
		}
	}
}
