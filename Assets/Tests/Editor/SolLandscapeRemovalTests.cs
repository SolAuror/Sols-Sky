using System;
using System.Linq;
using NUnit.Framework;
using Sol.Landscape;
using UnityEngine;

namespace Sol.Tests.Editor
{
    public sealed partial class SolLandscapeDesignerTests
    {
        [TestCase(0), TestCase(1), TestCase(2), TestCase(3), TestCase(4), TestCase(5), TestCase(6), TestCase(7)]
        public void Removal_AllChannelsPersistRestoreRepaintAndCancel(int layer)
        {
            var group = Fixture(1, 1, 8); group.profile.fallbackMaterialId = group.profile.Layers[(layer + 1) % 8].materialId;
            var data = group.tiles[0].paint; int pixel = (32 * 64 + 32) * 4 + layer % 4, map = 4 + layer / 4;
            using var stroke = new SolLandscapeStroke();
            void Dab(SolLandscapePaintOperation operation, float opacity = 1) { stroke.BeginStroke(group, operation, layer); stroke.ApplyDab(new Vector3(32, 0, 32), 12, opacity, .9f); stroke.CommitStroke(); }
            Dab(SolLandscapePaintOperation.RemoveMaterial); Assert.That(data.Bytes(map)[pixel], Is.EqualTo(255));
            Assert.That(data.HasOverrides || data.HasExclusions, Is.False); Assert.That(data.StorageBytes, Is.EqualTo(8L * 64 * 64)); Assert.That(data.Version, Is.EqualTo(2));
            var stored = data.Snapshot();
            Dab(SolLandscapePaintOperation.RestoreRemovedMaterial, .5f); Assert.That(data.Bytes(map)[pixel], Is.InRange(126, 129));
            data.Restore(stored); Assert.That(data.Bytes(map)[pixel], Is.EqualTo(255));
            stroke.BeginStroke(group, SolLandscapePaintOperation.Paint, layer); stroke.ApplyDab(new Vector3(32, 0, 32), 12, 1, .9f); stroke.CancelStroke();
            Assert.That(data.HasOverrides, Is.False); Assert.That(data.Bytes(map)[pixel], Is.EqualTo(255));
            Dab(SolLandscapePaintOperation.Paint); Assert.That(data.Bytes(map)[pixel], Is.Zero); Assert.That(data.Bytes(layer / 4)[pixel], Is.EqualTo(255));
        }
        [Test] public void Removal_GuardsFallbackProtectedAndRestoringEmptyData()
        {
            var group = Fixture(1, 1); using var stroke = new SolLandscapeStroke();
            Assert.Throws<InvalidOperationException>(() => stroke.BeginStroke(group, SolLandscapePaintOperation.RemoveMaterial, 0));
            group.profile.Layers[1].terrainLayer.name = "Rock";
            Assert.Throws<InvalidOperationException>(() => stroke.BeginStroke(group, SolLandscapePaintOperation.RemoveMaterial, 1));
            stroke.BeginStroke(group, SolLandscapePaintOperation.RestoreRemovedMaterial, 1); stroke.ApplyDab(new Vector3(32, 0, 32), 12, 1); stroke.CommitStroke();
            Assert.That(group.tiles[0].paint.StorageBytes, Is.Zero);
            Assert.That((int)SolLandscapePaintOperation.RestoreExcluded, Is.EqualTo(3));
        }
        [Test] public void Removal_RemapResizeLegacySnapshotAndFourTileJunction()
        {
            var group = Fixture(2, 2, 8); using var stroke = new SolLandscapeStroke();
            stroke.BeginStroke(group, SolLandscapePaintOperation.RemoveMaterial, 7); stroke.ApplyDab(new Vector3(64, 0, 64), 12, .5f); stroke.CommitStroke();
            int[] coordinates = { 4095, 4032, 63, 0 };
            for (int i = 0; i < 4; i++) Assert.That(group.tiles[i].paint.Bytes(5)[coordinates[i] * 4 + 3], Is.InRange(126, 129));
            var paint = group.tiles[0].paint; paint.Remap(new[] { 7, 1, 2, 3, 4, 5, 6, 0 }); paint.Resize(128);
            Assert.That(paint.Bytes(4)[(128 * 128 - 1) * 4], Is.InRange(126, 129));
            paint.Restore(new byte[4][]); Assert.That(paint.HasRemovals, Is.False, "Version-one snapshots do not reinterpret exclusion data.");
        }
        [TestCase("Manual"), TestCase("Auto"), TestCase("Override"), TestCase("Mixed"), TestCase("AllManual")]
        public void Removal_RenderedBaseRulesAndOverridesRestoreAndRepaint(string source)
        {
            var group = Fixture(1, 1); group.profile.heightBlend = false;
            var path = group.profile.Layers[1]; path.terrainLayer.name = "Path";
            path.mode = source == "Manual" || source == "Mixed" || source == "AllManual" ? SolLandscapeLayerMode.Manual : SolLandscapeLayerMode.Auto;
            if (source == "AllManual") group.profile.Layers[0].mode = SolLandscapeLayerMode.Manual;
            if (path.mode == SolLandscapeLayerMode.Manual)
            {
                var weights = new float[32, 32, 2]; for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) weights[y, x, 1] = 1;
                group.tiles[0].terrain.terrainData.SetAlphamaps(0, 0, weights);
            }
            var camera = Keep(new GameObject("Removal camera")).AddComponent<Camera>(); camera.transform.position = new Vector3(32, 60, 32); camera.transform.rotation = Quaternion.Euler(90, 0, 0); camera.orthographic = true; camera.orthographicSize = 32;
            var rt = Keep(new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)); camera.targetTexture = rt;
            var pixels = Keep(new Texture2D(64, 64, TextureFormat.RGBA32, false, true));
            float Read(int layer) { group.SetPreview(this, null, null, 1, layer); group.Publish(); camera.Render(); var previous = RenderTexture.active; RenderTexture.active = rt; pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); RenderTexture.active = previous; return pixels.GetPixel(32, 32).g; }
            using var stroke = new SolLandscapeStroke();
            void Dab(SolLandscapePaintOperation operation) { stroke.BeginStroke(group, operation, 1); stroke.ApplyDab(new Vector3(32, 0, 32), 12, 1, .9f); stroke.CommitStroke(); }
            if (source == "Override" || source == "Mixed") Dab(SolLandscapePaintOperation.Paint);
            float before = Read(1); Assert.That(before, Is.GreaterThan(.1f));
            Dab(SolLandscapePaintOperation.RemoveMaterial); Assert.That(Read(1), Is.LessThan(.02f), source); Assert.That(Read(0), Is.GreaterThan(.95f));
            Dab(SolLandscapePaintOperation.RestoreRemovedMaterial); Assert.That(Read(1), Is.EqualTo(before).Within(.02f));
            Dab(SolLandscapePaintOperation.RemoveMaterial); Dab(SolLandscapePaintOperation.Paint); Assert.That(Read(1), Is.GreaterThan(.95f));
        }
        [Test] public void Removal_ExistingMasksCannotRemoveNewlyProtectedAutomaticRock()
        {
            var group = Fixture(1, 1); var rock = group.profile.Layers[1]; rock.paintProtection = SolLandscapePaintProtection.Off;
            using var stroke = new SolLandscapeStroke(); stroke.BeginStroke(group, SolLandscapePaintOperation.RemoveMaterial, 1); stroke.ApplyDab(new Vector3(32, 0, 32), 100, 1); stroke.CommitStroke();
            rock.paintProtection = SolLandscapePaintProtection.On; group.Publish();
            var block = new MaterialPropertyBlock(); group.tiles[0].terrain.GetSplatMaterialPropertyBlock(block);
            Assert.That(block.GetVectorArray("_Sol_LandscapeRuleSettings")[1].z, Is.EqualTo(1));
            Assert.That(block.GetVector("_Sol_LandscapePaintFlags").w, Is.EqualTo(1));
            Assert.That(block.GetTexture("_Sol_LandscapePaint5"), Is.Not.Null);
        }
    }
}
