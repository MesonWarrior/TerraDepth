#!/bin/bash
# Compiles TerraDepth and packages it into ~/Library/Application Support/Terraria/tModLoader/Mods/TerraDepth.tmod
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
TML="$HOME/Library/Application Support/Steam/steamapps/common/tModLoader"
SRC="$HOME/Library/Application Support/Terraria/tModLoader/ModSources/TerraDepth"
[ -e "$SRC" ] || ln -s "$HERE" "$SRC"
cd "$HERE"
# hand-drawn textures: PNG sources in GrassTextures -> ready tModLoader textures in Assets/GrassTop
python3 tools/png2rawimg.py GrassTextures Assets/GrassTop
dotnet build -c Release
cd "$TML"
dotnet tModLoader.dll -server -build "$SRC/" -eac "$HERE/bin/Release/net8.0/TerraDepth.dll" -define "TML_2026_08;RELEASE" -unsafe true
