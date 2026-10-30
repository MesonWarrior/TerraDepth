using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TerraDepth.Rendering
{
	/// <summary>One sprite vanilla drew, in 2D world pixels.</summary>
	public struct CapturedSprite
	{
		public Texture2D Tex;
		public Rectangle Src;
		public Vector2 Center;
		public Vector2 Size;
		public float Rotation;
		public bool FlipH, FlipV;
		public Color Color;
	}

	/// <summary>
	/// Runs vanilla drawing code and records the sprites it draws instead of drawing them, so they can be placed in 3D.
	/// A SpriteBatch begun with a sorting mode keeps every sprite's parameters until End; they are read from there and
	/// the batch is emptied before End, so nothing reaches the screen.
	/// </summary>
	public static class SpriteCapture
	{
		private const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;
		private static readonly FieldInfo numSprites = typeof(SpriteBatch).GetField("numSprites", Priv);
		private static readonly FieldInfo spriteInfos = typeof(SpriteBatch).GetField("spriteInfos", Priv);
		private static readonly FieldInfo textureInfo = typeof(SpriteBatch).GetField("textureInfo", Priv);
		private static readonly FieldInfo sortMode = typeof(SpriteBatch).GetField("sortMode", Priv);
		private static readonly Type infoType = typeof(SpriteBatch).GetNestedType("SpriteInfo", BindingFlags.NonPublic);
		private static readonly FieldInfo sx = Field("sourceX"), sy = Field("sourceY"), sw = Field("sourceW"), sh = Field("sourceH"),
			dx = Field("destinationX"), dy = Field("destinationY"), dw = Field("destinationW"), dh = Field("destinationH"),
			color = Field("color"), ox = Field("originX"), oy = Field("originY"), sin = Field("rotationSin"), cos = Field("rotationCos"),
			effects = Field("effects");

		private static FieldInfo Field(string name) => infoType?.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

		/// <summary>Same layout as SpriteBatch.SpriteInfo, to read the sprites without reflection per field.</summary>
		[StructLayout(LayoutKind.Sequential)]
		private struct Info
		{
			public int textureHash;
			public float sourceX, sourceY, sourceW, sourceH;
			public float destinationX, destinationY, destinationW, destinationH;
			public Color color;
			public float originX, originY, rotationSin, rotationCos, depth;
			public byte effects;
		}

		private static readonly bool sameLayout = CheckLayout();

		private static bool CheckLayout()
		{
			try {
				if (infoType == null || Marshal.SizeOf(infoType) != Marshal.SizeOf<Info>())
					return false;
				foreach (var f in typeof(Info).GetFields())
					if (Marshal.OffsetOf(infoType, f.Name) != Marshal.OffsetOf<Info>(f.Name))
						return false;
				return true;
			}
			catch {
				return false;
			}
		}

		public static bool Available => numSprites != null && spriteInfos != null && textureInfo != null && sortMode != null && effects != null;

		/// <summary>Records what <paramref name="draw"/> draws with <paramref name="sb"/> (which must not be begun) into <paramref name="into"/>.</summary>
		public static void Run(SpriteBatch sb, Action draw, List<CapturedSprite> into)
		{
			sb.Begin(SpriteSortMode.Texture, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone, null, Matrix.Identity);
			try {
				draw();
			}
			finally {
				// vanilla code that restarted the batch itself leaves it in another mode: those sprites are lost
				if ((SpriteSortMode)sortMode.GetValue(sb) == SpriteSortMode.Texture)
					Read(sb, into);
				sb.End();
			}
		}

		private static void Read(SpriteBatch sb, List<CapturedSprite> into)
		{
			int n = (int)numSprites.GetValue(sb);
			var infos = (Array)spriteInfos.GetValue(sb);
			var textures = (Texture2D[])textureInfo.GetValue(sb);
			if (sameLayout) {
				var h = GCHandle.Alloc(infos, GCHandleType.Pinned);
				try {
					unsafe {
						var ptr = (Info*)h.AddrOfPinnedObject();
						for (int i = 0; i < n; i++) {
							Info o = ptr[i];
							Add(into, textures[i], o.sourceX, o.sourceY, o.sourceW, o.sourceH, o.destinationX, o.destinationY, o.destinationW, o.destinationH,
								o.color, o.originX, o.originY, o.rotationSin, o.rotationCos, o.effects);
						}
					}
				}
				finally {
					h.Free();
				}
				numSprites.SetValue(sb, 0);
				return;
			}
			for (int i = 0; i < n; i++) {
				object o = infos.GetValue(i);
				Add(into, textures[i], (float)sx.GetValue(o), (float)sy.GetValue(o), (float)sw.GetValue(o), (float)sh.GetValue(o),
					(float)dx.GetValue(o), (float)dy.GetValue(o), (float)dw.GetValue(o), (float)dh.GetValue(o), (Color)color.GetValue(o),
					(float)ox.GetValue(o), (float)oy.GetValue(o), (float)sin.GetValue(o), (float)cos.GetValue(o), (byte)effects.GetValue(o));
			}
			numSprites.SetValue(sb, 0);
		}

		private static void Add(List<CapturedSprite> into, Texture2D tex, float srcX, float srcY, float srcW, float srcH, float dstX, float dstY,
			float w, float h, Color col, float originX, float originY, float s, float c, byte fx2)
		{
			if (tex == null || tex.IsDisposed)
				return;
			if (w <= 0f || h <= 0f)
				return;
			float fx = (0.5f - originX) * w, fy = (0.5f - originY) * h;
			// FNA rotates the quad around the origin: its centre is the origin's offset to the middle, rotated
			var center = new Vector2(dstX + c * fx - s * fy, dstY + s * fx + c * fy);
			into.Add(new CapturedSprite {
				Tex = tex,
				Src = new Rectangle((int)Math.Round(srcX * tex.Width), (int)Math.Round(srcY * tex.Height),
					Math.Max(1, (int)Math.Round(srcW * tex.Width)), Math.Max(1, (int)Math.Round(srcH * tex.Height))),
				Center = center,
				Size = new Vector2(w, h),
				Rotation = (float)Math.Atan2(s, c),
				FlipH = (fx2 & 1) != 0,
				FlipV = (fx2 & 2) != 0,
				Color = col
			});
		}
	}
}
