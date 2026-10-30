using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace TerraDepth.Common
{
	public class DepthClientConfig : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ClientSide;

		[DefaultValue(true)]
		public bool Enabled3D;

		[Range(50, 110)]
		[DefaultValue(80)]
		[Slider]
		public int FieldOfView;

		[Range(32, 192)]
		[Increment(16)]
		[DefaultValue(96)]
		[Slider]
		public int RenderDistance;

		[Range(0.02f, 1f)]
		[DefaultValue(0.2f)]
		[Increment(0.02f)]
		public float MouseSensitivity;

		[DefaultValue(false)]
		public bool InvertMouseY;

		[DefaultValue(false)]
		public bool StartInThirdPerson;

		[Range(2f, 16f)]
		[DefaultValue(6f)]
		public float ThirdPersonDistance;

		[Range(2, 24)]
		[DefaultValue(6)]
		[Slider]
		public int ChunkBuildBudgetMs;
	}

	public class DepthServerConfig : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ServerSide;

		/// <summary>Depth of the world (Z axis) as percent of its length. Applied when a world gets 3D data for the first time.</summary>
		[Range(5, 25)]
		[DefaultValue(10)]
		[Slider]
		public int DepthPercent;
	}
}
