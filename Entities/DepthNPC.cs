using System;
using Microsoft.Xna.Framework;
using TerraDepth.Players;
using TerraDepth.World;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraDepth.Entities
{
	/// <summary>
	/// NPC depth. Vanilla AI is 2D, so:
	///  - hostile NPCs with a "chase the player" AI run it in the vertical plane through them and the player
	///    (see <see cref="Plane3D"/>): charges, hops and flights really go toward the player in 3D;
	///  - other hostile NPCs walk/fly toward the player's depth at their own pace;
	///  - every NPC stands and collides on its own slice (see <see cref="SliceSwap"/>), so critters and town NPCs
	///    simply live at their depth;
	///  - natural spawns are spread around the player in 3D, and "near the player" checks use 3D distances.
	/// </summary>
	public class DepthNPC : GlobalNPC
	{
		public override bool InstancePerEntity => true;

		public float Z = -1f;

		// plane mapping state
		private bool mapped;
		private float frameVX;          // horizontal speed along the plane, as the vanilla AI sees it (px/frame)
		private Vector2 planeDir;       // unit horizontal direction toward the player: (world X, depth Z)
		private float setVX;            // world X velocity we handed to vanilla movement
		private Vector2 savedTargetPos;
		private int savedTarget = -1;
		/// <summary>Horizontal direction of travel in 3D (world X, depth Z), for drawing.</summary>
		public Vector2 Heading;
		public bool HasHeading;

		private void EnsureZ(NPC npc)
		{
			if (Z < 0f)
				Z = Voxel.Ready ? DefaultZ(npc) : 0.5f;
		}

		private static float DefaultZ(NPC npc)
		{
			if (Voxel.WindowOpen && Voxel.InWindow((int)(npc.Center.X / 16), (int)(npc.Center.Y / 16)))
				return Voxel.WinZ + 0.5f;
			return Voxel.ZC + 0.5f;
		}

		public static bool IsHostile(NPC npc) => !npc.friendly && !npc.townNPC && (npc.damage > 0 || npc.boss) && !NPCLoaderCountsAsCritter(npc);

		private static bool NPCLoaderCountsAsCritter(NPC npc) => npc.CountsAsACritter;

		public override void OnSpawn(NPC npc, IEntitySource source)
		{
			if (!Voxel.Ready)
				return;
			// Natural spawns outside the live window would land in the middle slice, far from the player's depth.
			if (source is EntitySource_SpawnNPC && DepthWorldSystem.Active && Voxel.WindowOpen
				&& !Voxel.InWindow((int)(npc.Center.X / 16), (int)(npc.Center.Y / 16))) {
				npc.active = false;
				return;
			}
			if (source is EntitySource_Parent parent && parent.Entity != null && parent.Entity != npc)
				Z = DepthPlayer.ZOf(parent.Entity);
			else
				Z = DefaultZ(npc);
			if (source is EntitySource_SpawnNPC && DepthWorldSystem.Active)
				SpreadSpawn(npc);
		}

		public override bool PreAI(NPC npc)
		{
			if (!DepthWorldSystem.Active)
				return true;
			EnsureZ(npc);
			Player target = Main.LocalPlayer;
			float pz = target.GetModPlayer<DepthPlayer>().Z;
			if (!IsHostile(npc)) {
				HasHeading = false;
				return true;
			}
			if (Plane3D(npc)) {
				BeginPlane(npc, target, pz);
				return true;
			}
			// other hostile AIs: they keep their 2D behaviour and close in on the player's depth at their own speed
			HasHeading = false;
			float speed = Math.Max(0.03f, Math.Abs(npc.velocity.X) / 16f);
			float step = Math.Clamp(pz - Z, -speed, speed);
			if (step != 0f && (npc.noTileCollide || BodyFree(npc, Z + step)))
				Z += step;
			return true;
		}

		public override void PostAI(NPC npc)
		{
			if (mapped)
				EndPlane(npc);
		}

		// ------------------------------------------------------------------ 2D AI in the plane toward the player

		/// <summary>AI styles that only chase the player (no linked segments or parent NPCs), safe to run in a rotated plane.</summary>
		private static bool Plane3D(NPC npc)
		{
			if (npc.realLife >= 0 && npc.realLife != npc.whoAmI)
				return false;
			switch (npc.aiStyle) {
				case NPCAIStyleID.Slime:
				case NPCAIStyleID.DemonEye:
				case NPCAIStyleID.Fighter:
				case NPCAIStyleID.EyeOfCthulhu:
				case NPCAIStyleID.Flying:
				case NPCAIStyleID.Bat:
				case NPCAIStyleID.KingSlime:
				case NPCAIStyleID.Vulture:
				case NPCAIStyleID.Unicorn:
				case NPCAIStyleID.QueenBee:
				case NPCAIStyleID.FlyingFish:
				case NPCAIStyleID.Mimic:
					return true;
			}
			return false;
		}

		/// <summary>
		/// Before the AI: the target player is put, for the AI only, in the vertical plane through this NPC and the player,
		/// on the NPC's right at the true horizontal 3D distance. The AI's horizontal speed is restored in that frame.
		/// </summary>
		private void BeginPlane(NPC npc, Player target, float pz)
		{
			if (npc.target != target.whoAmI || target.dead) {
				mapped = false;
				return;
			}
			// what vanilla movement/collision did to the world X velocity we gave it last frame
			if (savedTarget >= 0 && Math.Abs(planeDir.X) > 0.35f && Math.Abs(npc.velocity.X - setVX) > 0.01f)
				frameVX = npc.velocity.X / planeDir.X;
			var to = new Vector2(target.Center.X - npc.Center.X, (pz - Z) * 16f);
			float dist = to.Length();
			if (dist > 0.5f)
				planeDir = to / dist;
			else if (planeDir == Vector2.Zero)
				planeDir = new Vector2(1f, 0f);
			savedTarget = target.whoAmI;
			savedTargetPos = target.position;
			target.position.X = npc.Center.X + dist - target.width / 2f;
			npc.velocity.X = frameVX;
			mapped = true;
		}

		/// <summary>After the AI: its horizontal velocity goes toward the player in 3D, split into world X and depth.</summary>
		private void EndPlane(NPC npc)
		{
			mapped = false;
			if (savedTarget >= 0 && savedTarget < Main.maxPlayers)
				Main.player[savedTarget].position = savedTargetPos;
			frameVX = npc.velocity.X;
			setVX = planeDir.X * frameVX;
			npc.velocity.X = setVX;
			float dz = planeDir.Y * frameVX / 16f;
			if (Math.Abs(frameVX) > 0.05f) {
				Heading = planeDir * Math.Sign(frameVX);
				HasHeading = true;
			}
			if (dz == 0f)
				return;
			float nz = Math.Clamp(Z + dz, 0.5f, Voxel.D - 0.5f);
			if (npc.noTileCollide || BodyFree(npc, nz)) {
				Z = nz;
				return;
			}
			// blocked in depth: walkers jump over it like vanilla does when blocked sideways
			if (npc.velocity.Y == 0f && (npc.aiStyle == NPCAIStyleID.Fighter || npc.aiStyle == NPCAIStyleID.Unicorn))
				npc.velocity.Y = -6f;
		}

		/// <summary>Keeps vanilla's spawn search inside the live window, where Main.tile is the player's slice.</summary>
		public override void EditSpawnRange(Player player, ref int spawnRangeX, ref int spawnRangeY, ref int safeRangeX, ref int safeRangeY)
		{
			if (!DepthWorldSystem.Active || !Voxel.WindowOpen || player.whoAmI != Main.myPlayer)
				return;
			int px = (int)(player.position.X / 16f), py = (int)(player.position.Y / 16f);
			int roomX = Math.Min(px - Voxel.WinX, Voxel.WinX + Voxel.WinW - 1 - px) - 4;
			int roomY = Math.Min(py - Voxel.WinY, Voxel.WinY + Voxel.WinH - 1 - py) - 8;
			spawnRangeX = Math.Max(20, Math.Min(spawnRangeX, roomX));
			spawnRangeY = Math.Max(12, Math.Min(spawnRangeY, roomY));
			safeRangeX = Math.Min(safeRangeX, spawnRangeX - 12);
			safeRangeY = Math.Min(safeRangeY, spawnRangeY - 8);
		}

		private static bool BodyFree(NPC npc, float z)
		{
			int x0 = (int)(npc.position.X / 16f), x1 = (int)((npc.position.X + npc.width - 1) / 16f);
			int y0 = (int)(npc.position.Y / 16f), y1 = (int)((npc.position.Y + npc.height - 1) / 16f);
			int cz = (int)Math.Floor(z);
			if (cz < 0 || cz >= Voxel.D)
				return false;
			if (x1 - x0 > 6 || y1 - y0 > 6)
				return true;
			for (int x = x0; x <= x1; x++)
				for (int y = y0; y <= y1; y++)
					if (Voxel.IsSolid(x, y, cz))
						return false;
			return true;
		}

		/// <summary>
		/// Runs a vanilla check that measures the NPC's distance to the player in 2D with the NPC moved, for the duration of
		/// the check, so that its 2D horizontal distance equals its real distance in 3D (X and depth).
		/// </summary>
		private static void WithDistance3D(NPC self, Action check)
		{
			if (!DepthWorldSystem.Active || Main.netMode != NetmodeID.SinglePlayer) {
				check();
				return;
			}
			float z = self.GetGlobalNPC<DepthNPC>().Z;
			Player p = Main.LocalPlayer;
			float dzPx = (z - p.GetModPlayer<DepthPlayer>().Z) * 16f;
			if (z < 0f || Math.Abs(dzPx) < 16f) {
				check();
				return;
			}
			float dx = self.Center.X - p.Center.X;
			float d3 = MathF.Sqrt(dx * dx + dzPx * dzPx);
			Vector2 pos = self.position;
			self.position.X = p.Center.X + (dx >= 0f ? d3 : -d3) - self.width / 2f;
			try {
				check();
			}
			finally {
				self.position = pos;
			}
		}

		/// <summary>Spawn caps and despawning of distant NPCs use the 3D distance to the player.</summary>
		public static void OnCheckActive(On_NPC.orig_CheckActive orig, NPC self) => WithDistance3D(self, () => orig(self));

		/// <summary>Town NPCs lower spawn rates only for a player near them in 3D.</summary>
		public static void OnAddTownSlots(On_NPC.orig_AddIntoPlayersTownNPCSlots orig, NPC self) => WithDistance3D(self, () => orig(self));

		/// <summary>
		/// Vanilla picks a natural spawn point in the live slice at some horizontal distance from the player. The offset is
		/// turned around the player by a random angle in the horizontal X/depth plane (same distance), and the NPC is put on
		/// the ground found at that depth, so spawns surround the player in 3D.
		/// </summary>
		private void SpreadSpawn(NPC npc)
		{
			Player p = Main.LocalPlayer;
			if (npc.wet || npc.boss || npc.townNPC)
				return;
			float pz = p.GetModPlayer<DepthPlayer>().Z;
			float dx = (npc.Center.X - p.Center.X) / 16f;
			double angle = Main.rand.NextDouble() * Math.PI * 2.0;
			float nx = (float)(dx * Math.Cos(angle)), nz = (float)(dx * Math.Sin(angle));
			float z = pz + nz;
			if (z < 2f || z > Voxel.D - 2f)
				return;
			int tx = (int)((p.Center.X / 16f) + nx);
			int zc = (int)Math.Floor(z);
			int wTiles = Math.Max(1, (npc.width + 15) / 16), hTiles = Math.Max(1, (npc.height + 15) / 16);
			int footY = (int)((npc.position.Y + npc.height) / 16f); // row just below the NPC
			bool flyer = npc.noGravity;
			for (int k = 0; k <= 30; k++) {
				for (int sign = -1; sign <= 1; sign += 2) {
					int fy = footY + k * sign;
					if (fy - hTiles < 5 || fy >= Main.maxTilesY - 5)
						continue;
					if (!SpaceFree(tx, fy - hTiles, wTiles, hTiles, zc))
						continue;
					if (!flyer) {
						bool ground = false;
						for (int x = tx; x < tx + wTiles; x++)
							if (Voxel.Get(x, fy, zc).IsSolid || Voxel.Get(x, fy, zc).HasTile && Main.tileSolidTop[Voxel.Get(x, fy, zc).TileType])
								ground = true;
						if (!ground)
							continue;
					}
					npc.position = new Vector2(tx * 16 + (wTiles * 16 - npc.width) / 2f, fy * 16 - npc.height);
					Z = zc + 0.5f;
					return;
				}
			}
		}

		private static bool SpaceFree(int x0, int y0, int w, int h, int z)
		{
			for (int x = x0; x < x0 + w; x++)
				for (int y = y0; y < y0 + h; y++) {
					VTile v = Voxel.Get(x, y, z);
					if (v.IsSolid || v.LiquidAmount > 0 && v.LiquidType == LiquidID.Lava)
						return false;
				}
			return true;
		}

		/// <summary>NPCs at another depth than Main.tile's slice update against their own slice (see <see cref="SliceSwap"/>).</summary>
		public static void OnUpdateNPC(On_NPC.orig_UpdateNPC orig, NPC self, int i)
		{
			if (!DepthWorldSystem.Active || !self.active || self.noTileCollide) {
				orig(self, i);
				return;
			}
			var d = self.GetGlobalNPC<DepthNPC>();
			d.EnsureZ(self);
			int cx = (int)(self.Center.X / 16f), cy = (int)(self.Center.Y / 16f);
			if (!SliceSwap.Needed(cx, cy, d.Z)) {
				orig(self, i);
				return;
			}
			int m = 3 + (int)(self.velocity.Length() / 16f);
			SliceSwap.Run((int)(self.position.X / 16f) - m, (int)(self.position.Y / 16f) - m,
				(int)((self.position.X + self.width) / 16f) + m, (int)((self.position.Y + self.height) / 16f) + m, d.Z, () => orig(self, i));
		}

		public override bool CanHitPlayer(NPC npc, Player target, ref int cooldownSlot)
		{
			if (!DepthWorldSystem.Active)
				return true;
			EnsureZ(npc);
			return Math.Abs(Z - target.GetModPlayer<DepthPlayer>().Z) <= DepthPlayer.HalfThickness + DepthPlayer.DepthRadius(npc);
		}

		public override bool CanHitNPC(NPC npc, NPC target)
		{
			if (!DepthWorldSystem.Active)
				return true;
			EnsureZ(npc);
			return Math.Abs(Z - target.GetGlobalNPC<DepthNPC>().Z) <= DepthPlayer.DepthRadius(npc) + DepthPlayer.DepthRadius(target);
		}
	}
}
