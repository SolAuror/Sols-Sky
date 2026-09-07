using System.Reflection;
using NUnit.Framework;
using Sol.Lighting;
using UnityEngine;

namespace Sol.Tests.Editor
{
    public sealed class SolEnvironmentLightTests
    {
        [Test]
        public void NightActivation_UsesTheDocumentedDayThresholds()
        {
            Assert.AreEqual(1f, SolEnvironmentLight.EvaluateActivation(
                SolEnvironmentLightActivation.NightOnly, null, 0.1f, 0f), 1e-5f);
            Assert.AreEqual(0f, SolEnvironmentLight.EvaluateActivation(
                SolEnvironmentLightActivation.NightOnly, null, 0.25f, 0f), 1e-5f);
            Assert.That(SolEnvironmentLight.EvaluateActivation(
                SolEnvironmentLightActivation.NightOnly, null, 0.175f, 0f),
                Is.InRange(0.45f, 0.55f));
        }

        [Test]
        public void WeatherDim_CanActivateNightLightsEarly()
        {
            Assert.AreEqual(0f, SolEnvironmentLight.EvaluateActivation(
                SolEnvironmentLightActivation.NightOnly, null, 1f, 0.55f), 1e-5f);
            Assert.AreEqual(1f, SolEnvironmentLight.EvaluateActivation(
                SolEnvironmentLightActivation.NightOnly, null, 1f, 0.85f), 1e-5f);
        }

        [Test]
        public void CustomCurve_IsEvaluatedAgainstDayFactor()
        {
            AnimationCurve curve = AnimationCurve.Linear(0f, 0.2f, 1f, 0.8f);

            Assert.AreEqual(0.5f, SolEnvironmentLight.EvaluateActivation(
                SolEnvironmentLightActivation.CustomCurve, curve, 0.5f, 1f), 1e-5f);
            Assert.AreEqual(1f, SolEnvironmentLight.EvaluateActivation(
                SolEnvironmentLightActivation.AlwaysOn, curve, 1f, 0f), 1e-5f);
        }

        [Test]
        public void SelectionScore_PrioritizesAuthoredPriorityAndRetainedLights()
        {
            float lowPriority = SolEnvironmentLight.CalculateSelectionScore(
                0, 10f, 10f, 10f, false);
            float highPriority = SolEnvironmentLight.CalculateSelectionScore(
                1, 1f, 100f, 0.1f, false);
            float retained = SolEnvironmentLight.CalculateSelectionScore(
                0, 10f, 10f, 10f, true);

            Assert.Greater(highPriority, lowPriority);
            Assert.Greater(retained, lowPriority);
        }

        [Test]
        public void ComponentDefaultsToAnOptInLocalLight()
        {
            GameObject go = new("Managed local light");
            try
            {
                Light light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.intensity = 3f;
                SolEnvironmentLight managed = go.AddComponent<SolEnvironmentLight>();

                Assert.AreSame(light, managed.Source);
                Assert.IsTrue(managed.IsLocal);
                Assert.AreEqual(3f, managed.AuthoredIntensity, 1e-5f);
                Assert.AreEqual(SolEnvironmentLightActivation.NightOnly,
                    managed.Activation);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ManagedLightsRemainRealtimeAndRestoreTheirAuthoredBakeMode()
        {
            GameObject go = new("Managed APV light");
            try
            {
                Light light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.lightmapBakeType = LightmapBakeType.Baked;
                SolEnvironmentLight managed = go.AddComponent<SolEnvironmentLight>();
                MethodInfo apply = typeof(SolEnvironmentLight).GetMethod(
                    "ApplyResolvedState", BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.IsNotNull(apply);
                apply.Invoke(managed, new object[] { true, false, false, 1f, 1f });
                Assert.AreEqual(LightmapBakeType.Realtime, light.lightmapBakeType,
                    "Managed settlement lights must not be captured into APV data.");

                managed.enabled = false;
                Assert.AreEqual(LightmapBakeType.Baked, light.lightmapBakeType,
                    "Releasing management must restore the authored bake mode.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
