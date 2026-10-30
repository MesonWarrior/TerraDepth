using System;
using TerraDepth.Common;
using TerraDepth.Players;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace TerraDepth.World
{
	/// <summary>Owns the voxel world lifetime: init, save/load and keeping the live window on the player's slice.</summary>
	public class DepthWorldSystem : ModSystem
	{
		public static int DefaultDepth => Math.Clamp(Main.maxTilesX * ModContent.GetInstance<DepthServerConfig>().DepthPercent / 100, 48, 1200);

		public static bool Active => Voxel.Ready && Main.netMode == NetmodeID.SinglePlayer && !Main.gameMenu && ModContent.GetInstance<DepthClientConfig>().Enabled3D;

		public override void OnWorldLoad()
		{
			// LoadWorldData (if this world has 3D data) and PostWorldLoad run after this.
			Voxel.Reset();
			ResetRenderer();
		}

		public override void PostWorldLoad()
		{
			if (!Voxel.Ready) {
				// first time this world is played with the mod
				Voxel.Init(DefaultDepth, Main.ActiveWorldFileData?.Seed ?? 1, null);
				Voxel.ConvertBase();
			}
			ReturnOldMan();
		}

		/// <summary>Earlier versions moved the Old Man to the spawn point with the town NPCs; put him back at the dungeon.</summary>
		private static void ReturnOldMan()
		{
			if (Main.dungeonX <= 0 || Main.dungeonY <= 0)
				return;
			var entrance = new Microsoft.Xna.Framework.Vector2(Main.dungeonX * 16 + 8, Main.dungeonY * 16);
			foreach (NPC n in Main.ActiveNPCs) {
				if (n.type != NPCID.OldMan || Microsoft.Xna.Framework.Vector2.Distance(n.Bottom, entrance) < 60 * 16)
					continue;
				n.Bottom = entrance;
				n.velocity = Microsoft.Xna.Framework.Vector2.Zero;
				n.GetGlobalNPC<Entities.DepthNPC>().Z = Voxel.ZC + 0.5f;
			}
		}

		public override void OnWorldUnload()
		{
			Voxel.Reset();
			ResetRenderer();
		}

		public override void ClearWorld()
		{
			Voxel.Reset();
			ResetRenderer();
		}

		private static void ResetRenderer()
		{
			if (!Main.dedServ)
				Main.QueueMainThreadAction(Rendering.Renderer3D.OnWorldChange);
		}

		public override void SaveWorldData(TagCompound tag) => Voxel.Save(tag);

		public override void LoadWorldData(TagCompound tag) => Voxel.Load(tag, DefaultDepth);

		public override void PostUpdatePlayers()
		{
			if (!Voxel.Ready || Main.netMode != NetmodeID.SinglePlayer)
				return;
			Player p = Main.LocalPlayer;
			var dp = p.GetModPlayer<DepthPlayer>();
			// an open chest from another slice must not end up in the live slice when the window is rebuilt
			dp.RemoveChestSwap();
			if (!Active) {
				Voxel.CloseWindow();
				return;
			}
			long t0 = Common.Perf.Now;
			Voxel.EnsureWindow((int)(p.Center.X / 16), (int)(p.Center.Y / 16), dp.SliceZ);
			Common.Perf.Lap("live slice window", t0);
			dp.ApplyChestSwap();
		}

		public override void PreSaveAndQuit()
		{
			// Leave the base world in Main.tile; the live slice goes into edits.
			if (Main.netMode == NetmodeID.SinglePlayer && !Main.dedServ)
				Main.LocalPlayer.GetModPlayer<DepthPlayer>().RemoveChestSwap();
			Voxel.CloseWindow();
		}
	}
}
