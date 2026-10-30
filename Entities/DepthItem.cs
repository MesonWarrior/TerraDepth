using System;
using TerraDepth.Players;
using TerraDepth.World;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace TerraDepth.Entities
{
	/// <summary>
	/// Dropped item depth. Items lie where they are: they fall and collide on their own slice (see <see cref="SliceSwap"/>),
	/// and are only pulled in when the player is within grab range in 3D, then they fly to the player in depth too.
	/// </summary>
	public class DepthItem : GlobalItem
	{
		public override bool InstancePerEntity => true;

		public float Z = -1f;

		public override void OnSpawn(Item item, IEntitySource source)
		{
			if (!Voxel.Ready)
				return;
			if (Voxel.OperatingZ >= 0)
				Z = Voxel.OperatingZ + 0.5f;
			else if (source is EntitySource_Parent p && p.Entity != null)
				Z = DepthPlayer.ZOf(p.Entity);
			else if (Voxel.WindowOpen)
				Z = Voxel.WinZ + 0.5f;
			else
				Z = Voxel.ZC + 0.5f;
		}

		private void EnsureZ()
		{
			if (Z < 0f)
				Z = Voxel.WindowOpen ? Voxel.WinZ + 0.5f : Voxel.ZC + 0.5f;
		}

		/// <summary>Updates an item with its own slice in Main.tile when it lies at another depth.</summary>
		public static void OnUpdateItem(On_Item.orig_UpdateItem orig, Item self, int i)
		{
			if (!DepthWorldSystem.Active || !self.active) {
				orig(self, i);
				return;
			}
			var d = self.GetGlobalItem<DepthItem>();
			d.EnsureZ();
			int cx = (int)(self.Center.X / 16f), cy = (int)(self.Center.Y / 16f);
			if (!SliceSwap.Needed(cx, cy, d.Z)) {
				orig(self, i);
				return;
			}
			int m = 2 + (int)(self.velocity.Length() / 16f);
			SliceSwap.Run((int)(self.position.X / 16f) - m, (int)(self.position.Y / 16f) - m,
				(int)((self.position.X + self.width) / 16f) + m, (int)((self.position.Y + self.height) / 16f) + m, d.Z, () => orig(self, i));
		}

		public override void Update(Item item, ref float gravity, ref float maxFallSpeed)
		{
			if (!DepthWorldSystem.Active)
				return;
			EnsureZ();
			// vanilla pulls a grabbed item toward the player in 2D; pull it in depth as well
			if (item.beingGrabbed && item.playerIndexTheItemIsReservedFor >= 0 && item.playerIndexTheItemIsReservedFor < Main.maxPlayers) {
				float pz = Main.player[item.playerIndexTheItemIsReservedFor].GetModPlayer<DepthPlayer>().Z;
				Z += Math.Clamp(pz - Z, -0.3f, 0.3f);
			}
		}

		/// <summary>Depth distance between the item and the player's body, in pixels.</summary>
		private float DepthGapPx(Player player) => Math.Max(0f, Math.Abs(player.GetModPlayer<DepthPlayer>().Z - Z) - DepthPlayer.HalfThickness) * 16f;

		/// <summary>Vanilla measures the grab range in 2D; shrink it so that it is the same range measured in 3D.</summary>
		public override void GrabRange(Item item, Player player, ref int grabRange)
		{
			if (!DepthWorldSystem.Active || Z < 0f)
				return;
			float gap = DepthGapPx(player);
			grabRange = gap > grabRange ? -1000 : (int)MathF.Sqrt(grabRange * grabRange - gap * gap);
		}

		public override bool CanPickup(Item item, Player player)
		{
			if (!DepthWorldSystem.Active || Z < 0f)
				return true;
			// out of 3D grab range: neither pulled nor picked up, even when overlapping in 2D
			return player.GetItemGrabRange(item) >= 0;
		}
	}
}
