using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraDepth.World
{
	/// <summary>
	/// A detached copy of everything vanilla stores for one tile. Voxels outside of the live slice
	/// are kept in this form; it can be written into / read from a real <see cref="Tile"/>.
	/// </summary>
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	public struct VTile
	{
		public TileTypeData Type;
		public WallTypeData Wall;
		public LiquidData Liquid;
		public TileWallWireStateData State;
		public TileWallBrightnessInvisibilityData Brightness;

		public static readonly int Size = Unsafe.SizeOf<VTile>();

		public bool HasTile => State.HasTile;
		public ushort TileType => Type.Type;
		public ushort WallType => Wall.Type;
		public byte LiquidAmount => Liquid.Amount;
		public int LiquidType => Liquid.LiquidType;

		public static VTile From(Tile t)
		{
			VTile v;
			v.Type = t.Get<TileTypeData>();
			v.Wall = t.Get<WallTypeData>();
			v.Liquid = t.Get<LiquidData>();
			v.State = t.Get<TileWallWireStateData>();
			v.Brightness = t.Get<TileWallBrightnessInvisibilityData>();
			return v;
		}

		public readonly void WriteTo(Tile t)
		{
			t.Get<TileTypeData>() = Type;
			t.Get<WallTypeData>() = Wall;
			t.Get<LiquidData>() = Liquid;
			t.Get<TileWallWireStateData>() = State;
			t.Get<TileWallBrightnessInvisibilityData>() = Brightness;
		}

		public void ClearTile()
		{
			State.HasTile = false;
			State.IsActuated = false;
			State.HasActuator = false;
			State.Slope = SlopeType.Solid;
			State.IsHalfBlock = false;
			State.TileFrameX = 0;
			State.TileFrameY = 0;
			Type.Type = 0;
		}

		public void SetBlock(ushort type)
		{
			Type.Type = type;
			State.HasTile = true;
			State.IsActuated = false;
			State.Slope = SlopeType.Solid;
			State.IsHalfBlock = false;
			State.TileFrameX = 18;
			State.TileFrameY = 18;
		}

		/// <summary>Solid for collision (platforms excluded).</summary>
		public readonly bool IsSolid
		{
			get
			{
				if (!State.HasTile || State.IsActuated)
					return false;
				ushort t = Type.Type;
				return t < TileLoader.TileCount && Main.tileSolid[t] && !Main.tileSolidTop[t];
			}
		}

		/// <summary>Rendered as a full cube (or half-cube).</summary>
		public readonly bool IsCube
		{
			get
			{
				if (!State.HasTile || State.IsActuated)
					return false;
				ushort t = Type.Type;
				return t < TileLoader.TileCount && Main.tileSolid[t] && !Main.tileSolidTop[t] && !Main.tileFrameImportant[t];
			}
		}

		/// <summary>Cube that hides the faces of its neighbours.</summary>
		public readonly bool IsOpaqueCube => IsCube && !State.IsHalfBlock && Main.tileBlockLight[Type.Type];

		/// <summary>Bytewise equality, ignoring transient liquid-simulation flags.</summary>
		public static bool SameContent(in VTile a, in VTile b)
		{
			VTile x = a, y = b;
			x.Liquid.SkipLiquid = false; x.Liquid.CheckingLiquid = false;
			y.Liquid.SkipLiquid = false; y.Liquid.CheckingLiquid = false;
			if (x.Liquid.Amount == 0) x.Liquid = default;
			if (y.Liquid.Amount == 0) y.Liquid = default;
			ReadOnlySpan<byte> sa = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref x, 1));
			ReadOnlySpan<byte> sb = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref y, 1));
			return sa.SequenceEqual(sb);
		}

		public static void Write(System.IO.BinaryWriter w, in VTile v)
		{
			VTile c = v;
			w.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref c, 1)));
		}

		public static VTile Read(System.IO.BinaryReader r)
		{
			VTile v = default;
			Span<byte> s = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref v, 1));
			r.Read(s);
			return v;
		}
	}
}
