# TerraDepth 3D

A tModLoader (Steam) mod that makes Terraria three-dimensional.

## World
- Axes: X is the Terraria world length, Y is the height, Z is the depth, 10% of the length (a small world is 4200 × 1200 × 420). The percentage can be changed in the mod settings (5–25%) and applies the first time a world is entered with the mod.
- All terrain is built by a 3D generator (`World/Gen3D.cs`):
  - the surface is a 3D density field: a height map from 2D noise over X and Z plus 3D noise near the surface (cliffs, overhangs, arches, grassy ledges), mountains in the snow, dunes in the desert, chasms in the corruption;
  - caves are 3D tunnels and caverns, underground lakes of water and lava, 3D ore veins and gems;
  - biomes are regions on the X–Z plane: their order along X is the vanilla one, but their borders bend strongly along the depth; underground biomes are volumes;
  - trees, grass, floating islands, the underworld with its lava sea.
- The first time a world is entered with the mod, the vanilla 2D world is replaced by the central slice of the 3D generator. Only the structures and items that cannot be generated are taken from vanilla (`WorldPlan.Mask`); the spawn point and town NPCs move to the new surface.
- Structures become volumetric (`Gen3D.StructureCell`), so any slice looks like a vanilla structure:
  - the dungeon is a 3D maze of brick rooms, corridors and shafts with platforms around the vanilla dungeon;
  - cabins, the temple, pyramids, underworld houses and island houses are the vanilla profile revolved around the vertical axis with a square metric (rooms walled on all sides, square pyramids, a ziggurat temple);
  - living trees, hives, islands and the shimmer use a round metric (a cylindrical trunk, a ball-shaped crown, a spherical hive);
  - chests, altars, orbs and life crystals exist only in the central slice, inside their structure.
- Only the player's changes are saved, so the world file barely grows.

## Rendering
- Blocks are meshed per 16³ chunk with the vanilla tile frames; grass tops use hand-drawn 8×8 textures (`GrassTextures`, converted into `Assets/GrassTop` by `build.sh`).
- Lighting: sky light, block light sources, the underworld glow, and one moving light carried by the player (a torch or glowstick in hand, or a light pet).
- Liquids use vanilla's animated liquid textures; lava glows.
- NPCs, minions and pets are drawn with vanilla code into an atlas. Chains, yoyo strings, fishing lines, whips and afterimage trails are vanilla's own drawing, captured and placed in 3D (`Rendering/SpriteCapture.cs`).

## Controls
| Key | Action |
|---|---|
| Mouse | Look |
| W A S D | Move (works with any keyboard layout) |
| Space | Jump |
| X | Drop through platforms, climb down |
| LMB / RMB | Use items, mine, build / interact (chests open in any slice; RMB on an NPC to talk and shop) |
| Hook key | Grappling hook, aimed at the block under the crosshair |
| Esc | Inventory, frees the cursor |
| Left Alt (hold) | Free the cursor |
| F5 | First / third person camera |
| F6 | Toggle 3D |

The mod keys can be rebound in Settings → Controls.

## Limitations
- Singleplayer only.
- Chests and multi-tile furniture can only be placed in the slice the player stands in.
- Doors open only in their own slice.
- Shader ribbons (Zenith, Rainbow Rod, Last Prism beam and similar) are not drawn in 3D yet.

## Building
```bash
./build.sh
```
Builds the DLL and writes `TerraDepth.tmod` to `~/Library/Application Support/Terraria/tModLoader/Mods`. The game must be closed while packaging.

## Installing on another computer
See [dist/INSTALL.txt](dist/INSTALL.txt).
