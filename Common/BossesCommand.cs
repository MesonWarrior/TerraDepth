using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraDepth.Common
{
	/// <summary>Test helper: /bosses gives summon items for every boss and event that has one.</summary>
	public class BossesCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "bosses";
		public override string Usage => "/bosses";
		public override string Description => "TerraDepth test kit: boss and event summon items";

		private static readonly (int type, int stack)[] Kit = {
			// pre-hardmode bosses
			(ItemID.SlimeCrown, 3), (ItemID.SuspiciousLookingEye, 3), (ItemID.WormFood, 3), (ItemID.BloodySpine, 3),
			(ItemID.Abeemination, 3), (ItemID.DeerThing, 3), (ItemID.ClothierVoodooDoll, 1), (ItemID.GuideVoodooDoll, 3),
			// hardmode bosses
			(ItemID.QueenSlimeCrystal, 3), (ItemID.MechanicalWorm, 3), (ItemID.MechanicalEye, 3), (ItemID.MechanicalSkull, 3),
			(ItemID.MechdusaSummon, 3), (ItemID.LihzahrdPowerCell, 3), (ItemID.TruffleWorm, 3), (ItemID.EmpressButterfly, 3),
			(ItemID.CelestialSigil, 3),
			// events
			(ItemID.GoblinBattleStandard, 2), (ItemID.SnowGlobe, 2), (ItemID.PirateMap, 2), (ItemID.BloodMoonStarter, 2),
			(ItemID.SolarTablet, 2), (ItemID.PumpkinMoonMedallion, 2), (ItemID.NaughtyPresent, 2),
			(ItemID.DD2ElderCrystalStand, 1), (ItemID.DD2ElderCrystal, 2),
		};

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			Player p = caller.Player;
			var source = new EntitySource_DebugCommand("TerraDepth /bosses");
			foreach (var (type, stack) in Kit)
				p.QuickSpawnItem(source, type, stack);
			caller.Reply($"TerraDepth: gave {Kit.Length} boss and event summons.", Microsoft.Xna.Framework.Color.LightGreen);
		}
	}
}
