using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TerraDepth.Entities;
using TerraDepth.Players;
using TerraDepth.World;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraDepth.Rendering
{
	/// <summary>
	/// Light used while vanilla draws an entity for us: vanilla asks Lighting.GetColor (the 2D slice lighting), we answer
	/// with the light of the 3D world at the entity.
	/// </summary>
	public static class EntityLight
	{
		public static bool Active;
		public static Color Value = Color.White;
	}

	/// <summary>
	/// Draws entities as upright billboards at their depth. Players and NPCs are drawn by vanilla into textures first
	/// (so slimes, items inside slimes, hats, glowmasks and modded PreDraw look exactly like in 2D), the rest from their sprites.
	/// </summary>
	public static class EntityRenderer
	{
		private struct Sprite
		{
			public Texture2D Tex;
			public Rectangle Src;
			public Vector3 Center; // tile space
			public Vector2 Size;   // tiles
			public float Rotation;
			public bool Flip;
			public Color Color;
			public float Dist;
			public bool Blend;     // alpha blended (translucent or pre-rendered), else alpha tested
			public bool Axial;     // laid along Forward instead of facing the camera
			public Vector3 Forward; // render space, normalized
			public Vector2 ImageForward; // direction of travel inside the (flipped) sprite image, y down
			public bool Upright;   // axial sprite in a vertical plane (the player), instead of turned toward the camera
			public bool FlipV;
		}

		private static readonly List<Sprite> sprites = new();
		private static readonly VertexPositionColorTexture[] quad = new VertexPositionColorTexture[6];
		private static readonly RenderTarget2D[] playerTargets = new RenderTarget2D[Main.maxPlayers];
		private const int PlayerRT = 192;
		private const float MaxDist = 72f;

		private const int AtlasSize = 2048;
		private static RenderTarget2D npcAtlas;
		private static readonly Dictionary<int, Rectangle> npcCells = new();
		private static readonly Dictionary<int, Rectangle> projCells = new();
		private const int MaxAtlasProjectiles = 96;

		// projectiles vanilla hides from its normal pass and draws in a layer of their own (Stardust Dragon and others)
		private static readonly HashSet<int> cachedProjDraws = new();
		private static readonly System.Reflection.MethodInfo cacheProjDraws =
			typeof(Main).GetMethod("CacheProjDraws", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

		private static void CacheHiddenProjectiles()
		{
			cachedProjDraws.Clear();
			try {
				cacheProjDraws?.Invoke(Main.instance, null);
			}
			catch (Exception e) {
				Renderer3D.LogOnce("projectile draw cache", e);
				return;
			}
			var m = Main.instance;
			foreach (var list in new[] { m.DrawCacheProjsBehindNPCsAndTiles, m.DrawCacheProjsBehindNPCs, m.DrawCacheProjsBehindProjectiles,
					m.DrawCacheProjsOverWiresUI, m.DrawCacheProjsOverPlayers, m.DrawCacheFirstFractals })
				foreach (int i in list)
					cachedProjDraws.Add(i);
		}

		/// <summary>Whether vanilla draws the projectile at all (hidden ones only in their own layer, or with their owner).</summary>
		private static bool VanillaDrawn(Projectile proj)
		{
			if (!proj.hide || proj.aiStyle == ProjAIStyleID.Yoyo)
				return true;
			bool heldByOwner = proj.owner >= 0 && proj.owner < Main.maxPlayers && Main.player[proj.owner].heldProj == proj.whoAmI;
			return !heldByOwner && cachedProjDraws.Contains(proj.whoAmI);
		}

		/// <summary>Minions, pets, sentries and specially drawn projectiles are drawn by vanilla's code into the atlas.</summary>
		private static bool UsesAtlas(Projectile proj) => proj.minion || proj.sentry || Main.projPet[proj.type] || proj.hide;

		private static float ProjZ(Projectile proj)
		{
			float z = proj.GetGlobalProjectile<DepthProjectile>().Z;
			return z < 0f ? Voxel.ZC + 0.5f : z;
		}

		public static void Unload()
		{
			for (int i = 0; i < playerTargets.Length; i++) {
				playerTargets[i]?.Dispose();
				playerTargets[i] = null;
			}
			npcAtlas?.Dispose();
			npcAtlas = null;
			scratch?.Dispose();
			scratch = null;
		}

		/// <summary>Light of the 3D world at a tile-space point (sky, plus vanilla light when on the simulated slice).</summary>
		public static Color LightAt(Vector3 tile)
		{
			int x = (int)tile.X, y = (int)tile.Y, z = (int)Math.Floor(tile.Z);
			if (!WorldGen.InWorld(x, y))
				return Color.White;
			Vector3 v = ChunkRenderer.Sun * ChunkBuilder.SkyLight(x, y, z);
			if (y >= ChunkBuilder.HellRow)
				v = Vector3.Max(v, ChunkBuilder.HellLight);
			if (Voxel.WindowOpen && Voxel.InWindow(x, y) && Math.Abs(tile.Z - (Voxel.WinZ + 0.5f)) < 1.5f) {
				bool was = EntityLight.Active;
				EntityLight.Active = false;
				v = Vector3.Max(v, Lighting.GetColor(x, y).ToVector3());
				EntityLight.Active = was;
			}
			v = Vector3.Max(v, new Vector3(0.07f));
			return new Color(Vector3.Min(v, Vector3.One));
		}

		private static void Add(Texture2D tex, Rectangle src, Vector3 center, Vector2 sizeTiles, float rot, bool flip, Color color, Vector3 cam, bool blend)
		{
			if (tex == null || src.Width <= 0 || src.Height <= 0)
				return;
			sprites.Add(new Sprite {
				Tex = tex, Src = src, Center = center, Size = sizeTiles, Rotation = rot, Flip = flip, Color = color,
				Dist = Vector3.DistanceSquared(center, cam), Blend = blend || color.A < 250
			});
		}

		/// <summary>
		/// NPCs that move in 3D face their real direction of travel on screen: their AI thinks the player is always on its
		/// right, so the vanilla sprite direction alone would be wrong.
		/// </summary>
		private static bool NpcFlip(NPC npc, bool flipView)
		{
			var d = npc.GetGlobalNPC<DepthNPC>();
			if (!d.HasHeading)
				return flipView;
			bool wantRight = d.Heading.X * Camera3D.FlatRight.X + d.Heading.Y * Camera3D.FlatRight.Z > 0f;
			bool facesRight = npc.spriteDirection == 1;
			return wantRight != facesRight;
		}

		private static float ZOf(NPC npc)
		{
			float z = npc.GetGlobalNPC<DepthNPC>().Z;
			return z < 0f ? Voxel.ZC + 0.5f : z;
		}

		/// <summary>Renders players and NPCs with vanilla code into textures; must run before the 3D pass binds its target.</summary>
		public static void Prepare(GraphicsDevice gd, Vector3 cam)
		{
			Vector2 oldScreen = Main.screenPosition;
			try {
				for (int i = 0; i < Main.maxPlayers; i++) {
					Player p = Main.player[i];
					if (!p.active || p.dead || p.outOfRange)
						continue;
					if (i == Main.myPlayer && !Camera3D.ThirdPerson)
						continue;
					ref RenderTarget2D rt = ref playerTargets[i];
					if (rt == null || rt.IsDisposed)
						rt = new RenderTarget2D(gd, PlayerRT, PlayerRT, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
					gd.SetRenderTarget(rt);
					gd.Clear(Color.Transparent);
					EntityLight.Value = LightAt(new Vector3(p.Center.X / 16f, p.Center.Y / 16f, p.GetModPlayer<DepthPlayer>().Z));
					EntityLight.Active = true;
					Main.screenPosition = (p.Center - new Vector2(PlayerRT / 2f)).Floor();
					Main.PlayerRenderer.DrawPlayers(Main.Camera, new[] { p });
					EntityLight.Active = false;
				}
			}
			catch (Exception e) {
				Renderer3D.LogOnce("player sprite", e);
			}
			finally {
				EntityLight.Active = false;
				Main.screenPosition = oldScreen;
			}
			CacheHiddenProjectiles();
			LiftProjectiles(gd, cam);
			PrepareNpcs(gd, cam);
			Main.screenPosition = oldScreen;
			gd.SetRenderTarget(null);
		}

		private static void PrepareNpcs(GraphicsDevice gd, Vector3 cam)
		{
			npcCells.Clear();
			if (npcAtlas == null || npcAtlas.IsDisposed)
				npcAtlas = new RenderTarget2D(gd, AtlasSize, AtlasSize, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
			var list = new List<(float d, NPC n)>();
			foreach (NPC npc in Main.ActiveNPCs) {
				var c = new Vector3(npc.Center.X / 16f, npc.Center.Y / 16f, ZOf(npc));
				float d = Vector3.DistanceSquared(c, cam);
				if (d < MaxDist * MaxDist)
					list.Add((d, npc));
			}
			list.Sort((a, b) => a.d.CompareTo(b.d));

			gd.SetRenderTarget(npcAtlas);
			gd.Clear(Color.Transparent);
			var sb = Main.spriteBatch;
			var scissor = new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true };
			int x = 0, y = 0, rowH = 0;
			foreach (var (_, npc) in list) {
				int fw = Math.Max(npc.frame.Width, npc.width), fh = Math.Max(npc.frame.Height, npc.height);
				int w = (int)(fw * Math.Max(1f, npc.scale)) + 64, h = (int)(fh * Math.Max(1f, npc.scale)) + 64;
				if (w > AtlasSize / 2 || h > AtlasSize / 2)
					continue; // huge bosses use their plain sprite
				if (x + w > AtlasSize) {
					x = 0;
					y += rowH;
					rowH = 0;
				}
				if (y + h > AtlasSize)
					break;
				var cell = new Rectangle(x, y, w, h);
				x += w;
				rowH = Math.Max(rowH, h);
				try {
					Vector2 screenPos = npc.Center - new Vector2(cell.X + w / 2f, cell.Y + h / 2f);
					Main.screenPosition = screenPos;
					EntityLight.Value = LightAt(new Vector3(npc.Center.X / 16f, npc.Center.Y / 16f, ZOf(npc)));
					EntityLight.Active = true;
					gd.ScissorRectangle = cell;
					sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, scissor, null, Matrix.Identity);
					Main.instance.DrawNPCDirect(sb, npc, npc.behindTiles, screenPos);
					sb.End();
					npcCells[npc.whoAmI] = cell;
				}
				catch (Exception e) {
					Renderer3D.LogOnce("npc sprite " + npc.type, e);
					try { sb.End(); } catch { }
				}
				finally {
					EntityLight.Active = false;
				}
			}
			PrepareProjectiles(gd, cam, sb, scissor, ref x, ref y, ref rowH);
			scissor.Dispose();
		}

		// ------------------------------------------------------------------ vanilla drawing lifted into 3D

		private enum LiftKind { None, Tether, Whip, Trail }

		private static readonly List<Sprite> lifted = new();
		private static readonly HashSet<int> liftedProjs = new();
		private static readonly List<CapturedSprite> captured = new();
		private static RenderTarget2D scratch;
		private const int MaxLifted = 64;

		/// <summary>
		/// Projectiles whose vanilla drawing reaches beyond their sprite: chains, strings and lines to the owner (hooks,
		/// flails, harpoons, yoyos, bobbers), whips, and afterimage trails.
		/// </summary>
		private static LiftKind KindOf(Projectile p)
		{
			if (p.owner < 0 || p.owner >= Main.maxPlayers || !Main.player[p.owner].active)
				return LiftKind.None;
			if (ProjectileID.Sets.IsAWhip[p.type])
				return LiftKind.Whip;
			if (p.aiStyle is ProjAIStyleID.Hook or ProjAIStyleID.Flail or ProjAIStyleID.Harpoon or ProjAIStyleID.Yoyo or ProjAIStyleID.Bobber
					|| Main.projHook[p.type])
				return LiftKind.Tether;
			if (ProjectileID.Sets.TrailingMode[p.type] >= 0 && !UsesAtlas(p))
				return LiftKind.Trail;
			return LiftKind.None;
		}

		/// <summary>
		/// Draws those projectiles with vanilla's code into a sprite capture and places every captured sprite in 3D:
		/// tethers and whips through a map of their 2D drawing plane onto the real 3D line from the owner, trails at the
		/// depth the projectile had where each afterimage is drawn.
		/// </summary>
		private static void LiftProjectiles(GraphicsDevice gd, Vector3 cam)
		{
			lifted.Clear();
			liftedProjs.Clear();
			if (!SpriteCapture.Available)
				return;
			var list = new List<(float d, Projectile p, LiftKind k)>();
			foreach (Projectile proj in Main.ActiveProjectiles) {
				if (!VanillaDrawn(proj))
					continue;
				LiftKind k = KindOf(proj);
				if (k == LiftKind.None)
					continue;
				float d = Vector3.DistanceSquared(new Vector3(proj.Center.X / 16f, proj.Center.Y / 16f, ProjZ(proj)), cam);
				if (d < MaxDist * MaxDist)
					list.Add((d, proj, k));
			}
			if (list.Count == 0)
				return;
			list.Sort((a, b) => a.d.CompareTo(b.d));
			if (scratch == null || scratch.IsDisposed)
				scratch = new RenderTarget2D(gd, 4, 4);
			// anything vanilla flushes on its own during the capture lands here, out of sight
			gd.SetRenderTarget(scratch);
			Vector2 oldScreen = Main.screenPosition;
			var sb = Main.spriteBatch;
			for (int i = 0; i < list.Count && i < MaxLifted; i++) {
				var (_, proj, kind) = list[i];
				try {
					LiftOne(proj, kind, sb);
					liftedProjs.Add(proj.whoAmI);
				}
				catch (Exception e) {
					Renderer3D.LogOnce("lifted projectile " + proj.type, e);
				}
				finally {
					EntityLight.Active = false;
					Main.screenPosition = oldScreen;
				}
			}
		}

		private static void LiftOne(Projectile proj, LiftKind kind, SpriteBatch sb)
		{
			Player owner = Main.player[proj.owner];
			float oz = owner.GetModPlayer<DepthPlayer>().Z;
			var dp = proj.GetGlobalProjectile<DepthProjectile>();
			float pz = ProjZ(proj);
			Vector3 p3 = new(proj.Center.X / 16f, proj.Center.Y / 16f, pz);

			// the plane vanilla draws in (anchor c2, direction u) and where it goes in 3D (anchor c3, direction a3)
			Vector2 c2 = owner.MountedCenter;
			Vector2 u;
			Vector3 c3 = new(c2.X / 16f, c2.Y / 16f, oz), a3;
			Vector2 shift = Vector2.Zero;
			switch (kind) {
				case LiftKind.Tether: {
					Vector2 real = proj.Center - c2;
					u = real.LengthSquared() > 4f ? Vector2.Normalize(real) : new Vector2(owner.direction, 0f);
					Vector3 d3 = p3 - c3;
					float len = d3.Length();
					a3 = len > 0.01f ? d3 / len : new Vector3(u, 0f);
					// vanilla lays the chain along the 2D distance: give it the real 3D length, so the links neither gap nor pile up
					shift = c2 + u * len * 16f - proj.Center;
					break;
				}
				case LiftKind.Whip: {
					c2 = owner.RotatedRelativePoint(owner.MountedCenter);
					c3 = new Vector3(c2.X / 16f, c2.Y / 16f, oz);
					u = proj.velocity.LengthSquared() > 0.01f ? Vector2.Normalize(proj.velocity) : new Vector2(owner.direction, 0f);
					a3 = Vector3.Normalize(new Vector3(u.X / 16f, u.Y / 16f, dp.HeldSlope));
					break;
				}
				default: {
					Vector3 dir = dp.Direction(proj);
					u = proj.velocity.LengthSquared() > 0.01f ? Vector2.Normalize(proj.velocity) : new Vector2(1f, 0f);
					a3 = dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : new Vector3(u, 0f);
					break;
				}
			}
			Vector2 uPerp = new(-u.Y, u.X);
			Vector3 p = new Vector3(uPerp, 0f);
			p -= a3 * Vector3.Dot(p, a3);
			p = p.LengthSquared() > 1e-6f ? Vector3.Normalize(p) : Vector3.Normalize(Vector3.Cross(a3, Vector3.UnitZ) + new Vector3(0f, 1e-3f, 0f));
			// a 2D direction of the drawing, in 3D
			Vector3 Map(Vector2 d) => a3 * Vector2.Dot(d, u) + p * Vector2.Dot(d, uPerp);

			captured.Clear();
			Main.screenPosition = Vector2.Zero;
			EntityLight.Value = LightAt(p3);
			EntityLight.Active = true;
			Vector2 oldPos = proj.position;
			proj.position += shift;
			try {
				SpriteCapture.Run(sb, () => Main.instance.DrawProjDirect(proj), captured);
			}
			finally {
				proj.position = oldPos;
			}

			foreach (var cs in captured) {
				Vector3 center;
				if (kind == LiftKind.Trail)
					center = new Vector3(cs.Center.X / 16f, cs.Center.Y / 16f, dp.TrailDepthAt(cs.Center));
				else
					center = c3 + Map((cs.Center - c2) / 16f);
				// the image axis vanilla drew along the flight (or the chain) is the one carried along it in 3D;
				// a whip curls away from its aim, its segments run along their long side
				float co = (float)Math.Cos(cs.Rotation), si = (float)Math.Sin(cs.Rotation);
				Vector2 ex = new(co, si), ey = new(-si, co);
				bool alongX = kind == LiftKind.Whip ? cs.Size.X > cs.Size.Y : Math.Abs(Vector2.Dot(ex, u)) >= Math.Abs(Vector2.Dot(ey, u));
				Vector2 imageAxis = alongX ? new Vector2(1f, 0f) : new Vector2(0f, 1f);
				Vector2 drawn = alongX ? ex : ey;
				Vector3 fwd = Map(drawn);
				if (fwd.LengthSquared() < 1e-6f)
					continue;
				lifted.Add(new Sprite {
					Tex = cs.Tex, Src = cs.Src, Center = center, Size = cs.Size / 16f, Color = cs.Color, Blend = true,
					Axial = true, Forward = Vector3.Normalize(Camera3D.ToRender(fwd)), ImageForward = imageAxis,
					Flip = cs.FlipH, FlipV = cs.FlipV
				});
			}
		}

		private static void PrepareProjectiles(GraphicsDevice gd, Vector3 cam, SpriteBatch sb, RasterizerState scissor, ref int x, ref int y, ref int rowH)
		{
			projCells.Clear();
			var list = new List<(float d, Projectile p)>();
			foreach (Projectile proj in Main.ActiveProjectiles) {
				if (!UsesAtlas(proj) || !VanillaDrawn(proj) || liftedProjs.Contains(proj.whoAmI))
					continue;
				var c = new Vector3(proj.Center.X / 16f, proj.Center.Y / 16f, ProjZ(proj));
				float d = Vector3.DistanceSquared(c, cam);
				if (d < MaxDist * MaxDist)
					list.Add((d, proj));
			}
			list.Sort((a, b) => a.d.CompareTo(b.d));
			int count = 0;
			foreach (var (_, proj) in list) {
				if (count++ >= MaxAtlasProjectiles)
					break;
				Main.instance.LoadProjectile(proj.type);
				Texture2D tex = TextureAssets.Projectile[proj.type].Value;
				int fh = tex.Height / Math.Max(1, Main.projFrames[proj.type]);
				float big = Math.Max(Math.Max(tex.Width, fh), Math.Max(proj.width, proj.height)) * Math.Max(1f, proj.scale);
				// room for rotation, draw offsets and trails around the sprite
				int size = (int)(big * 1.5f) + 48;
				if (size > AtlasSize / 2)
					continue;
				if (x + size > AtlasSize) {
					x = 0;
					y += rowH;
					rowH = 0;
				}
				if (y + size > AtlasSize)
					break;
				var cell = new Rectangle(x, y, size, size);
				x += size;
				rowH = Math.Max(rowH, size);
				try {
					Vector2 screenPos = proj.Center - new Vector2(cell.X + size / 2f, cell.Y + size / 2f);
					Main.screenPosition = screenPos;
					EntityLight.Value = LightAt(new Vector3(proj.Center.X / 16f, proj.Center.Y / 16f, ProjZ(proj)));
					EntityLight.Active = true;
					gd.ScissorRectangle = cell;
					sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, scissor, null, Matrix.Identity);
					Main.instance.DrawProjDirect(proj);
					sb.End();
					projCells[proj.whoAmI] = cell;
				}
				catch (Exception e) {
					Renderer3D.LogOnce("projectile sprite " + proj.type, e);
					try { sb.End(); } catch { }
				}
				finally {
					EntityLight.Active = false;
				}
			}
		}

		public static void Collect(Vector3 cam)
		{
			sprites.Clear();
			float maxD2 = MaxDist * MaxDist;
			bool flipView = Camera3D.FlatRight.X < 0f;

			foreach (NPC npc in Main.ActiveNPCs) {
				float z = ZOf(npc);
				if (npcCells.TryGetValue(npc.whoAmI, out Rectangle cell)) {
					var cc = new Vector3(npc.Center.X / 16f, (npc.Center.Y + npc.gfxOffY) / 16f, z);
					Add(npcAtlas, cell, cc, new Vector2(cell.Width, cell.Height) / 16f, 0f, NpcFlip(npc, flipView), Color.White, cam, true);
					continue;
				}
				Main.instance.LoadNPC(npc.type);
				Texture2D tex = TextureAssets.Npc[npc.type].Value;
				Rectangle src = npc.frame;
				if (src.Width == 0 || src.Height == 0)
					src = new Rectangle(0, 0, tex.Width, tex.Height / Math.Max(1, Main.npcFrameCount[npc.type]));
				float bottom = (npc.position.Y + npc.height + 4f + npc.gfxOffY) / 16f;
				float h = src.Height * npc.scale / 16f;
				var c = new Vector3(npc.Center.X / 16f, bottom - h / 2f, z);
				if (Vector3.DistanceSquared(c, cam) > maxD2)
					continue;
				Color light = npc.GetAlpha(LightAt(c));
				Add(tex, src, c, new Vector2(src.Width, src.Height) * npc.scale / 16f, npc.rotation, (npc.spriteDirection == 1) ^ flipView, light, cam, false);
			}

			foreach (var l in lifted) {
				var ls = l;
				ls.Dist = Vector3.DistanceSquared(l.Center, cam);
				sprites.Add(ls);
			}
			foreach (Projectile proj in Main.ActiveProjectiles) {
				if (!VanillaDrawn(proj) || liftedProjs.Contains(proj.whoAmI))
					continue;
				float z = ProjZ(proj);
				if (projCells.TryGetValue(proj.whoAmI, out Rectangle pcell)) {
					var pc = new Vector3(proj.Center.X / 16f, proj.Center.Y / 16f, z);
					Add(npcAtlas, pcell, pc, new Vector2(pcell.Width, pcell.Height) / 16f, 0f, flipView, Color.White, cam, true);
					continue;
				}
				var c = new Vector3(proj.Center.X / 16f, (proj.Center.Y + proj.gfxOffY) / 16f, z);
				if (Vector3.DistanceSquared(c, cam) > maxD2)
					continue;
				Main.instance.LoadProjectile(proj.type);
				Texture2D tex = TextureAssets.Projectile[proj.type].Value;
				int frames = Math.Max(1, Main.projFrames[proj.type]);
				int fh = tex.Height / frames;
				var src = new Rectangle(0, fh * Math.Clamp(proj.frame, 0, frames - 1), tex.Width, fh);
				Vector2 size = new Vector2(src.Width, src.Height) * proj.scale / 16f;
				Color col = proj.GetAlpha(LightAt(c));
				if (TryAxial(proj, out Vector3 fwd, out Vector2 imgFwd)) {
					sprites.Add(new Sprite {
						Tex = tex, Src = src, Center = c, Size = size, Flip = proj.spriteDirection == -1, Color = col,
						Dist = Vector3.DistanceSquared(c, cam), Blend = col.A < 250, Axial = true, Forward = fwd, ImageForward = imgFwd
					});
				}
				else
					Add(tex, src, c, size, proj.rotation, (proj.spriteDirection == -1) ^ flipView, col, cam, false);
			}

			foreach (Item item in Main.ActiveItems) {
				float z = item.GetGlobalItem<DepthItem>().Z;
				if (z < 0f)
					z = Voxel.ZC + 0.5f;
				Main.instance.LoadItem(item.type);
				Texture2D tex = TextureAssets.Item[item.type].Value;
				Rectangle src = Main.itemAnimations[item.type] != null ? Main.itemAnimations[item.type].GetFrame(tex) : tex.Bounds;
				float scale = item.scale;
				float big = Math.Max(src.Width, src.Height) * scale;
				if (big > 40f)
					scale *= 40f / big;
				float bottom = (item.position.Y + item.height) / 16f;
				var c = new Vector3(item.Center.X / 16f, bottom - src.Height * scale / 32f, z);
				if (Vector3.DistanceSquared(c, cam) > maxD2)
					continue;
				Add(tex, src, c, new Vector2(src.Width, src.Height) * scale / 16f, 0f, false, item.GetAlpha(LightAt(c)), cam, false);
			}

			for (int i = 0; i < Main.maxPlayers; i++) {
				Player p = Main.player[i];
				RenderTarget2D rt = playerTargets[i];
				if (!p.active || p.dead || rt == null || rt.IsDisposed)
					continue;
				if (i == Main.myPlayer && !Camera3D.ThirdPerson)
					continue;
				var c = new Vector3(p.Center.X / 16f, (p.Center.Y + p.gfxOffY) / 16f, p.GetModPlayer<DepthPlayer>().Z);
				if (i == Main.myPlayer) {
					// the local player turns to where the camera looks (like the first-person view model), slightly
					// toward the over-the-shoulder camera so the profile sprite is not seen edge-on
					Vector3 facing = Vector3.Normalize(Camera3D.FlatForward + Camera3D.FlatRight * 0.6f);
					sprites.Add(new Sprite {
						Tex = rt, Src = rt.Bounds, Center = c, Size = new Vector2(PlayerRT / 16f), Color = Color.White,
						Dist = Vector3.DistanceSquared(c, cam), Blend = true, Axial = true, Upright = true,
						Forward = Camera3D.ToRender(facing), ImageForward = new Vector2(1f, 0f), Flip = p.direction == -1
					});
				}
				else
					Add(rt, rt.Bounds, c, new Vector2(PlayerRT / 16f), 0f, flipView, Color.White, cam, true);
			}
		}

		/// <summary>
		/// Projectiles whose sprite vanilla turns along their velocity (arrows, bullets, bolts) fly along their real 3D
		/// direction. Returns that direction in render space and where "forward" points inside the sprite image.
		/// </summary>
		private static bool TryAxial(Projectile proj, out Vector3 forward, out Vector2 imageForward)
		{
			forward = default;
			imageForward = new Vector2(0f, -1f);
			Vector3 dir = proj.GetGlobalProjectile<DepthProjectile>().Direction(proj);
			Vector2 v = proj.velocity;
			float v2 = v.Length();
			if (dir.LengthSquared() < 1e-4f)
				return false;
			if (v2 > 0.5f) {
				// sprite turned with the velocity: its rotation is the velocity angle plus a multiple of 90 degrees
				float diff = MathHelper.WrapAngle(proj.rotation - (float)Math.Atan2(v.Y, v.X));
				float q = diff / MathHelper.PiOver2;
				if (Math.Abs(q - Math.Round(q)) > 0.12f)
					return false;
				// travel direction inside the image: the velocity rotated back by the sprite's rotation
				Vector2 n = v / v2;
				float cos = (float)Math.Cos(-proj.rotation), sin = (float)Math.Sin(-proj.rotation);
				imageForward = new Vector2(n.X * cos - n.Y * sin, n.X * sin + n.Y * cos);
			}
			else if (proj.aiStyle != ProjAIStyleID.Arrow)
				return false; // moving almost only in depth: only trust arrows/bullets to point "up" in their image
			forward = Vector3.Normalize(Camera3D.ToRender(dir));
			return true;
		}

		private static void EmitAxial(GraphicsDevice gd, in Sprite s)
		{
			Vector3 c = Camera3D.ToRender(s.Center);
			Vector3 side;
			if (s.Upright)
				side = Vector3.Down;
			else {
				Vector3 toCam = Camera3D.ToRender(Camera3D.EyeTile) - c;
				side = Vector3.Cross(s.Forward, toCam);
				if (side.LengthSquared() < 1e-6f)
					side = Camera3D.ToRender(Camera3D.FlatRight);
				side.Normalize();
			}
			// images of the texture axes: x to the right, y downward
			Vector2 g = s.ImageForward;
			Vector3 ax = s.Forward * g.X - side * g.Y;
			Vector3 ay = s.Forward * g.Y + side * g.X;
			Vector3 hx = ax * (s.Size.X / 2f), hy = ay * (s.Size.Y / 2f);
			float iw = 1f / s.Tex.Width, ih = 1f / s.Tex.Height;
			float u0 = s.Src.X * iw, u1 = (s.Src.X + s.Src.Width) * iw, v0 = s.Src.Y * ih, v1 = (s.Src.Y + s.Src.Height) * ih;
			if (s.Flip)
				(u0, u1) = (u1, u0);
			if (s.FlipV)
				(v0, v1) = (v1, v0);
			var tl = new VertexPositionColorTexture(c - hx - hy, s.Color, new Vector2(u0, v0));
			var tr = new VertexPositionColorTexture(c + hx - hy, s.Color, new Vector2(u1, v0));
			var br = new VertexPositionColorTexture(c + hx + hy, s.Color, new Vector2(u1, v1));
			var bl = new VertexPositionColorTexture(c - hx + hy, s.Color, new Vector2(u0, v1));
			quad[0] = tl; quad[1] = tr; quad[2] = br;
			quad[3] = tl; quad[4] = br; quad[5] = bl;
			gd.DrawUserPrimitives(PrimitiveType.TriangleList, quad, 0, 2);
		}

		private static void Emit(GraphicsDevice gd, in Sprite s, Vector3 right, Vector3 up)
		{
			if (s.Axial) {
				EmitAxial(gd, s);
				return;
			}
			float cos = (float)Math.Cos(s.Rotation), sin = (float)Math.Sin(s.Rotation);
			Vector3 r = right * cos - up * sin;
			Vector3 u = up * cos + right * sin;
			Vector3 c = Camera3D.ToRender(s.Center);
			Vector3 hr = r * (s.Size.X / 2f), hu = u * (s.Size.Y / 2f);
			float iw = 1f / s.Tex.Width, ih = 1f / s.Tex.Height;
			float u0 = s.Src.X * iw, u1 = (s.Src.X + s.Src.Width) * iw, v0 = s.Src.Y * ih, v1 = (s.Src.Y + s.Src.Height) * ih;
			if (s.Flip)
				(u0, u1) = (u1, u0);
			if (s.FlipV)
				(v0, v1) = (v1, v0);
			var tl = new VertexPositionColorTexture(c - hr + hu, s.Color, new Vector2(u0, v0));
			var tr = new VertexPositionColorTexture(c + hr + hu, s.Color, new Vector2(u1, v0));
			var br = new VertexPositionColorTexture(c + hr - hu, s.Color, new Vector2(u1, v1));
			var bl = new VertexPositionColorTexture(c - hr - hu, s.Color, new Vector2(u0, v1));
			quad[0] = tl; quad[1] = tr; quad[2] = br;
			quad[3] = tl; quad[4] = br; quad[5] = bl;
			gd.DrawUserPrimitives(PrimitiveType.TriangleList, quad, 0, 2);
		}

		/// <summary>Opaque sprites with depth writes.</summary>
		public static void DrawOpaque(GraphicsDevice gd, AlphaTestEffect fx)
		{
			Vector3 right = Camera3D.ToRender(Camera3D.FlatRight);
			Texture2D current = null;
			foreach (var s in sprites) {
				if (s.Blend)
					continue;
				if (s.Tex != current) {
					current = s.Tex;
					fx.Texture = current;
					fx.CurrentTechnique.Passes[0].Apply();
				}
				Emit(gd, s, right, Vector3.Up);
			}
		}

		/// <summary>Translucent and vanilla-rendered sprites, far to near, alpha blended like vanilla's SpriteBatch.</summary>
		public static void DrawBlended(GraphicsDevice gd, BasicEffect fx)
		{
			Vector3 right = Camera3D.ToRender(Camera3D.FlatRight);
			var blended = sprites.FindAll(s => s.Blend);
			blended.Sort((a, b) => b.Dist.CompareTo(a.Dist));
			Texture2D current = null;
			foreach (var s in blended) {
				if (s.Tex != current) {
					current = s.Tex;
					fx.Texture = current;
					fx.CurrentTechnique.Passes[0].Apply();
				}
				Emit(gd, s, right, Vector3.Up);
			}
		}

		/// <summary>
		/// First-person view model of the held item, held in the right hand like in first-person games: the sprite lies in a
		/// vertical plane that points forward and slightly toward the crosshair, so it is seen in perspective. The pose comes
		/// from the vanilla use style (swing, aim, stab, eat/drink).
		/// </summary>
		public static void DrawHeldItem(GraphicsDevice gd, AlphaTestEffect fx)
		{
			Player p = Main.LocalPlayer;
			if (Camera3D.ThirdPerson || p.dead || !p.active)
				return;
			Item it = p.HeldItem;
			if (it == null || it.type <= ItemID.None)
				return;
			Main.instance.LoadItem(it.type);
			Texture2D tex = TextureAssets.Item[it.type].Value;
			Rectangle src = Main.itemAnimations[it.type] != null ? Main.itemAnimations[it.type].GetFrame(tex) : tex.Bounds;
			if (src.Width <= 0 || src.Height <= 0)
				return;

			Matrix inv = Matrix.Invert(Camera3D.View);
			Vector3 R = inv.Right, U = inv.Up, F = inv.Forward, E = inv.Translation;
			// world units per sprite pixel: a 32 px sword is about half a tile long in the hand
			float px = MathHelper.Clamp(0.5f / Math.Max(src.Width, src.Height), 0.011f, 0.02f) * Math.Max(0.6f, it.scale);
			float w = src.Width * px, h = src.Height * px;

			bool using_ = p.itemAnimation > 0 && p.itemAnimationMax > 0;
			float t = using_ ? 1f - p.itemAnimation / (float)p.itemAnimationMax : 0f;
			float bump = using_ ? MathF.Sin(t * MathHelper.Pi) : 0f;
			float walk = Math.Abs(p.velocity.X) + Math.Abs(p.GetModPlayer<DepthPlayer>().VZ) * 16f;
			float bob = walk > 0.1f && p.velocity.Y == 0f ? MathF.Sin((float)Main.timeForVisualEffects * 0.25f) * 0.012f : 0f;

			Vector3 hand = E + R * 0.27f - U * (0.27f + bob) + F * 0.42f;
			// main direction of the item: from the hand forward, up and a little toward the crosshair
			Vector3 dir = Vector3.Normalize(F * 0.8f + U * 0.55f - R * 0.22f);
			bool diagonalSprite = true; // most item sprites point to the upper right
			switch (it.useStyle) {
				case ItemUseStyleID.Swing:
				case ItemUseStyleID.MowTheLawn:
				case ItemUseStyleID.Guitar:
					if (using_) {
						// overhead swing: raised back, then down and forward
						float ang = MathHelper.Lerp(0.75f, -1.5f, t);
						dir = Vector3.TransformNormal(dir, Matrix.CreateFromAxisAngle(Vector3.Normalize(R - F * 0.2f), ang));
						hand += F * 0.06f * bump;
					}
					break;
				case ItemUseStyleID.Shoot:
					// barrel / bow pointing at the crosshair
					dir = Vector3.Normalize(F * 0.95f - R * 0.18f + U * 0.06f);
					diagonalSprite = false;
					hand += U * 0.07f - F * 0.07f * bump;
					break;
				case ItemUseStyleID.Thrust:
				case ItemUseStyleID.Rapier:
					dir = Vector3.Normalize(F * 0.95f - R * 0.2f + U * 0.12f);
					hand += F * 0.3f * bump;
					break;
				case ItemUseStyleID.EatFood:
				case ItemUseStyleID.DrinkLong:
				case ItemUseStyleID.DrinkOld:
				case ItemUseStyleID.DrinkLiquid:
				case ItemUseStyleID.HoldUp:
				case ItemUseStyleID.RaiseLamp:
					dir = Vector3.Normalize(F * 0.45f + U * 0.85f - R * 0.25f);
					hand += U * 0.12f * bump - R * 0.1f * bump;
					break;
			}

			// plane of the sprite: contains dir and the up direction (perpendicular to dir), i.e. a vertical blade
			Vector3 side = U - Vector3.Dot(U, dir) * dir;
			if (side.LengthSquared() < 1e-4f)
				side = -F;
			side.Normalize();
			Vector3 ax, ay; // sprite x (to the right) and y (up) axes in the world
			if (diagonalSprite) {
				// sprite diagonal (handle bottom-left -> tip top-right) along dir
				ax = Vector3.Normalize(dir - side);
				ay = Vector3.Normalize(dir + side);
			}
			else {
				ax = dir;
				ay = side;
			}
			// the handle sits a little inside the bottom-left corner
			Vector3 o = hand - ax * (w * 0.15f) - ay * (h * (diagonalSprite ? 0.15f : 0.5f));
			Vector3 bl = o, brc = o + ax * w, tl = o + ay * h, tr = o + ax * w + ay * h;

			Color light = it.GetAlpha(LightAt(Camera3D.HeadTile));
			float iw = 1f / tex.Width, ih = 1f / tex.Height;
			float u0 = src.X * iw, u1 = src.Right * iw, v0 = src.Y * ih, v1 = src.Bottom * ih;
			quad[0] = new VertexPositionColorTexture(tl, light, new Vector2(u0, v0));
			quad[1] = new VertexPositionColorTexture(tr, light, new Vector2(u1, v0));
			quad[2] = new VertexPositionColorTexture(brc, light, new Vector2(u1, v1));
			quad[3] = quad[0];
			quad[4] = quad[2];
			quad[5] = new VertexPositionColorTexture(bl, light, new Vector2(u0, v1));
			bool fog = fx.FogEnabled;
			fx.FogEnabled = false;
			fx.Texture = tex;
			fx.CurrentTechnique.Passes[0].Apply();
			gd.DepthStencilState = DepthStencilState.None;
			gd.RasterizerState = RasterizerState.CullNone;
			gd.DrawUserPrimitives(PrimitiveType.TriangleList, quad, 0, 2);
			fx.FogEnabled = fog;
		}

		/// <summary>Camera-facing quad (sun, moon).</summary>
		public static void DrawBillboard(GraphicsDevice gd, Effect fx, Texture2D tex, Rectangle src, Vector3 renderCenter, float size, Color color)
		{
			Matrix inv = Matrix.Invert(Camera3D.View);
			Vector3 r = inv.Right * (size / 2f), u = inv.Up * (size / 2f);
			float iw = 1f / tex.Width, ih = 1f / tex.Height;
			float u0 = src.X * iw, u1 = src.Right * iw, v0 = src.Y * ih, v1 = src.Bottom * ih;
			var tl = new VertexPositionColorTexture(renderCenter - r + u, color, new Vector2(u0, v0));
			var tr = new VertexPositionColorTexture(renderCenter + r + u, color, new Vector2(u1, v0));
			var br = new VertexPositionColorTexture(renderCenter + r - u, color, new Vector2(u1, v1));
			var bl = new VertexPositionColorTexture(renderCenter - r - u, color, new Vector2(u0, v1));
			quad[0] = tl; quad[1] = tr; quad[2] = br;
			quad[3] = tl; quad[4] = br; quad[5] = bl;
			fx.CurrentTechnique.Passes[0].Apply();
			gd.DrawUserPrimitives(PrimitiveType.TriangleList, quad, 0, 2);
		}
	}
}
