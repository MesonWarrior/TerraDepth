using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SDL2;
using TerraDepth.Rendering;
using TerraDepth.World;
using Terraria;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraDepth.Common
{
	/// <summary>
	/// Mouse look, layout-independent WASD (SDL scancodes, so it also works with a Cyrillic layout),
	/// 3D target selection and translating the 3D target into the 2D mouse position vanilla expects.
	/// </summary>
	public class Input3D : ModSystem
	{
		public static bool LookActive { get; private set; }
		private static bool wasLook;

		public static bool HasTarget;
		public static Raycast.Hit Target;
		/// <summary>NPC under the crosshair (closer than any block), or -1.</summary>
		public static int NpcTarget = -1;
		/// <summary>Cell vanilla should act on (hit cell or the empty cell in front of it).</summary>
		public static int ActX, ActY, ActZ;
		public static bool ToolMode;
		/// <summary>Normalized tile-space aim direction.</summary>
		public static Vector3 AimDir = new(0, 0, -1);
		/// <summary>
		/// Point the crosshair is on (block, enemy, or a far point in the air), in tile space. Everything vanilla aims at the
		/// mouse (shots, hooks, held weapons) aims from the player at this point, like at the cursor in 2D.
		/// </summary>
		public static Vector3 AimPoint;
		private const float AimRange = 100f;

		/// <summary>Normalized direction from a tile-space point to <see cref="AimPoint"/>.</summary>
		public static Vector3 ShotDirFrom(Vector3 from)
		{
			Vector3 d = AimPoint - from;
			return d.LengthSquared() < 0.25f ? AimDir : Vector3.Normalize(d);
		}
		public static float Reach;

		public static bool KeyForward, KeyBack, KeyLeft, KeyRight, KeyDown;

		private const int SC_W = 26, SC_A = 4, SC_S = 22, SC_D = 7, SC_X = 27;
		private const int SC_F5 = 62, SC_F6 = 63, SC_LALT = 226;

		// default keys read straight from SDL, used for mod actions that have no key in the player's controls profile
		private static bool f5Down, f6Down, lAltDown, f5Edge, f6Edge;

		/// <summary>
		/// tModLoader only puts a mod's default keys into its preset control profiles; in a "Custom" profile mod actions start
		/// without any key. Then the default key works anyway, until the player assigns one in Settings / Controls.
		/// </summary>
		private static bool Unbound(ModKeybind kb) => kb != null && kb.GetAssignedKeys().Count == 0;

		public static bool ToggleCameraPressed => (TerraDepthMod.ToggleCamera?.JustPressed ?? false) || Unbound(TerraDepthMod.ToggleCamera) && f5Edge;
		public static bool Toggle3DPressed => (TerraDepthMod.Toggle3D?.JustPressed ?? false) || Unbound(TerraDepthMod.Toggle3D) && f6Edge;
		public static bool FreeCursorHeld => (TerraDepthMod.FreeCursor?.Current ?? false) || Unbound(TerraDepthMod.FreeCursor) && lAltDown;

		private static unsafe void ReadDefaultKeys()
		{
			bool f5 = false, f6 = false, alt = false;
			if (Main.hasFocus && !Main.drawingPlayerChat && !Main.editSign && !Main.editChest && !PlayerInput.WritingText) {
				byte* state = (byte*)SDL.SDL_GetKeyboardState(out int n);
				if (state != null) {
					f5 = Scancode(state, n, SC_F5);
					f6 = Scancode(state, n, SC_F6);
					alt = Scancode(state, n, SC_LALT);
				}
			}
			f5Edge = f5 && !f5Down;
			f6Edge = f6 && !f6Down;
			f5Down = f5;
			f6Down = f6;
			lAltDown = alt;
		}

		private static unsafe bool Scancode(byte* state, int n, int code) => code < n && state[code] != 0;

		/// <summary>
		/// The "drop down" action (vanilla's Down, which S no longer is in 3D): its assigned keys are read by their physical
		/// position like WASD, so a letter key works in any keyboard layout; unassigned it falls back to X.
		/// </summary>
		private static unsafe bool DropDownHeld(byte* state, int n)
		{
			ModKeybind kb = TerraDepthMod.DropDown;
			if (kb == null)
				return false;
			if (kb.Current)
				return true;
			var keys = kb.GetAssignedKeys();
			if (keys.Count == 0)
				return Scancode(state, n, SC_X);
			foreach (string k in keys) {
				int sc = ScancodeOf(k);
				if (sc >= 0 && Scancode(state, n, sc))
					return true;
			}
			return false;
		}

		/// <summary>SDL scancode of an XNA key name, for letters, digits and modifiers; -1 for others.</summary>
		private static int ScancodeOf(string key)
		{
			if (key.Length == 1 && key[0] >= 'A' && key[0] <= 'Z')
				return 4 + key[0] - 'A';
			if (key.Length == 2 && key[0] == 'D' && key[1] >= '0' && key[1] <= '9')
				return key[1] == '0' ? 39 : 30 + key[1] - '1';
			return key switch {
				"LeftControl" => 224, "LeftShift" => 225, "LeftAlt" => 226,
				"RightControl" => 228, "RightShift" => 229, "RightAlt" => 230,
				"Space" => 44, "Tab" => 43, "CapsLock" => 57,
				_ => -1
			};
		}

		private static unsafe void ReadKeys()
		{
			KeyForward = KeyBack = KeyLeft = KeyRight = KeyDown = false;
			if (!Main.hasFocus || Main.drawingPlayerChat || Main.editSign || Main.editChest || Main.blockInput || PlayerInput.WritingText)
				return;
			byte* state = (byte*)SDL.SDL_GetKeyboardState(out int n);
			if (state == null)
				return;
			KeyForward = Scancode(state, n, SC_W);
			KeyBack = Scancode(state, n, SC_S);
			KeyLeft = Scancode(state, n, SC_A);
			KeyRight = Scancode(state, n, SC_D);
			KeyDown = DropDownHeld(state, n);
		}

		private static bool UiOpen(Player p)
		{
			return Main.playerInventory || Main.ingameOptionsWindow || Main.inFancyUI || Main.drawingPlayerChat || Main.editSign
				|| Main.editChest || Main.mapFullscreen || p.talkNPC >= 0 || p.chest != -1 || p.sign >= 0 || !Main.hasFocus
				|| Main.blockInput || FreeCursorHeld || Main.gameMenu;
		}

		public override void PostUpdateInput()
		{
			ReadDefaultKeys();
			if (Main.gameMenu || !DepthWorldSystem.Active) {
				LookActive = wasLook = false;
				HasTarget = false;
				return;
			}
			Player p = Main.LocalPlayer;
			ReadKeys();
			var cfg = ModContent.GetInstance<DepthClientConfig>();
			Main.SmartCursorWanted_Mouse = false;
			Main.SmartCursorWanted_GamePad = false;
			Main.GameZoomTarget = 1f;

			LookActive = !UiOpen(p) && !p.dead;
			if (LookActive) {
				var bounds = Main.instance.Window.ClientBounds;
				int cx = bounds.Width / 2, cy = bounds.Height / 2;
				MouseState ms = Mouse.GetState();
				if (wasLook) {
					float dx = ms.X - cx, dy = ms.Y - cy;
					float sens = cfg.MouseSensitivity * 0.01f;
					Camera3D.Yaw += dx * sens;
					Camera3D.Pitch += (cfg.InvertMouseY ? dy : -dy) * sens;
					Camera3D.Clamp();
				}
				Mouse.SetPosition(cx, cy);
			}
			wasLook = LookActive;

			long t0 = Perf.Now;
			Camera3D.Update(p, Main.screenWidth / (float)Math.Max(1, Main.screenHeight));
			SelectTarget(p);
			if (LookActive)
				ApplyFakeMouse(p);
			Perf.Lap("aim and targets", t0);
		}

		private static bool Targetable(VTile v, Item held)
		{
			if (v.HasTile)
				return true;
			if (held.hammer > 0 && v.WallType > 0 && v.WallType < WallLoader.WallCount && Main.wallHouse[v.WallType])
				return true;
			return false;
		}

		private static void SelectTarget(Player p)
		{
			Item held = p.HeldItem;
			ToolMode = held.pick > 0 || held.axe > 0 || held.hammer > 0 || held.createTile >= 0 || held.createWall >= 0
				|| held.tileWand >= 0 || held.mech || held.type == ItemID.None;
			Reach = Player.tileRangeX + held.tileBoost + p.blockRange + 1.5f;

			Vector3 dir = Camera3D.ForwardTile;
			Vector3 origin = Camera3D.EyeTile;
			if (!LookActive) {
				// free cursor: pick through the mouse pointer
				dir = Camera3D.ScreenRay(new Vector2(Main.mouseX, Main.mouseY), Main.screenWidth, Main.screenHeight);
			}
			AimDir = Vector3.Normalize(dir);
			float extra = Camera3D.ThirdPerson ? Vector3.Distance(Camera3D.EyeTile, Camera3D.HeadTile) : 0f;
			HasTarget = Raycast.Cast(origin, dir, Reach + extra + 1f, v => Targetable(v, held), out Target);
			if (HasTarget) {
				Vector3 cell = new(Target.X + 0.5f, Target.Y + 0.5f, Target.Z + 0.5f);
				if (Vector3.Distance(cell, Camera3D.HeadTile) > Reach)
					HasTarget = false;
			}
			PickNpc(origin, AimDir, Reach + extra + 3f);
			SelectAimPoint(p, origin);
			if (HasTarget) {
				bool placing = (held.createTile >= 0 || held.createWall >= 0 || held.tileWand >= 0) && !Main.mouseRight;
				ActX = Target.X; ActY = Target.Y; ActZ = Target.Z;
				if (placing) {
					ActX += Target.NX; ActY += Target.NY; ActZ += Target.NZ;
				}
				ActZ = Math.Clamp(ActZ, 0, Voxel.D - 1);
			}
		}

		private static void SelectAimPoint(Player p, Vector3 eye)
		{
			// in third person start at the player, so blocks between the camera and the player are not aimed at
			Vector3 head = Camera3D.HeadTile;
			float t0 = Math.Max(0f, Vector3.Dot(head - eye, AimDir));
			Vector3 origin = eye + AimDir * t0;
			float dist = AimRange;
			if (Raycast.Cast(origin, AimDir, AimRange, v => v.HasTile && v.TileType < TileLoader.TileCount
					&& (Main.tileSolid[v.TileType] || Main.tileSolidTop[v.TileType]), out var hit))
				dist = hit.Distance;
			foreach (NPC npc in Main.ActiveNPCs) {
				if (npc.friendly || npc.dontTakeDamage)
					continue;
				float z = npc.GetGlobalNPC<Entities.DepthNPC>().Z;
				if (z < 0f)
					continue;
				Vector3 min = new(npc.position.X / 16f, npc.position.Y / 16f, z - 0.5f);
				Vector3 max = new((npc.position.X + npc.width) / 16f, (npc.position.Y + npc.height) / 16f, z + 0.5f);
				if (RayBox(origin, AimDir, min, max, out float t) && t < dist)
					dist = t;
			}
			AimPoint = origin + AimDir * Math.Max(dist, 0.5f);
		}

		/// <summary>Ray against NPC hitboxes in 3D (their 2D box, half a tile deep around their depth).</summary>
		private static void PickNpc(Vector3 origin, Vector3 dir, float maxDist)
		{
			NpcTarget = -1;
			float best = HasTarget ? Target.Distance : maxDist;
			foreach (NPC npc in Main.ActiveNPCs) {
				float z = npc.GetGlobalNPC<Entities.DepthNPC>().Z;
				if (z < 0f)
					continue;
				Vector3 min = new(npc.position.X / 16f, npc.position.Y / 16f, z - 0.5f);
				Vector3 max = new((npc.position.X + npc.width) / 16f, (npc.position.Y + npc.height) / 16f, z + 0.5f);
				if (RayBox(origin, dir, min, max, out float t) && t < best) {
					best = t;
					NpcTarget = npc.whoAmI;
				}
			}
		}

		private static bool RayBox(Vector3 o, Vector3 d, Vector3 min, Vector3 max, out float t)
		{
			float t0 = 0f, t1 = float.MaxValue;
			for (int i = 0; i < 3; i++) {
				float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z;
				float di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
				float lo = i == 0 ? min.X : i == 1 ? min.Y : min.Z;
				float hi = i == 0 ? max.X : i == 1 ? max.Y : max.Z;
				if (Math.Abs(di) < 1e-6f) {
					if (oi < lo || oi > hi) {
						t = 0f;
						return false;
					}
					continue;
				}
				float a = (lo - oi) / di, b = (hi - oi) / di;
				if (a > b)
					(a, b) = (b, a);
				t0 = Math.Max(t0, a);
				t1 = Math.Min(t1, b);
				if (t0 > t1) {
					t = 0f;
					return false;
				}
			}
			t = t0;
			return true;
		}

		private static void ApplyFakeMouse(Player p)
		{
			Vector2 world;
			Vector2 center2D = p.Center;
			if (NpcTarget >= 0 && (Main.mouseRight || !ToolMode)) {
				// talking, shopping or hitting: vanilla finds the NPC under the mouse
				world = Main.npc[NpcTarget].Center;
			}
			else if (Main.mouseRight && HasTarget) {
				// interacting (chests, doors, levers): point at the block under the crosshair whatever is held
				world = new Vector2(Target.X * 16 + 8, Target.Y * 16 + 8);
			}
			else if (ToolMode) {
				if (HasTarget)
					world = new Vector2(ActX * 16 + 8, ActY * 16 + 8);
				else {
					// No valid 3D target: point far away so vanilla doesn't act on a random tile of the slice.
					Vector2 d = new(AimDir.X, AimDir.Y);
					if (d.LengthSquared() < 0.01f)
						d = new Vector2(0, -1);
					d.Normalize();
					world = center2D + d * 16f * 40f;
				}
			}
			else {
				Vector2 d = new Vector2(AimPoint.X * 16f, AimPoint.Y * 16f) - center2D;
				if (d.LengthSquared() < 4f) {
					// looking straight along Z: keep facing direction
					d = new Vector2(Camera3D.FlatForward.X >= 0 ? 1 : -1, -0.05f) * 16f;
				}
				world = center2D + d;
			}

			Vector2 screen = world - Main.screenPosition;
			// keep the fake pointer inside the play area so it never hovers UI elements
			Vector2 playerScreen = center2D - Main.screenPosition;
			Vector2 rel = screen - playerScreen;
			float maxR = Math.Min(Main.screenWidth, Main.screenHeight) * 0.3f;
			if (!ToolMode && rel.Length() > maxR)
				rel = Vector2.Normalize(rel) * maxR;
			screen = playerScreen + rel;

			Vector2 sc = new(Main.screenWidth / 2f, Main.screenHeight / 2f);
			float zoom = Main.GameViewMatrix.Zoom.X;
			screen = sc + (screen - sc) * zoom;
			int mx = (int)screen.X, my = (int)screen.Y;
			Main.mouseX = Main.lastMouseX = PlayerInput.MouseX = mx;
			Main.mouseY = Main.lastMouseY = PlayerInput.MouseY = my;
			PlayerInput.CacheZoomableValues();
		}
	}
}
