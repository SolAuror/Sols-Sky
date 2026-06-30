using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(WaterTileGrid))]
public class WaterTileGridEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var grid = (WaterTileGrid)target;

        EditorGUILayout.Space();

        // Stats
        int diameter   = grid.gridRadius * 2 + 1;
        int totalTiles = diameter * diameter;
        float totalSize = diameter * grid.tileSize;

        EditorGUILayout.HelpBox(
            $"Grid: {diameter}x{diameter} = {totalTiles} tiles\n" +
            $"World footprint: {totalSize:0}m x {totalSize:0}m\n" +
            $"Verts per tile (full LOD): {(grid.tileResolution + 1) * (grid.tileResolution + 1):N0}\n" +
            $"Auto volume: {(grid.autoWaterVolume ? $"{totalSize + grid.volumeMargin * 2:0}m x {totalSize + grid.volumeMargin * 2:0}m x {grid.volumeDepth:0}m deep" : "disabled")}",
            MessageType.None);

        EditorGUILayout.Space();

        if (GUILayout.Button("Rebuild Tiles", GUILayout.Height(30)))
        {
            grid.RebuildAll();
            SceneView.RepaintAll();
        }
    }
}
