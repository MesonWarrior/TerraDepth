using System;
using Microsoft.Xna.Framework;
using TerraDepth.Players;
using TerraDepth.Rendering;
using TerraDepth.World;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace TerraDepth.Common
{
	public class TerraDepthMod : Mod
	{
		public static ModKeybind ToggleCamera, Toggle3D, FreeCursor, DropDown;

		public override void Load()
		{
			ToggleCamera = KeybindLoader.RegisterKeybind(this, "ToggleCamera", "F5");
			Toggle3D = KeybindLoader.RegisterKeybind(this, "Toggle3D", "F6");
			FreeCursor = KeybindLoader.RegisterKeybind(this, "FreeCursor", "LeftAlt");
			DropDown = KeybindLoader.RegisterKeybind(this, "DropDown", "X");
			On_Player.Spawn += OnPlayerSpawn;
			On_Collision.EmptyTile += OnEmptyTile;
			On_Lighting.GetColor_int_int += (orig, x, y) => EntityLight.Active ? EntityLight.Value : orig(x, y);
			On_Lighting.GetColor_int_int_Color += (orig, x, y, c) => EntityLight.Active ? c.MultiplyRGBA(EntityLight.Value) : orig(x, y, c);
			On_Lighting.GetColor_Point += (orig, p) => EntityLight.Active ? EntityLight.Value : orig(p);
			On_Lighting.GetColor_Point_Color += (orig, p, c) => EntityLight.Active ? c.MultiplyRGBA(EntityLight.Value) : orig(p, c);
			On_Player.HandleBeingInChestRange += DepthPlayer.OnHandleChestRange;
			On_NPC.CheckActive += Entities.DepthNPC.OnCheckActive;
			On_NPC.UpdateNPC += Entities.DepthNPC.OnUpdateNPC;
			On_Item.UpdateItem += Entities.DepthItem.OnUpdateItem;
			On_Projectile.Update += Entities.DepthProjectile.OnUpdate;
			On_NPC.AddIntoPlayersTownNPCSlots += Entities.DepthNPC.OnAddTownSlots;
			// In 3D the world is drawn by the 3D renderer: vanilla only draws the sky and surface backgrounds behind it.
			On_Main.DoDraw_WallsAndBlacks += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DoDraw_Tiles_NonSolid += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DoDraw_Tiles_Solid += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DoDraw_WallsTilesNPCs += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DoDraw_Waterfalls += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DoDraw_DrawNPCsOverTiles += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DoDraw_DrawNPCsBehindTiles += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DrawBackground += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DrawProjectiles += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DrawPlayers_BehindNPCs += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DrawPlayers_AfterProjectiles += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DrawItems += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DrawGore += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DrawGoreBehind += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DrawDust += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DrawRain += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DrawInfernoRings += (orig, self) => { if (!Skip2D) orig(self); };
			On_Main.DrawBG += (orig, self) => { if (!Skip2D) orig(self); }; // covered by the 3D frame
			if (!Main.dedServ)
				Main.OnPreDraw += Renderer3D.OnPreDraw;
		}

		public override void Unload()
		{
			On_Player.Spawn -= OnPlayerSpawn;
			On_Collision.EmptyTile -= OnEmptyTile;
			EntityLight.Active = false;
			if (!Main.dedServ) {
				Main.OnPreDraw -= Renderer3D.OnPreDraw;
				Main.QueueMainThreadAction(Renderer3D.Unload);
			}
			ToggleCamera = Toggle3D = FreeCursor = DropDown = null;
		}

		/// <summary>True while the 3D frame covers the screen: vanilla's 2D world and background drawing is skipped.</summary>
		private static bool Skip2D => DepthWorldSystem.Active && Renderer3D.FrameReady && !Main.mapFullscreen;

		private static bool OnEmptyTile(On_Collision.orig_EmptyTile orig, int i, int j, bool ignoreTiles)
		{
			if (!DepthWorldSystem.Active || !WorldGen.InWorld(i, j))
				return orig(i, j, ignoreTiles);
			int z = Voxel.OperatingZ >= 0 ? Voxel.OperatingZ : Voxel.WinZ;
			return DepthPlayer.EmptyTile3D(i, j, ignoreTiles, z + 0.5f);
		}

		/// <summary>Spawning (entering the world, respawn, recall) always happens on the base slice, where the spawn point is valid.</summary>
		private static void OnPlayerSpawn(On_Player.orig_Spawn orig, Player self, PlayerSpawnContext context)
		{
			if (Voxel.Ready && self.whoAmI == Main.myPlayer) {
				Voxel.CloseWindow();
				self.GetModPlayer<DepthPlayer>().ResetToCenterSlice();
			}
			orig(self, context);
		}
	}
}
