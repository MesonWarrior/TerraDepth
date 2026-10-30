using System;
using Microsoft.Xna.Framework;
using TerraDepth.Common;
using TerraDepth.Players;
using TerraDepth.World;
using Terraria;
using Terraria.ModLoader;

namespace TerraDepth.Rendering
{
	/// <summary>
	/// Two coordinate spaces are used:
	///  tile space   - x right, y down (Terraria rows), z depth; 1 unit = 1 tile.
	///  render space - x right, y up, z depth. render = (x, -y, z).
	/// Yaw 0 looks toward -Z, which matches the classic 2D view (x to the right).
	/// </summary>
	public static class Camera3D
	{
		public static float Yaw;
		public static float Pitch;
		public static bool ThirdPerson;

		public static Vector3 EyeTile;
		public static Vector3 HeadTile;
		public static Matrix View, Projection;
		public static BoundingFrustum Frustum = new(Matrix.Identity);
		public static float FarPlane = 120f;

		public static Vector3 ToRender(Vector3 tile) => new(tile.X, -tile.Y, tile.Z);

		public static Vector3 ForwardTile
		{
			get
			{
				float cp = (float)Math.Cos(Pitch);
				return new Vector3((float)Math.Sin(Yaw) * cp, -(float)Math.Sin(Pitch), -(float)Math.Cos(Yaw) * cp);
			}
		}

		/// <summary>Horizontal forward/right in tile space (y = 0).</summary>
		public static Vector3 FlatForward => new((float)Math.Sin(Yaw), 0f, -(float)Math.Cos(Yaw));
		public static Vector3 FlatRight => new((float)Math.Cos(Yaw), 0f, (float)Math.Sin(Yaw));

		public static Vector3 PlayerHead(Player p)
		{
			var dp = p.GetModPlayer<DepthPlayer>();
			return new Vector3(p.Center.X / 16f, (p.position.Y + 8f + p.gfxOffY) / 16f, dp.Z);
		}

		public static void Update(Player p, float aspect)
		{
			var cfg = ModContent.GetInstance<DepthClientConfig>();
			HeadTile = PlayerHead(p);
			Vector3 fwd = ForwardTile;
			EyeTile = HeadTile;
			if (ThirdPerson) {
				// over-the-shoulder: pivot to the right of and a bit above the head, camera behind the pivot,
				// so the crosshair looks past the character instead of at its back
				Vector3 shoulder = FlatRight * 0.95f + new Vector3(0f, -0.3f, 0f);
				float sLen = shoulder.Length();
				if (Raycast.Cast(HeadTile, shoulder, sLen + 0.3f, v => v.IsOpaqueCube, out var sHit))
					shoulder *= Math.Max(0f, sHit.Distance - 0.3f) / sLen;
				Vector3 pivot = HeadTile + shoulder;
				float dist = cfg.ThirdPersonDistance;
				Vector3 back = -fwd;
				if (Raycast.Cast(pivot, back, dist + 0.5f, v => v.IsOpaqueCube, out var hit))
					dist = Math.Max(0.4f, hit.Distance - 0.3f);
				EyeTile = pivot + back * dist;
			}
			FarPlane = cfg.RenderDistance + 16f;
			Vector3 eyeR = ToRender(EyeTile);
			Vector3 fwdR = ToRender(fwd);
			View = Matrix.CreateLookAt(eyeR, eyeR + fwdR, Vector3.Up);
			Projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(cfg.FieldOfView), Math.Max(0.1f, aspect), 0.05f, FarPlane);
			Frustum.Matrix = View * Projection;
		}

		/// <summary>Projects a tile-space point to screen pixels. Returns false when behind the camera.</summary>
		public static bool Project(Vector3 tile, int screenW, int screenH, out Vector2 screen, out float depth)
		{
			Vector4 clip = Vector4.Transform(new Vector4(ToRender(tile), 1f), View * Projection);
			screen = default;
			depth = clip.W;
			if (clip.W <= 0.05f)
				return false;
			float nx = clip.X / clip.W, ny = clip.Y / clip.W;
			screen = new Vector2((nx * 0.5f + 0.5f) * screenW, (1f - (ny * 0.5f + 0.5f)) * screenH);
			return true;
		}

		/// <summary>Tile-space ray through a screen pixel.</summary>
		public static Vector3 ScreenRay(Vector2 screen, int screenW, int screenH)
		{
			float nx = screen.X / screenW * 2f - 1f;
			float ny = 1f - screen.Y / screenH * 2f;
			Matrix inv = Matrix.Invert(View * Projection);
			Vector4 a = Vector4.Transform(new Vector4(nx, ny, 0f, 1f), inv);
			Vector4 b = Vector4.Transform(new Vector4(nx, ny, 1f, 1f), inv);
			Vector3 pa = new Vector3(a.X, a.Y, a.Z) / a.W;
			Vector3 pb = new Vector3(b.X, b.Y, b.Z) / b.W;
			Vector3 d = Vector3.Normalize(pb - pa);
			return new Vector3(d.X, -d.Y, d.Z);
		}

		public static void Clamp()
		{
			Pitch = MathHelper.Clamp(Pitch, -1.55f, 1.55f);
			Yaw = MathHelper.WrapAngle(Yaw);
		}

		public static int ClampZ(float z) => Math.Clamp((int)Math.Floor(z), 0, Math.Max(0, Voxel.D - 1));
	}
}
