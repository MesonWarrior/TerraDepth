using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraDepth.Common
{
	/// <summary>Test helper: /endgame gives the player a set of endgame weapons, tools, armor and accessories.</summary>
	public class EndgameCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "endgame";
		public override string Usage => "/endgame";
		public override string Description => "TerraDepth test kit: endgame weapons, tools, armor and accessories";

		private static readonly (int type, int stack)[] Kit = {
			// melee
			(ItemID.Zenith, 1), (ItemID.Meowmere, 1), (ItemID.StarWrath, 1), (ItemID.Terrarian, 1),
			(ItemID.SolarEruption, 1), (ItemID.DayBreak, 1),
			// ranged
			(ItemID.SDMG, 1), (ItemID.Celeb2, 1), (ItemID.VortexBeater, 1), (ItemID.Phantasm, 1),
			(ItemID.EndlessMusketPouch, 1), (ItemID.EndlessQuiver, 1),
			// magic
			(ItemID.LastPrism, 1), (ItemID.LunarFlareBook, 1), (ItemID.NebulaBlaze, 1),
			// summon
			(ItemID.StardustDragonStaff, 1), (ItemID.MoonlordTurretStaff, 1), (ItemID.RainbowCrystalStaff, 1),
			// tools
			(ItemID.SolarFlarePickaxe, 1), (ItemID.SolarFlareDrill, 1), (ItemID.LunarHamaxeSolar, 1), (ItemID.RodofDiscord, 1),
			(ItemID.Shellphone, 1),
			// armor
			(ItemID.SolarFlareHelmet, 1), (ItemID.SolarFlareBreastplate, 1), (ItemID.SolarFlareLeggings, 1),
			// accessories
			(ItemID.WingsSolar, 1), (ItemID.EmpressFlightBooster, 1), (ItemID.TerrasparkBoots, 1),
			(ItemID.AnkhShield, 1), (ItemID.CelestialShell, 1),
			// hook and light pet
			(ItemID.LunarHook, 1), (ItemID.WispinaBottle, 1),
			// consumables
			(ItemID.SuperHealingPotion, 30), (ItemID.Torch, 999),
		};

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			Player p = caller.Player;
			var source = new EntitySource_DebugCommand("TerraDepth /endgame");
			foreach (var (type, stack) in Kit)
				p.QuickSpawnItem(source, type, stack);
			caller.Reply($"TerraDepth: gave {Kit.Length} endgame items.", Microsoft.Xna.Framework.Color.LightGreen);
		}
	}
}
