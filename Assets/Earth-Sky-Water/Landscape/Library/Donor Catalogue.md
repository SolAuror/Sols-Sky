# Curated landscape resources

Source root: `B:\Projects\Unity\AssetUnpacking\Random Unpacking\RandomUnpacking\Assets` (user-provided licensed modding resources).

`DonorManifest.json` is the machine-readable import manifest. Original TIFFs are retained under Sources; Working contains normalized RGBA masks, colour/normal PNGs and correctly scaled height PNGs. Converter v3 preserves TIFF fourth samples marked as unspecified data and scales unsigned 16-bit heights across their full range. Source TerrainLayer mask remaps are recorded explicitly. Grass Moss A, Stone A and Cliff Mossy A have separate height sources. Other imported HDRP masks use neutral 0.5 height.

| Source | Included or reference use |
| --- | --- |
| `Visual Design Cafe/Nature Renderer Demo/Realistic/Art/Ground` | Grass A, Grass Soil A, Heather A, Pebbles B, Cliff Mossy E; source smoothness remaps retained. |
| `TerrainDemoScene_URP/Terrain/Textures` | Black Sand A, Black Sand Rocks B, Grass Moss A, Rock Jagged B, Tidal Pools B. |
| `TerrainDemoScene_URP/Prefabs/Rocks/Textures` | Stone A and Cliff Mossy A, including height maps. |
| `TerrainDemoScene_URP/Terrain/Brushes` | 45 brushes, 45 corresponding heightmaps and importer preset, under Elementa's Editor/Brushes directory. |
| Procedural Terrain Painter | Reference for layer cards, modifier/rule controls, texture masks, heatmaps and multi-terrain orchestration. Its alphamap-repaint architecture is not a runtime dependency. |
| `Hivemind/TownSmith/HDRP(Default)/Art/Terrain` | Deferred paths/wear collection: TL_Road, TL_Mud and TL_MudFootPrints. TerrainTextures contains `T_road_*`, `T_mud_*`, `T_mudFootprints_*`, including RMA and BaseColor_MaskMap files. No conversion performed; inspect channels and material bindings before interpreting packing. |
| Sorcerer's Hut terrain artwork | Mud style reference for a later collection; not included in either starter palette. |
| `Visual Design Cafe/Nature Renderer Demo/Scripts/RuntimeEditing.cs` | Reference for terrain cloning, bounded edits and collider synchronization. It removes vegetation; it is not a landscape material editor. |
| TerrainDemoScene_URP foliage subgraphs | Deferred techniques: wind deformation, terrain-colour matching, distance fade, wrapped lighting and canopy AO. No foliage/weather/water framework imported. |

Open `Examples/Material Preview.unity` to compare the twelve donors and three existing legacy materials. The grid runs left to right, then northward: Temperate's eight materials, five distinct Volcanic Coast donors, then Stone A and Cliff Mossy A. Select the named terrain to identify a sample. The separate Landscape Designer scene demonstrates automatic rules and a cross-tile override path.
