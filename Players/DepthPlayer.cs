using System;
using Microsoft.Xna.Framework;
using TerraDepth.Common;
using TerraDepth.Rendering;
using TerraDepth.World;
using Terraria;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Audio;
using Terraria.ModLoader;

namespace TerraDepth.Players
{
	public class DepthPlayer : ModPlayer
	{
		/// <summary>Depth of the body center, in tiles.</summary>
		public float Z;
		public float VZ;
		private float moveZInput;
		public const float HalfThickness = 0.3f;

		// other-slice operation state
		private bool swapped;
		private int swapX0, swapY0, swapZ;
		private const int SwapR = 4;
		private VTile[] swapSaved;
		private bool fakeWall;
		private int fakeX, fakeY;

		public int SliceZ => Camera3D.ClampZ(Z);

		public override void Initialize()
		{
			Z = Voxel.Ready ? Voxel.ZC + 0.5f : 0.5f;
			VZ = 0;
		}

		public void ResetToCenterSlice()
		{
			Z = Voxel.ZC + 0.5f;
			VZ = 0;
		}

		public override void OnEnterWorld()
		{
			ResetToCenterSlice();
			Camera3D.Yaw = 0f;
			Camera3D.Pitch = -0.15f;
			Camera3D.ThirdPerson = ModContent.GetInstance<DepthClientConfig>().StartInThirdPerson;
		}

		public override void ProcessTriggers(TriggersSet triggersSet)
		{
			if (Input3D.ToggleCameraPressed)
				Camera3D.ThirdPerson = !Camera3D.ThirdPerson;
			if (Input3D.Toggle3DPressed) {
				var cfg = ModContent.GetInstance<DepthClientConfig>();
				cfg.Enabled3D = !cfg.Enabled3D;
				// While 2D, the player walks in the base world, which is the center slice.
				ResetToCenterSlice();
				Main.NewText(cfg.Enabled3D ? "TerraDepth: 3D ON" : "TerraDepth: 3D OFF (base 2D slice)", 150, 220, 255);
			}
		}

		/// <summary>A container opened in a slice other than the live one (vanilla would close it at once).</summary>
		public bool VirtualChest;
		public float VirtualChestZ;

		// while such a container is open its tiles stand in Main.tile, so vanilla's chest UI and checks see a real chest
		private bool chestSwapped;
		private int chestSwapX, chestSwapY, chestSwapW, chestSwapH;
		private VTile[] chestSaved = new VTile[6];
		private VTile[] chestWritten = new VTile[6];

		public void ApplyChestSwap()
		{
			if (chestSwapped || !VirtualChest || Player.chest < 0)
				return;
			int z = Math.Clamp((int)Math.Floor(VirtualChestZ), 0, Voxel.D - 1);
			VTile anchor = Voxel.Get(Player.chestX, Player.chestY, z);
			chestSwapW = anchor.HasTile && anchor.TileType < TileLoader.TileCount && TileID.Sets.BasicDresser[anchor.TileType] ? 3 : 2;
			chestSwapH = 2;
			chestSwapX = Player.chestX;
			chestSwapY = Player.chestY;
			for (int i = 0; i < chestSwapW; i++)
				for (int j = 0; j < chestSwapH; j++) {
					int x = chestSwapX + i, y = chestSwapY + j;
					if (!WorldGen.InWorld(x, y))
						continue;
					Tile t = Main.tile[x, y];
					int k = i * chestSwapH + j;
					chestSaved[k] = VTile.From(t);
					chestWritten[k] = Voxel.Get(x, y, z);
					chestWritten[k].WriteTo(t);
				}
			chestSwapped = true;
		}

		public void RemoveChestSwap()
		{
			if (!chestSwapped)
				return;
			chestSwapped = false;
			for (int i = 0; i < chestSwapW; i++)
				for (int j = 0; j < chestSwapH; j++) {
					int x = chestSwapX + i, y = chestSwapY + j;
					if (!WorldGen.InWorld(x, y))
						continue;
					chestSaved[i * chestSwapH + j].WriteTo(Main.tile[x, y]);
				}
		}

		public override void SetControls()
		{
			if (!DepthWorldSystem.Active)
				return;
			if (Player.chest == -1) {
				VirtualChest = false;
				RemoveChestSwap();
			}
			if (Player.whoAmI == Main.myPlayer && Player.controlUseTile && Player.releaseUseTile && Input3D.HasTarget && Input3D.LookActive)
				TryOpenOtherSliceContainer();
			// With a free cursor (inventory, chest, chat open) right clicks in the world would hit an arbitrary 2D spot
			// of the live slice (and could close the chest just opened); only the UI gets them then.
			if (Player.whoAmI == Main.myPlayer && !Input3D.LookActive)
				Player.controlUseTile = false;
			float f = (Input3D.KeyForward ? 1 : 0) - (Input3D.KeyBack ? 1 : 0);
			float s = (Input3D.KeyRight ? 1 : 0) - (Input3D.KeyLeft ? 1 : 0);
			Vector3 m = Camera3D.FlatForward * f + Camera3D.FlatRight * s;
			if (m.LengthSquared() > 1f)
				m.Normalize();
			Player.controlRight = m.X > 0.3f;
			Player.controlLeft = m.X < -0.3f;
			moveZInput = m.Z;

			bool onRope = OnRope();
			Player.controlUp = onRope && Input3D.KeyForward || Player.grappling[0] >= 0 && Input3D.KeyForward;
			Player.controlDown = Input3D.KeyDown || onRope && Input3D.KeyBack;
			if (onRope) {
				// climbing: W/S go up/down instead of moving in depth
				moveZInput = 0f;
				Player.controlLeft = Player.controlRight = false;
				if (s != 0f) {
					Player.controlRight = s > 0;
					Player.controlLeft = s < 0;
				}
			}
		}

		private bool OnRope()
		{
			if (Player.pulley)
				return true;
			Point c = Player.Center.ToTileCoordinates();
			if (!WorldGen.InWorld(c.X, c.Y))
				return false;
			Tile t = Main.tile[c.X, c.Y];
			return t.HasTile && Main.tileRope[t.TileType];
		}

		public override void PostUpdate()
		{
			if (Player.whoAmI != Main.myPlayer || !DepthWorldSystem.Active)
				return;
			if (Player.dead) {
				VZ = 0;
				return;
			}
			float speed = Math.Max(Player.maxRunSpeed, Player.accRunSpeed) / 16f;
			if (Player.mount.Active)
				speed = Math.Max(speed, Player.mount.RunSpeed / 16f);
			float target = moveZInput * speed;
			float accel = Player.velocity.Y == 0f ? 0.25f : 0.12f;
			if (GrapplePull(out float pull)) {
				// vanilla pulls toward the hooks in 2D; the depth part of the pull, at the same speed
				target = pull;
				accel = 1f;
			}
			VZ += (target - VZ) * accel;
			if (Math.Abs(VZ) < 0.001f)
				VZ = 0f;
			if (VZ != 0f)
				MoveZ(VZ);
		}

		private bool GrapplePull(out float vz)
		{
			vz = 0f;
			// vanilla clears grapCount just before PostUpdate, so the latched hooks (ai[0] 2) are looked up directly
			float hz = 0f;
			int n = 0;
			Vector2 hc = Vector2.Zero;
			foreach (Projectile proj in Main.ActiveProjectiles) {
				if (proj.owner != Player.whoAmI || proj.aiStyle != ProjAIStyleID.Hook && !Main.projHook[proj.type] || proj.ai[0] != 2f)
					continue;
				float z = proj.GetGlobalProjectile<Entities.DepthProjectile>().Z;
				if (z < 0f)
					continue;
				hz += z;
				hc += proj.Center;
				n++;
			}
			if (n == 0)
				return false;
			hz /= n;
			hc /= n;
			float dz = hz - Z;
			float dist = new Vector3((hc - Player.Center) / 16f, dz).Length();
			const float speed = 12f / 16f; // tiles per tick, about vanilla's hook pull
			vz = dist < 0.05f ? 0f : dz / dist * speed;
			if (Math.Abs(vz) > Math.Abs(dz))
				vz = dz;
			return true;
		}

		private void MoveZ(float dz)
		{
			float nz = Math.Clamp(Z + dz, HalfThickness + 0.05f, Voxel.D - HalfThickness - 0.05f);
			if (BodyFree(nz, 0)) {
				Z = nz;
				return;
			}
			// step up one block, like vanilla does horizontally
			bool grounded = Player.velocity.Y == 0f || Player.sliding;
			if (grounded && BodyFree(nz, -16) && BodyFree(Z, -16)) {
				Player.position.Y -= 16f;
				Player.gfxOffY += 16f;
				Z = nz;
				return;
			}
			VZ = 0f;
		}

		private bool BodyFree(float z, int yOffsetPx)
		{
			int x0 = (int)(Player.position.X / 16f), x1 = (int)((Player.position.X + Player.width - 1) / 16f);
			int y0 = (int)((Player.position.Y + yOffsetPx) / 16f), y1 = (int)((Player.position.Y + yOffsetPx + Player.height - 1) / 16f);
			int z0 = (int)Math.Floor(z - HalfThickness), z1 = (int)Math.Floor(z + HalfThickness);
			for (int cz = z0; cz <= z1; cz++) {
				if (cz < 0 || cz >= Voxel.D)
					return false;
				for (int x = x0; x <= x1; x++)
					for (int y = y0; y <= y1; y++)
						if (Voxel.IsSolid(x, y, cz))
							return false;
			}
			return true;
		}

		// ------------------------------------------------------------------ acting on other slices

		public override bool CanUseItem(Item item)
		{
			if (!DepthWorldSystem.Active || Player.whoAmI != Main.myPlayer)
				return true;
			// Clicking the world with a free cursor would hit the wrong 2D position.
			if (!Input3D.LookActive)
				return false;
			if (Input3D.ToolMode && Input3D.HasTarget && Input3D.ActZ != Voxel.WinZ && item.createTile >= 0 && item.createTile < TileLoader.TileCount) {
				int t = item.createTile;
				// Containers and multi-tile objects have position-keyed side data (chests, tile entities):
				// they can only be placed in the slice the player stands in.
				if (Main.tileContainer[t] || TileID.Sets.BasicChest[t] || Main.tileFrameImportant[t] && !Main.tileCut[t] && t != TileID.Torches)
					return false;
			}
			return true;
		}

		public override bool PreItemCheck()
		{
			swapped = false;
			fakeWall = false;
			if (Player.whoAmI != Main.myPlayer || !DepthWorldSystem.Active || !Input3D.ToolMode || !Input3D.HasTarget)
				return true;
			int z = Input3D.ActZ;
			if (!Voxel.InWindow(Input3D.ActX, Input3D.ActY))
				return true;
			Voxel.OperatingZ = z;
			if (z != Voxel.WinZ) {
				// Temporarily put the target slice around the target cell into Main.tile so vanilla mining/placing works on it.
				swapX0 = Input3D.ActX - SwapR;
				swapY0 = Input3D.ActY - SwapR;
				swapZ = z;
				int n = SwapR * 2 + 1;
				swapSaved ??= new VTile[n * n];
				for (int i = 0; i < n; i++) {
					for (int j = 0; j < n; j++) {
						int x = swapX0 + i, y = swapY0 + j;
						if (!WorldGen.InWorld(x, y))
							continue;
						Tile t = Main.tile[x, y];
						swapSaved[i * n + j] = VTile.From(t);
						Voxel.GetStored(x, y, z).WriteTo(t);
					}
				}
				swapped = true;
			}
			// Vanilla wants support in the same 2D slice. When the block we aim at is in front or behind (along Z),
			// lend the target cell a temporary wall so vanilla accepts it.
			Item held = Player.HeldItem;
			if (held.createTile >= 0 && Input3D.Target.NZ != 0 && WorldGen.InWorld(Input3D.ActX, Input3D.ActY)) {
				Tile t = Main.tile[Input3D.ActX, Input3D.ActY];
				if (t.WallType == 0) {
					t.WallType = WallID.Stone;
					fakeWall = true;
					fakeX = Input3D.ActX;
					fakeY = Input3D.ActY;
				}
			}
			return true;
		}

		public override void PostItemCheck()
		{
			if (fakeWall) {
				fakeWall = false;
				Tile t = Main.tile[fakeX, fakeY];
				if (t.WallType == WallID.Stone)
					t.WallType = 0;
			}
			if (!swapped) {
				Voxel.OperatingZ = -1;
				return;
			}
			swapped = false;
			int n = SwapR * 2 + 1;
			for (int i = 0; i < n; i++) {
				for (int j = 0; j < n; j++) {
					int x = swapX0 + i, y = swapY0 + j;
					if (!WorldGen.InWorld(x, y))
						continue;
					Tile t = Main.tile[x, y];
					VTile now = VTile.From(t);
					VTile before = Voxel.GetStored(x, y, swapZ);
					swapSaved[i * n + j].WriteTo(t);
					if (!VTile.SameContent(now, before))
						Voxel.Set(x, y, swapZ, now);
				}
			}
			Voxel.OperatingZ = -1;
		}

		/// <summary>3D version of Collision.EmptyTile: entities only block a cell when they overlap it in depth too.</summary>
		public static bool EmptyTile3D(int i, int j, bool ignoreTiles, float cellZ)
		{
			if (Main.tile[i, j].HasTile && !ignoreTiles)
				return false;
			var r = new Rectangle(i * 16, j * 16, 16, 16);
			for (int k = 0; k < Main.maxPlayers; k++) {
				Player p = Main.player[k];
				if (p.active && !p.dead && !p.ghost && r.Intersects(p.Hitbox)
					&& Math.Abs(p.GetModPlayer<DepthPlayer>().Z - cellZ) < 0.5f + HalfThickness)
					return false;
			}
			foreach (NPC npc in Main.ActiveNPCs) {
				if (r.Intersects(npc.Hitbox) && Math.Abs(ZOf(npc) - cellZ) < 0.5f + DepthRadius(npc) - 0.3f)
					return false;
			}
			return true;
		}

		private static bool LockedChest(in VTile v, int x, int y)
		{
			ushort t = v.TileType;
			if (t >= TileID.Count)
				return TileLoader.IsLockedChest(x, y, t);
			int fx = v.State.TileFrameX;
			if (t == TileID.Containers)
				return fx >= 72 && fx <= 106 || fx >= 144 && fx <= 178 || fx >= 828 && fx <= 1006 || fx >= 1296 && fx <= 1330
					|| fx >= 1368 && fx <= 1402 || fx >= 1440 && fx <= 1474;
			if (t == TileID.Containers2)
				return fx / 36 == 13;
			return false;
		}

		/// <summary>Right click on a chest or dresser that is not in the live slice: open it directly by its position.</summary>
		private void TryOpenOtherSliceContainer()
		{
			var h = Input3D.Target;
			if (h.Z == Voxel.WinZ && Voxel.InWindow(h.X, h.Y))
				return; // live slice: vanilla handles it
			VTile v = Voxel.Get(h.X, h.Y, h.Z);
			if (!v.HasTile || v.TileType >= TileLoader.TileCount || !Main.tileContainer[v.TileType])
				return;
			int fx = v.State.TileFrameX, fy = v.State.TileFrameY;
			int left, top;
			if (TileID.Sets.BasicDresser[v.TileType]) {
				left = h.X - fx % 54 / 18;
				top = h.Y - fy % 36 / 18;
			}
			else {
				left = h.X - fx % 36 / 18;
				top = h.Y - fy % 36 / 18;
			}
			// consume the click: the button stays held but is no longer "fresh", so vanilla does not use the live-slice tile
			// at this position now, nor take the same press as a second click next frame (which would close the chest)
			Player.releaseUseTile = false;
			if (LockedChest(Voxel.Get(left, top, h.Z), left, top)) {
				Main.NewText("Locked", 255, 200, 100);
				return;
			}
			int idx = Chest.FindChest(left, top);
			if (idx < 0)
				return;
			if (Player.chest == idx) {
				Player.chest = -1;
				VirtualChest = false;
				RemoveChestSwap();
				SoundEngine.PlaySound(SoundID.MenuClose);
			}
			else {
				RemoveChestSwap();
				Player.OpenChest(left, top, idx);
				VirtualChest = true;
				VirtualChestZ = h.Z + 0.5f;
				ApplyChestSwap();
				SoundEngine.PlaySound(SoundID.MenuOpen);
			}
			Recipe.FindRecipes();
		}

		/// <summary>Vanilla closes a chest whose tile is not in Main.tile; for other-slice chests check the 3D distance instead.</summary>
		public static void OnHandleChestRange(On_Player.orig_HandleBeingInChestRange orig, Player self)
		{
			var dp = self.GetModPlayer<DepthPlayer>();
			if (!DepthWorldSystem.Active || !dp.VirtualChest || self.chest < 0 || self.whoAmI != Main.myPlayer) {
				orig(self);
				return;
			}
			var chestCenter = new Vector3(self.chestX + 1f, self.chestY + 1f, dp.VirtualChestZ);
			var me = new Vector3(self.Center.X / 16f, self.Center.Y / 16f, dp.Z);
			if (Vector3.Distance(chestCenter, me) > Player.tileRangeX + 2f) {
				self.chest = -1;
				dp.VirtualChest = false;
				dp.RemoveChestSwap();
				SoundEngine.PlaySound(SoundID.MenuClose);
				Recipe.FindRecipes();
			}
		}

		// ------------------------------------------------------------------ 3D combat checks

		public static float ZOf(Entity e)
		{
			switch (e) {
				case Player p: return p.GetModPlayer<DepthPlayer>().Z;
				case NPC n: return n.GetGlobalNPC<Entities.DepthNPC>().Z;
				case Projectile pr: return pr.GetGlobalProjectile<Entities.DepthProjectile>().Z;
				case Item it: return it.GetGlobalItem<Entities.DepthItem>().Z;
			}
			return Voxel.ZC + 0.5f;
		}

		public static float DepthRadius(Entity e) => Math.Max(e.width, e.height) / 32f * 0.6f + 0.3f;

		public override bool? CanHitNPCWithItem(Item item, NPC target)
		{
			if (!DepthWorldSystem.Active)
				return null;
			return Math.Abs(ZOf(target) - Z) <= 1.6f + DepthRadius(target) ? null : false;
		}

		public override bool CanBeHitByNPC(NPC npc, ref int cooldownSlot)
		{
			if (!DepthWorldSystem.Active)
				return true;
			return Math.Abs(ZOf(npc) - Z) <= HalfThickness + DepthRadius(npc);
		}

		public override bool CanBeHitByProjectile(Projectile proj)
		{
			if (!DepthWorldSystem.Active)
				return true;
			return Math.Abs(ZOf(proj) - Z) <= HalfThickness + DepthRadius(proj);
		}
	}
}
