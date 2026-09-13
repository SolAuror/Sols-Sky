using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sol.Landscape
{
    public enum SolLandscapePaintOperation { Paint, EraseToAutomatic, Exclude, RestoreExcluded, RemoveMaterial, RestoreRemovedMaterial }

    /// <summary>GPU paint transaction. No editor dependencies or readbacks during ApplyDab.</summary>
    public sealed class SolLandscapeStroke : IDisposable
    {
        sealed class Change { public byte[][] before; public RectInt region; public int maps; }
        readonly Dictionary<SolLandscapePaintData, Change> changes = new();
        readonly List<SolLandscapeTile> created = new();
        static readonly Dictionary<SolLandscapeGroup, SolLandscapeStroke> owners = new();
        SolLandscapeGroup group;
        SolLandscapePaintOperation operation;
        int layer;
        Material brush;
        bool active;
        public bool IsActive => active;
        public event Action<SolLandscapeTile> BeforeTileChange;
        public event Action<SolLandscapePaintData> Committed;
        public void BeginStroke(SolLandscapeGroup target, SolLandscapePaintOperation mode, int materialIndex)
        {
            if (IsActive) throw new InvalidOperationException("Finish the current stroke first.");
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (owners.ContainsKey(target)) throw new InvalidOperationException("Another stroke is already editing this landscape. Finish or cancel it first.");
            if (!target.Validate(out string reason)) throw new InvalidOperationException(reason);
            if (materialIndex < 0 || materialIndex >= target.profile.Layers.Count) throw new ArgumentOutOfRangeException(nameof(materialIndex));
            if ((mode == SolLandscapePaintOperation.Exclude || mode == SolLandscapePaintOperation.RemoveMaterial) && materialIndex == target.profile.FallbackIndex) throw new InvalidOperationException("The fallback ground cannot be removed. Choose another fallback in Setup first.");
            if ((mode == SolLandscapePaintOperation.Paint || mode == SolLandscapePaintOperation.Exclude || mode == SolLandscapePaintOperation.RemoveMaterial) && target.profile.Layers[materialIndex].ProtectAutomaticCoverage)
                throw new InvalidOperationException("This automatic material is protected. Paint another material; its stroke will leave the protected coverage intact. Change Paint protection to Off to paint this material directly.");
            if (!Enum.IsDefined(typeof(SolLandscapePaintOperation), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            var shader = target.profile.paintShader != null ? target.profile.paintShader : Shader.Find("Hidden/Sol/Landscape Paint");
            if (shader == null) throw new InvalidOperationException("The landscape paint shader is missing.");
            brush ??= new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            group = target; operation = mode; layer = materialIndex;
            owners.Add(target, this); active = true;
        }
        public void ApplyDab(Vector3 worldPosition, float radius, float opacity, float hardness = .5f, Texture stamp = null, float rotationDegrees = 0)
        {
            if (!IsActive) throw new InvalidOperationException("BeginStroke must be called first.");
            if (group == null) { CancelStroke(); throw new InvalidOperationException("The landscape was removed during the stroke."); }
            if (!(radius > 0) || !Finite(radius) || !Finite(opacity) || !Finite(hardness) || !Finite(rotationDegrees)
                || !Finite(worldPosition.x) || !Finite(worldPosition.z)) return;
            opacity = Mathf.Clamp01(opacity);
            if (opacity <= 0) return;
            foreach (var tile in group.tiles)
            {
                var t = tile.terrain; var origin = t.transform.position; var size = t.terrainData.size;
                float x = worldPosition.x - origin.x, z = worldPosition.z - origin.z;
                float nearestX = x - Mathf.Clamp(x, 0, size.x), nearestZ = z - Mathf.Clamp(z, 0, size.z);
                if (nearestX * nearestX + nearestZ * nearestZ >= radius * radius) continue;
                if (operation == SolLandscapePaintOperation.EraseToAutomatic && (tile.paint == null || !tile.paint.HasOverrides)) continue;
                if (operation == SolLandscapePaintOperation.RestoreExcluded && (tile.paint == null || !tile.paint.HasExclusions)) continue;
                if (operation == SolLandscapePaintOperation.RestoreRemovedMaterial && (tile.paint == null || !tile.paint.HasRemovals)) continue;
                BeforeTileChange?.Invoke(tile);
                if (tile.paint == null) { tile.paint = ScriptableObject.CreateInstance<SolLandscapePaintData>(); tile.paint.Initialize(group.paintResolution); created.Add(tile); }
                var data = tile.paint; int first = operation >= SolLandscapePaintOperation.RemoveMaterial ? 4 : operation >= SolLandscapePaintOperation.Exclude ? 2 : 0;
                if (!changes.TryGetValue(data, out var change))
                { change = new Change { before = data.Snapshot() }; changes.Add(data, change); data.AllocateChannels(first / 2); }
                int n = data.Resolution;
                int minX = Mathf.Clamp(Mathf.FloorToInt((x - radius) / size.x * (n - 1)), 0, n - 1);
                int minY = Mathf.Clamp(Mathf.FloorToInt((z - radius) / size.z * (n - 1)), 0, n - 1);
                int maxX = Mathf.Clamp(Mathf.CeilToInt((x + radius) / size.x * (n - 1)) + 1, 1, n);
                int maxY = Mathf.Clamp(Mathf.CeilToInt((z + radius) / size.z * (n - 1)) + 1, 1, n);
                var rect = new RectInt(minX, minY, maxX - minX, maxY - minY);
                if (change.region.width == 0) change.region = rect;
                else change.region = new RectInt(Mathf.Min(rect.xMin, change.region.xMin), Mathf.Min(rect.yMin, change.region.yMin),
                    Mathf.Max(rect.xMax, change.region.xMax) - Mathf.Min(rect.xMin, change.region.xMin), Mathf.Max(rect.yMax, change.region.yMax) - Mathf.Min(rect.yMin, change.region.yMin));
                brush.SetVector("_Brush", new Vector4(x / size.x, z / size.z, radius / size.x, radius / size.z));

                brush.SetFloat("_Resolution", n); brush.SetFloat("_Rotation", rotationDegrees * Mathf.Deg2Rad);
                brush.SetTexture("_Stamp", stamp != null ? stamp : Texture2D.whiteTexture);
                void DabPair(int start, int shaderOperation)
                {
                    brush.SetVector("_Settings", new Vector4(opacity, Mathf.Clamp(hardness, 0, .999f), shaderOperation, layer % 4));
                    for (int i = start; i < start + 2; i++)
                    {
                        change.maps |= 1 << i;
                        brush.SetFloat("_SelectedMap", i - start == layer / 4 ? 1 : 0);
                        var target = data.GetWorking(i); var temp = RenderTexture.GetTemporary(target.descriptor);
                        var previous = RenderTexture.active;
                        try { Graphics.Blit(target, temp, brush); Graphics.Blit(temp, target); }
                        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(temp); }
                    }
                }
                DabPair(first, operation >= SolLandscapePaintOperation.RemoveMaterial ? (int)operation - 2 : (int)operation);
                // Deliberate repainting also reveals this material, with exactly the same falloff/stamp.
                if (operation == SolLandscapePaintOperation.Paint && data.HasRemovals) DabPair(4, 3);
                data.Revision++;
            }
            group.Publish();
        }
        public void CommitStroke()
        {
            foreach (var pair in changes)
            { for (int i = 0; i < 6; i++) if ((pair.Value.maps & (1 << i)) != 0) pair.Key.CommitRegion(i, pair.Value.region); Committed?.Invoke(pair.Key); }
            Complete();
        }
        public void CancelStroke()
        {
            foreach (var pair in changes) if (pair.Key != null) pair.Key.Restore(pair.Value.before);
            foreach (var tile in created) { if (tile.paint != null) SolLandscapePaintData.DestroyTexture(tile.paint); tile.paint = null; }
            Complete();
        }
        /// <summary>End a transaction after external undo has restored canonical data. Never overwrite that restored state with the stroke snapshot.</summary>
        public void DiscardAfterExternalRestore()
        {
            foreach (var pair in changes) if (pair.Key != null) pair.Key.RefreshTextures();
            Complete();
        }
        void Complete()
        {
            changes.Clear(); created.Clear();
            var target = group;
            if (!ReferenceEquals(target, null)) owners.Remove(target);
            group = null; active = false;
            if (target != null) { target.Invalidate(); if (target.isActiveAndEnabled) target.Publish(); }
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public void Dispose() { if (IsActive) CancelStroke(); if (brush != null) SolLandscapePaintData.DestroyTexture(brush); }
    }
}
