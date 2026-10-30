using System;
using Microsoft.Xna.Framework;
using TerraDepth.Common;
using TerraDepth.Players;
using TerraDepth.World;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraDepth.Entities
{
	/// <summary>
	/// Projectile depth: player shots get a Z velocity from the 3D aim, enemy shots aim at the player's depth,
	/// held weapons/minions/pets stay with their owner. Off-slice projectiles collide with voxels themselves.
	/// </summary>
	public class DepthProjectile : GlobalProjectile
	{
		public override bool InstancePerEntity => true;

		public float Z = -1f;
		public float VZ;
		/// <summary>Held projectiles: depth offset per 2D pixel distance from the owner.</summary>
		private float heldSlope;
		private bool held;

		private static bool IsHeld(Projectile p)
		{
			if (p.owner < 0 || p.owner >= Main.maxPlayers)
				return false;
			Player o = Main.player[p.owner];
			return o.heldProj == p.whoAmI || p.aiStyle is ProjAIStyleID.Spear or ProjAIStyleID.Drill or ProjAIStyleID.Yoyo
				or ProjAIStyleID.Flail or ProjAIStyleID.HeldProjectile or ProjAIStyleID.ShortSword or ProjAIStyleID.Whip
				|| p.ownerHitCheck || ProjectileID.Sets.IsAWhip[p.type];
		}

		private static bool FollowsOwner(Projectile p) => p.minion || p.sentry || Main.projPet[p.type];

		private static bool IsHook(Projectile p) => p.aiStyle == ProjAIStyleID.Hook || Main.projHook[p.type];

		public override void OnSpawn(Projectile projectile, IEntitySource source)
		{
			if (!Voxel.Ready)
				return;
			Entity parent = source is EntitySource_Parent ps ? ps.Entity : null;
			if (parent == null && projectile.owner >= 0 && projectile.owner < Main.maxPlayers && projectile.friendly)
				parent = Main.player[projectile.owner];
			Z = parent != null ? DepthPlayer.ZOf(parent) : (Voxel.WindowOpen ? Voxel.WinZ + 0.5f : Voxel.ZC + 0.5f);
			held = IsHeld(projectile);

			bool fromLocalAim = parent is Player pl && pl.whoAmI == Main.myPlayer && DepthWorldSystem.Active
				&& (source is EntitySource_ItemUse || source is EntitySource_ItemUse_WithAmmo);
			if (fromLocalAim) {
				// from where the shot starts to the point under the crosshair (vanilla aims from the player at the cursor)
				Vector3 a = Input3D.ShotDirFrom(new Vector3(projectile.Center.X / 16f, projectile.Center.Y / 16f, Z));
				float xy = new Vector2(a.X, a.Y).Length();
				if (held) {
					heldSlope = a.Z / Math.Max(0.2f, xy) / 16f;
				}
				else if (!FollowsOwner(projectile) && projectile.velocity != Vector2.Zero) {
					float s = projectile.velocity.Length();
					float f = Math.Max(xy, 0.15f);
					projectile.velocity *= f;
					// vanilla aims a hook at the mouse, which points at the block being mined while a tool is held
					if (IsHook(projectile) && xy > 0.01f)
						projectile.velocity = new Vector2(a.X, a.Y) / xy * s * f;
					VZ = s * a.Z / 16f;
					// a hook only latches onto blocks of the slice it is in: it must not skip a slice in one tick
					if (IsHook(projectile) && Math.Abs(VZ) > 0.9f) {
						float k = 0.9f / Math.Abs(VZ);
						VZ *= k;
						projectile.velocity *= k;
					}
				}
			}
			else if (parent is NPC && projectile.hostile && DepthWorldSystem.Active) {
				Player t = Main.LocalPlayer;
				float s = Math.Max(1f, projectile.velocity.Length());
				float frames = Vector2.Distance(projectile.Center, t.Center) / s;
				VZ = (t.GetModPlayer<DepthPlayer>().Z - Z) / Math.Max(10f, frames);
			}
			else if (parent is Projectile pp) {
				VZ = pp.GetGlobalProjectile<DepthProjectile>().VZ;
			}
		}

		/// <summary>Direction of flight in tile space (x right, y down, z depth); held weapons point along their aim.</summary>
		public Vector3 Direction(Projectile projectile)
		{
			Vector2 v = projectile.velocity;
			if (held || IsHeld(projectile))
				return new Vector3(v.X / 16f, v.Y / 16f, heldSlope * v.Length());
			return new Vector3(v.X / 16f, v.Y / 16f, VZ);
		}

		/// <summary>Held projectiles: depth offset (tiles) per 2D pixel of distance from the owner.</summary>
		public float HeldSlope => heldSlope;

		/// <summary>Recent centres and depths of projectiles that leave a trail, so vanilla's afterimages can be put at their depth.</summary>
		public (Vector2 center, float z)[] Trail;
		public int TrailCount, TrailHead;
		private const int TrailMax = 48;

		public override void PostAI(Projectile projectile)
		{
			UpdateDepth(projectile);
			if (DepthWorldSystem.Active && Z >= 0f && ProjectileID.Sets.TrailingMode[projectile.type] >= 0) {
				Trail ??= new (Vector2, float)[TrailMax];
				TrailHead = (TrailHead + 1) % TrailMax;
				Trail[TrailHead] = (projectile.Center, Z);
				TrailCount = Math.Min(TrailCount + 1, TrailMax);
			}
		}

		/// <summary>Depth recorded where the projectile was nearest to a 2D point (an afterimage drawn at an old position).</summary>
		public float TrailDepthAt(Vector2 p)
		{
			float best = float.MaxValue, z = Z;
			for (int i = 0; i < TrailCount; i++) {
				var (c, tz) = Trail[(TrailHead - i + TrailMax) % TrailMax];
				float d = Vector2.DistanceSquared(c, p);
				if (d < best) {
					best = d;
					z = tz;
				}
			}
			return z;
		}

		private void UpdateDepth(Projectile projectile)
		{
			if (!DepthWorldSystem.Active)
				return;
			if (Z < 0f)
				Z = Voxel.WindowOpen ? Voxel.WinZ + 0.5f : Voxel.ZC + 0.5f;
			Player owner = projectile.owner >= 0 && projectile.owner < Main.maxPlayers ? Main.player[projectile.owner] : null;
			if (owner != null && owner.active && projectile.friendly && (held || IsHeld(projectile))) {
				float ownerZ = owner.GetModPlayer<DepthPlayer>().Z;
				float dist = Vector2.Distance(projectile.Center, owner.Center);
				if (owner.whoAmI == Main.myPlayer && heldSlope == 0f && Input3D.AimDir.Z != 0f) {
					Vector3 a = Input3D.ShotDirFrom(new Vector3(owner.Center.X / 16f, owner.Center.Y / 16f, ownerZ));
					heldSlope = a.Z / Math.Max(0.2f, new Vector2(a.X, a.Y).Length()) / 16f;
				}
				Z = ownerZ + Math.Clamp(heldSlope * dist, -8f, 8f);
				return;
			}
			if (IsHook(projectile)) {
				HookDepth(projectile, owner);
				return;
			}
			if (owner != null && owner.active && FollowsOwner(projectile)) {
				float oz = owner.GetModPlayer<DepthPlayer>().Z;
				Z += Math.Clamp(oz - Z, -0.12f, 0.12f);
				return;
			}
			Z += VZ;
			if (Z < 0f || Z >= Voxel.D) {
				projectile.Kill();
				return;
			}
			SteerHoming(projectile);
			lastVelocity = projectile.velocity;
		}

		private Vector2 lastVelocity;

		/// <summary>
		/// Hooks fly in 3D toward the crosshair (ai[0] 0), latch where their own slice has a block (see <see cref="OnUpdate"/>,
		/// ai[0] 2) and come back to the owner's depth when retracting (ai[0] 1). The owner is pulled in depth by DepthPlayer.
		/// </summary>
		private const float MaxHookTiles = 35f;

		private void HookDepth(Projectile p, Player owner)
		{
			if (p.ai[0] == 0f) {
				Z += VZ;
				// vanilla retracts a hook by its 2D distance only, which barely grows when it flies along depth
				bool tooFar = owner != null && new Vector3((p.Center - owner.Center) / 16f, Z - owner.GetModPlayer<DepthPlayer>().Z).Length() > MaxHookTiles;
				if (Z < 0.05f || Z > Voxel.D - 0.05f || tooFar) {
					Z = Math.Clamp(Z, 0.05f, Voxel.D - 0.05f);
					p.ai[0] = 1f;
					p.netUpdate = true;
				}
				return;
			}
			if (p.ai[0] == 1f && owner != null && owner.active) {
				float oz = owner.GetModPlayer<DepthPlayer>().Z;
				float speed = Math.Max(1f, p.velocity.Length());
				float frames = Math.Max(1f, Vector2.Distance(p.Center, owner.Center) / speed);
				Z += (oz - Z) / frames;
			}
		}

		/// <summary>
		/// Homing projectiles turn toward their target in 2D; give them the depth part of that turn. A projectile counts as
		/// homing when its direction changes sideways (not only by gravity) while an enemy is right ahead of it.
		/// </summary>
		private void SteerHoming(Projectile p)
		{
			if (!p.friendly || p.damage <= 0 || lastVelocity == Vector2.Zero)
				return;
			if (Math.Abs(p.velocity.X - lastVelocity.X) < 0.05f)
				return;
			float speed = p.velocity.Length();
			if (speed < 1f)
				return;
			Vector2 dir = p.velocity / speed;
			NPC best = null;
			float bestD = 30f * 16f;
			foreach (NPC n in Main.ActiveNPCs) {
				if (!n.CanBeChasedBy(p))
					continue;
				Vector2 to = n.Center - p.Center;
				float d = to.Length();
				if (d >= bestD || d < 1f || Vector2.Dot(to / d, dir) < 0.85f)
					continue;
				bestD = d;
				best = n;
			}
			if (best == null)
				return;
			float frames = Math.Max(1f, bestD / speed);
			VZ = Math.Clamp((best.GetGlobalNPC<DepthNPC>().Z - Z) / frames, -speed / 16f, speed / 16f);
		}

		/// <summary>Projectiles at another depth collide with their own slice (see <see cref="SliceSwap"/>).</summary>
		public static void OnUpdate(On_Projectile.orig_Update orig, Projectile self, int i)
		{
			// hooks look for blocks to latch onto themselves, without tile collision
			if (!DepthWorldSystem.Active || !self.active || !self.tileCollide && !IsHook(self)) {
				orig(self, i);
				return;
			}
			var d = self.GetGlobalProjectile<DepthProjectile>();
			int cx = (int)(self.Center.X / 16f), cy = (int)(self.Center.Y / 16f);
			if (d.Z < 0f || !SliceSwap.Needed(cx, cy, d.Z)) {
				orig(self, i);
				return;
			}
			// explosives destroy tiles around them when they die during this update
			int m = 2 + (int)(self.velocity.Length() / 16f) + (self.aiStyle == ProjAIStyleID.Explosive ? 12 : 0);
			SliceSwap.Run((int)(self.position.X / 16f) - m, (int)(self.position.Y / 16f) - m,
				(int)((self.position.X + self.width) / 16f) + m, (int)((self.position.Y + self.height) / 16f) + m, d.Z, () => orig(self, i));
		}

		public override bool? CanHitNPC(Projectile projectile, NPC target)
		{
			if (!DepthWorldSystem.Active || Z < 0f)
				return null;
			float tz = target.GetGlobalNPC<DepthNPC>().Z;
			float reach = DepthPlayer.DepthRadius(target) + DepthPlayer.DepthRadius(projectile) + (held ? 1.2f : 0f);
			return Math.Abs(tz - Z) <= reach ? null : false;
		}

		public override bool CanHitPlayer(Projectile projectile, Player target)
		{
			if (!DepthWorldSystem.Active || Z < 0f)
				return true;
			return Math.Abs(target.GetModPlayer<DepthPlayer>().Z - Z) <= DepthPlayer.HalfThickness + DepthPlayer.DepthRadius(projectile);
		}
	}
}
