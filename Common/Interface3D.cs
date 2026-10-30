using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TerraDepth.Entities;
using TerraDepth.Players;
using TerraDepth.Rendering;
using TerraDepth.World;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using Terraria.UI;

namespace TerraDepth.Common
{
	/// <summary>Puts the 3D frame under the vanilla UI and draws the 3D HUD (crosshair, health bars, damage numbers).</summary>
	public class Interface3D : ModSystem
	{
		private static readonly string[] HiddenAlways = {
			"Vanilla: Entity Health Bars", "Vanilla: Emote Bubbles", "Vanilla: Smart Cursor Targets", "Vanilla: Tile Grid",
			"Vanilla: Ruler", "Vanilla: Wire Selection", "Vanilla: Laser Ruler"
		};
		// "Vanilla: Mouse Over" must keep running: vanilla handles talking to NPCs (right click) inside it. Only its text,
		// which would appear at the 2D mouse position, is hidden.
		private static readonly string[] HiddenWhileLooking = { "Vanilla: Cursor", "Vanilla: Interact Item Icon", "Vanilla: Mouse Text" };

		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			if (!DepthWorldSystem.Active)
				return;
			foreach (var l in layers) {
				if (Array.IndexOf(HiddenAlways, l.Name) >= 0)
					l.Active = false;
				if (Input3D.LookActive && Array.IndexOf(HiddenWhileLooking, l.Name) >= 0)
					l.Active = false;
			}
			layers.Insert(0, new LegacyGameInterfaceLayer("TerraDepth: 3D World", DrawWorld, InterfaceScaleType.None));
			layers.Insert(1, new LegacyGameInterfaceLayer("TerraDepth: 3D HUD", DrawHud, InterfaceScaleType.None));
		}

		private static bool DrawWorld()
		{
			if (!Renderer3D.FrameReady || Renderer3D.Target == null)
				return true;
			SpriteBatch sb = Main.spriteBatch;
			sb.End();
			sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
			sb.Draw(Renderer3D.Target, new Rectangle(0, 0, Main.screenWidth, Main.screenHeight), Color.White);
			sb.End();
			sb.Begin(SpriteSortMode.Deferred, null, null, null, null, null, Matrix.Identity);
			return true;
		}

		private static bool DrawHud()
		{
			if (!Renderer3D.FrameReady)
				return true;
			SpriteBatch sb = Main.spriteBatch;
			int sw = Main.screenWidth, sh = Main.screenHeight;
			Player me = Main.LocalPlayer;
			float myZ = me.GetModPlayer<DepthPlayer>().Z;

			// enemy health bars
			foreach (NPC npc in Main.ActiveNPCs) {
				if (npc.life >= npc.lifeMax || npc.lifeMax <= 1 || npc.dontTakeDamage || npc.townNPC && npc.life >= npc.lifeMax)
					continue;
				var pos = new Vector3(npc.Center.X / 16f, (npc.position.Y + npc.height) / 16f + 0.6f, npc.GetGlobalNPC<DepthNPC>().Z);
				if (Vector3.Distance(pos, Camera3D.EyeTile) > 50f)
					continue;
				if (Camera3D.Project(pos, sw, sh, out Vector2 s, out float depth)) {
					float scale = MathHelper.Clamp(14f / depth, 0.45f, 1.3f);
					Main.instance.DrawHealthBar(s.X + Main.screenPosition.X, s.Y + Main.screenPosition.Y, npc.life, npc.lifeMax, 1f, scale, true);
				}
			}

			// damage numbers
			for (int i = 0; i < Main.maxCombatText; i++) {
				CombatText ct = Main.combatText[i];
				if (!ct.active)
					continue;
				Vector2 c2 = ct.position + new Vector2(16f, 10f);
				float z = myZ, best = 4f * 16f;
				foreach (NPC npc in Main.ActiveNPCs) {
					float d = Vector2.Distance(npc.Center, c2);
					if (d < best) {
						best = d;
						z = npc.GetGlobalNPC<DepthNPC>().Z;
					}
				}
				if (!Camera3D.Project(new Vector3(c2.X / 16f, c2.Y / 16f, z), sw, sh, out Vector2 s, out float depth))
					continue;
				var font = FontAssets.CombatText[ct.crit ? 1 : 0].Value;
				float scale = ct.scale * MathHelper.Clamp(14f / depth, 0.5f, 1.4f);
				Vector2 size = font.MeasureString(ct.text);
				Color col = ct.color * ((255 - ct.alpha) / 255f);
				Utils.DrawBorderStringFourWay(sb, font, ct.text, s.X, s.Y, col, Color.Black * ((255 - ct.alpha) / 255f), size / 2f, scale);
			}

			// crosshair
			if (Input3D.LookActive) {
				Texture2D px = TextureAssets.MagicPixel.Value;
				int cx = sw / 2, cy = sh / 2;
				Color shadow = Color.Black * 0.6f;
				sb.Draw(px, new Rectangle(cx - 9, cy - 2, 18, 4), shadow);
				sb.Draw(px, new Rectangle(cx - 2, cy - 9, 4, 18), shadow);
				sb.Draw(px, new Rectangle(cx - 8, cy - 1, 16, 2), Color.White);
				sb.Draw(px, new Rectangle(cx - 1, cy - 8, 2, 16), Color.White);
			}

			// name of the NPC under the crosshair
			if (Input3D.LookActive && Input3D.NpcTarget >= 0) {
				NPC t = Main.npc[Input3D.NpcTarget];
				if (t.active) {
					string name = t.GivenOrTypeName;
					if (!t.townNPC && t.lifeMax > 1 && !t.dontTakeDamage)
						name += $": {t.life}/{t.lifeMax}";
					var mf = FontAssets.MouseText.Value;
					Vector2 sz = mf.MeasureString(name);
					Utils.DrawBorderStringFourWay(sb, mf, name, sw / 2f - sz.X / 2f, sh / 2f + 18f, Color.White, Color.Black, Vector2.Zero, 1f);
				}
			}

			// coordinates
			string info = $"X {me.Center.X / 16f:0}  Y {me.Center.Y / 16f:0}  Z {myZ:0.0} / {Voxel.D}   [F5] camera  [F6] 2D/3D  [Esc] cursor";
			var f = FontAssets.MouseText.Value;
			Vector2 isz = f.MeasureString(info) * 0.8f;
			Utils.DrawBorderStringFourWay(sb, f, info, sw / 2f - isz.X / 2f, sh - 28f, Color.White * 0.85f, Color.Black, Vector2.Zero, 0.8f);
			return true;
		}
	}
}
