using NUnit.Framework;
using Sol.Lighting;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sol.Tests.Editor
{
    public sealed class SolReflectionProbeAnchorTests
    {
        [Test]
        public void GiCadenceIsClampedWhenAsyncReadbackIsUnavailable()
        {
            Assert.AreEqual(1d,
                SolSkyLightingScheduler.ResolveGiUpdateInterval(true), 1e-8d);
            Assert.AreEqual(4d,
                SolSkyLightingScheduler.ResolveGiUpdateInterval(false), 1e-8d);
        }

        [Test]
        public void PriorityScore_PrefersVisibleWaterRelevantAndStaleAnchors()
        {
            float baseline = SolReflectionProbeAnchor.CalculatePriorityScore(
                10f, false, 0f, 1f);

            Assert.Greater(SolReflectionProbeAnchor.CalculatePriorityScore(
                10f, true, 0f, 1f), baseline);
            Assert.Greater(SolReflectionProbeAnchor.CalculatePriorityScore(
                10f, false, 1f, 1f), baseline);
            Assert.Greater(SolReflectionProbeAnchor.CalculatePriorityScore(
                10f, false, 0f, 20f), baseline);
            Assert.Greater(baseline, SolReflectionProbeAnchor.CalculatePriorityScore(
                100f, false, 0f, 1f));
        }

        [Test]
        public void AnchorDefaultsToItsSiblingRealtimeProbe()
        {
            GameObject go = new("Probe anchor");
            try
            {
                ReflectionProbe probe = go.AddComponent<ReflectionProbe>();
                probe.mode = ReflectionProbeMode.Realtime;
                SolReflectionProbeAnchor anchor =
                    go.AddComponent<SolReflectionProbeAnchor>();

                Assert.AreSame(probe, anchor.Source);
                Assert.IsFalse(anchor.IsRenderPending);
                Assert.AreEqual(go.transform.position, anchor.WorldInfluenceBounds.center);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ShippingWaterPrefab_RegistersItsExistingRealtimeProbe()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Earth-Sky-Water/Water/Shaders/WaterTile.prefab");

            Assert.IsNotNull(prefab);
            ReflectionProbe probe = prefab.GetComponentInChildren<ReflectionProbe>(true);
            SolReflectionProbeAnchor anchor =
                prefab.GetComponentInChildren<SolReflectionProbeAnchor>(true);
            Assert.IsNotNull(probe);
            Assert.IsNotNull(anchor);
            Assert.AreSame(probe, anchor.Source);
            Assert.AreEqual(1f, anchor.WaterRelevance, 1e-5f);
        }

        [TestCase(2f, 0.039f, 0.001f, false)]
        [TestCase(2f, 0.041f, 0.001f, true)]
        [TestCase(1.9f, 0f, 0f, false)]
        [TestCase(2.1f, 0f, 0f, true)]
        public void ProbeInvalidationThresholds_AreStable(
            float sunAngle, float cloudDelta, float ambientDelta, bool expected)
        {
            Vector3 previousSun = Vector3.forward;
            Vector3 currentSun = Quaternion.AngleAxis(sunAngle, Vector3.up) * previousSun;
            Color previousAmbient = Color.black;
            Color currentAmbient = new(ambientDelta, 0f, 0f);

            Assert.AreEqual(expected, SolSkyLightingScheduler.HasMeaningfulChange(
                currentSun, cloudDelta, currentAmbient,
                previousSun, 0f, previousAmbient));
        }
    }
}
