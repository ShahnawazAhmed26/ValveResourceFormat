# Source 1 and GoldSrc

Source 1 and GoldSrc games open in the GUI viewer. They are not supported by the CLI and the library is not published.

Source 1 maps, models, materials and textures are converted to Source 2 resources in memory and drawn by the Source 2 renderer. The converted resources are not written out as compiled files.

## Supported

### Source 1

- Game mounting from `gameinfo.txt`:
  - search paths with `+` path IDs
  - `|gameinfo_path|`, `|all_source_engine_paths|` and `|appid_N|`
  - wildcard folders, sorted case-insensitively
  - VPK v1 and v2
  - `update` and `_dlcN` folders
  - the map pakfile
- BSP v19 to v21:
  - the L4D2 lump header
  - LZMA lumps
  - external lump files (`_l_N.lmp`)
  - texture, model and pakfile names in UTF-8 or Latin-1
  - pakfile entries (stored, deflate, LZMA)
  - displacements
  - world and brush entity collision from the physics collision lump, with player and NPC clips, water and displacement collision
  - static props v4 to v11, including 72-byte v7 and v10 entries, and fade distances
  - overlays, water overlays and overlay fades
  - per-face cubemap materials
  - light entities with styles, attenuation, cones and HDR keys
- MDL v44 to v49 with VVD, VTX (both strip group layouts, detected per file), PHY and ANI:
  - include models
  - skins and bodygroups
  - sequences, blends and pose parameters
  - procedural bones (axis interp, quat interp, aim at bone and attachment)
  - local hierarchy
  - flexes, flex controllers and flex rules, stereo flexes and eyelids, set with the flex controller sliders
- VTF 7.0 to 7.5:
  - all mips, faces and frames
  - sphere maps
  - DXT, ATI and the uncompressed and HDR formats
- VMT:
  - patch, `#base` and `#include`
  - fallback blocks
  - key and bracket conditions
  - proxies, evaluated once at load: math, sine, ramp, noise, texture scroll and transform, animated texture
- Shaders:
  - LightmappedGeneric and WorldVertexTransition: per-texture transforms, `$seamless_scale`, detail blend modes, bump and ssbump lighting, blend modulate and masked blending
  - VertexLitGeneric with phong, blend tint, selfillum and envmap
  - UnlitGeneric and UnlitTwoTexture
  - Modulate and MonitorScreen
  - WorldTwoTextureBlend
  - Water (cheap path)
  - Refract
  - Eyes and teeth
  - Infected
  - Sprite
  - Cable
  - DecalModulate
  - detail sprites
- PCF particles through DMX binary v1 to v5 and keyvalues2, from the game and map particle manifests.
- Entity classes from the FGD files of each game, and Hammer tool visuals.

### GoldSrc

- Game mounting from `liblist.gam`:
  - the base game
  - `fallback_dir`
  - `_addon`, `_hd` and `_downloads` folders
- BSP v30, including the Blue Shift lump order, with WAD3 textures (miptex, qpic and font lumps), light styles, render modes and render fx 14, scrolling, animated and random-tiled textures, and the sky.
- MDL v10 with `T.mdl` textures, sequence group files, chrome, additive and masked textures, and motion extraction.
- SPR v2 (sprite groups are flattened).
- Entity classes for Half-Life and Counter-Strike.

## Not supported

### Source 1

- BSP:
  - versions before v19, v22 and v23 (Dota 2 before Reborn, INFRA), and Strata BSP v25
  - game variants: Dark Messiah, Vindictus, Tactical Intervention (encrypted), Zeno Clash static props
  - Xbox 360 and PS3 maps
  - external lump files that replace the game lump
  - lumps that are read but not used: world lights (lights come from entities), visibility, occlusion, areas, area portals, clip portal vertices, original faces, face ids, displacement triangle flags, displacement multiblend (CS:GO), map flags, leaf water data, leaf min distance to water
  - lumps that are not read: leaf faces, displacement lightmap alphas and sample positions, face macro texture info, static prop collision (prop collision, hulls, hull verts, tris, blob), XZip pak file, phys level, overlay system levels, displacement multiblend (Black Mesa)
  - brushes and brush sides: collision comes from the physics collision lump, so brush entities keep the half unit inset the compiler gives their visible sides, and window and grate contents collide as solid
  - overlay detail levels
  - face lump v2
- Static props:
  - v12 and v13
  - screen-size fades
  - Black Mesa per-vertex lighting with 12-byte vertices (`sp_hdr_N.vhv`) and `sp_rgbe_N.vhv`
- Models:
  - MDL before v44 and v50 to v53, and the v44 variant with extra header fields
  - VTX other than v7
  - VVC
  - `.phz` physics files
  - wrinkle maps, bone flex drivers and flex animation in sequences
  - eyes looking at a target, so eyelids stay as for eyes that look straight ahead
  - the mouth flex weight that darkens the teeth (they are lit as for an open mouth)
  - local sequence layers over a blend sequence (they are laid over the blend instead of composed into it, which differs while the sequence cross fades)
  - animation event sounds
  - the illumination position attachment
  - IK chains, rules and locks
  - jiggle bone simulation
  - zero-frame animation data
  - bone controllers
  - flex controller UI groups (the controllers of stereo and n-way sliders show one by one)
- VTF:
  - 7.6
  - console formats (Xbox 360 and PS3 textures)
  - volume textures (first slice only)
- Materials:
  - animated proxies (scrolling, pulsing and animated textures stay at their first frame)
  - entity-driven proxies (left at a neutral value)
  - water reflection and refraction, flow maps, `$scroll1`/`$scroll2`, `$bottommaterial`
  - `$seamless_base` and `$seamless_detail`
  - uncompressed HDR sky textures
  - `$phongdisablehalflambert`, `$diffuseexp` and `$envmaplightscale`
  - TF2 sheen, cloak and other item effects
  - tree sway
  - alpha to coverage
  - Lightmapped_4WayBlend
  - SolidEnergy
  - Portal shaders
  - WindowImposter
  - screenspace_general
  - Wireframe
  - CS:GO WeaponDecal, Character, CustomCharacter, wear, sticker and holo parameters
  - SpriteCard, Subrect, SplineRope and Grass
  - ShatteredGlass, Core, EyeBall, VolumeClouds, and the Black Mesa `bms_*` and portal shaders
  - Black Mesa `$emissiveblend`
- Entities:
  - `env_projectedtexture`
  - `beam_spotlight` and `point_spotlight`
  - `color_correction`
  - engine exposure defaults for `env_tonemap_controller`
  - Portal 2 turret variants, paint and cube paint skins
  - VGUI screens
- Mounting:
  - sourcemods resolving `|all_source_engine_paths|` to the SDK Base install
  - `_english` language substitution
  - GMA addons
  - folders with only a `gameinfo_*.txt` file
- DMX: the `uint64` text type and the legacy `DMXVersion` headers

### GoldSrc

- BSP v29
- WAD2 and WAD4
- Console and alpha MDL versions
- Decals (`infodecal`, `decals.wad`)
- Textures from WADs the map does not list
- Models of `world_items` and `xen_spore_*` entities
- Glow sprite occlusion
- Studio render mode 1
- Render fx other than 14

## Credits

Formats and behaviour were cross-checked against these projects:

- [SourceUtils](https://github.com/Metapyziks/SourceUtils)
- [Crowbar](https://github.com/ZeqMacaw/Crowbar)
- [SourceIO](https://github.com/REDxEYE/SourceIO)
- [bspsrc](https://github.com/StrataSource/bspsrc)
- [Strata Source wiki](https://github.com/StrataSource/Wiki)
- [srctools](https://github.com/TeamSpen210/srctools)
- [sourcepp](https://github.com/craftablescience/sourcepp)
