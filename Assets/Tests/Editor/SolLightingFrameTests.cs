using System.IO;
using NUnit.Framework;
using Sol.Lighting;
using UnityEngine;

namespace Sol.Tests.Editor
{
    public sealed class SolLightingFrameTests
    {
        static SolDirectionalLightState LightState(float intensity, bool enabled = true)
            => new(null, Vector3.up, Color.white, intensity, 1f, enabled);

        [Test]
        public void DominantLight_HoldsItsPreviousChoiceInsideHysteresisBand()
        {
            SolDirectionalLightState sun = LightState(1f);
            SolDirectionalLightState moon = LightState(1.05f);

            Assert.AreEqual(
                SolDominantLightKind.Sun,
                SolLightingResolver.ResolveDominant(sun, moon, SolDominantLightKind.Sun));
            Assert.AreEqual(
                SolDominantLightKind.Moon,
                SolLightingResolver.ResolveDominant(sun, moon, SolDominantLightKind.Moon));
        }

        [Test]
        public void DominantLight_SwitchesOnlyAfterHysteresisIsExceeded()
        {
            SolDirectionalLightState sun = LightState(1f);
            SolDirectionalLightState moon = LightState(1.11f);

            Assert.AreEqual(
                SolDominantLightKind.Moon,
                SolLightingResolver.ResolveDominant(sun, moon, SolDominantLightKind.Sun));
        }

        [Test]
        public void DominantLight_RejectsDisabledCandidates()
        {
            Assert.AreEqual(
                SolDominantLightKind.None,
                SolLightingResolver.ResolveDominant(
                    LightState(5f, enabled: false),
                    LightState(5f, enabled: false),
                    SolDominantLightKind.None));
        }

        [TestCase(-1f, 1f)]
        [TestCase(0f, 1f)]
        [TestCase(0.5f, 0.625f)]
        [TestCase(1f, 0.25f)]
        [TestCase(2f, 0.25f)]
        public void WeatherAttenuation_IsBoundedAndDeterministic(float dim, float expected)
        {
            Assert.AreEqual(expected, SolLightingResolver.ResolveWeatherAttenuation(dim), 1e-5f);
        }

        [Test]
        public void Lightning_RemainsSeparateFromStableCaptureState()
        {
            SolDirectionalLightState sun = LightState(1f);
            SolDirectionalLightState moon = LightState(0f, enabled: false);
            SolTrilightAmbient stable = new(
                new Color(0.2f, 0.3f, 0.4f),
                new Color(0.1f, 0.2f, 0.3f),
                new Color(0.05f, 0.08f, 0.1f));
            SolTrilightAmbient flashed = new(
                stable.Sky + Color.white,
                stable.Equator + Color.white,
                stable.Ground);

            SolLightingFrame frame = new(
                1UL,
                sun,
                moon,
                SolDominantLightKind.Sun,
                flashed,
                stable,
                1f,
                0f,
                1f,
                0f,
                0.8f);

            Assert.AreEqual(0.8f, frame.Lightning, 1e-5f);
            Assert.AreEqual(stable.Sky, frame.StableAmbient.Sky);
            Assert.AreNotEqual(frame.Ambient.Sky, frame.StableAmbient.Sky);
        }

        [Test]
        public void DirectionalResolution_AppliesWeatherOnlyToIntensity()
        {
            Quaternion rotation = Quaternion.Euler(15f, 30f, 0f);
            SolDirectionalLightState candidate = new(
                null,
                new Vector3(1f, 2f, 3f),
                new Color(0.8f, 0.7f, 0.6f),
                4f,
                0.65f,
                true,
                rotation);

            SolDirectionalLightState resolved =
                SolLightingResolver.ResolveDirectional(candidate, 0.25f);

            Assert.AreEqual(1f, resolved.Intensity, 1e-5f);
            Assert.AreEqual(candidate.Color, resolved.Color);
            Assert.That(Vector3.Distance(candidate.Direction, resolved.Direction), Is.LessThan(1e-6f));
            Assert.AreEqual(candidate.Rotation, resolved.Rotation);
            Assert.AreEqual(candidate.ShadowStrength, resolved.ShadowStrength);
            Assert.IsTrue(resolved.Enabled);
        }

        [Test]
        public void CelestialBlend_UsesBlackKeyWhenBothBodiesAreUnavailable()
        {
            SolCelestialLightBlend blend = SolLightingResolver.ResolveCelestialBlend(
                LightState(2f, enabled: false),
                LightState(3f, enabled: false),
                0.25f);

            Assert.AreEqual(Color.black, blend.Radiance);
            Assert.AreEqual(0f, blend.Intensity);
            Assert.AreEqual(0f, blend.ShadowStrength);
            Assert.That(blend.Direction.sqrMagnitude, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void CelestialBlend_AddsBothRadiancesAtCrossover()
        {
            SolDirectionalLightState sun = new(
                null, Vector3.right, new Color(1f, 0.5f, 0.25f), 0.4f, 0f, true);
            SolDirectionalLightState moon = new(
                null, Vector3.forward, new Color(0.25f, 0.5f, 1f), 0.2f, 0f, true);

            SolCelestialLightBlend blend = SolLightingResolver.ResolveCelestialBlend(
                sun, moon, 0.5f);

            Assert.AreEqual(sun.Radiance + moon.Radiance, blend.Radiance);
            Assert.AreEqual(sun.Intensity + moon.Intensity, blend.Intensity, 1e-5f);
            Assert.That(blend.SunWeight, Is.InRange(0f, 1f));
            Assert.That(blend.ShadowStrength, Is.InRange(0f, 1f));
            Assert.That(blend.Direction.sqrMagnitude, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void CelestialBlend_FadesContinuouslyIntoAmbientOnlyTwilight()
        {
            SolDirectionalLightState moon = new(
                null, Vector3.forward, Color.white, 0.001f, 0f, true);
            SolDirectionalLightState unavailable = LightState(5f, enabled: false);

            SolCelestialLightBlend beforeGap = SolLightingResolver.ResolveCelestialBlend(
                unavailable, moon, 0f);
            SolCelestialLightBlend inGap = SolLightingResolver.ResolveCelestialBlend(
                unavailable, unavailable, 0f);

            Assert.AreEqual(0.001f, beforeGap.Radiance.maxColorComponent, 1e-6f);
            Assert.AreEqual(0f, inGap.Radiance.maxColorComponent, 1e-6f);
        }

        [Test]
        public void TimeOfDay_DoesNotCommitDirectionalOrAmbientLighting()
        {
            string timeOfDay = File.ReadAllText(
                "Assets/Earth-Sky-Water/TimeOfDay/TimeofDay.cs");
            string waterManager = File.ReadAllText(
                "Assets/Earth-Sky-Water/Scripts/Management/WaterManager.cs");
            string director = File.ReadAllText(
                "Assets/Earth-Sky-Water/Scripts/Lighting/SolLightingDirector.cs");

            Assert.That(timeOfDay, Does.Not.Contain("sunLight.transform.rotation ="));
            Assert.That(timeOfDay, Does.Not.Contain("moonLight.transform.rotation ="));
            Assert.That(timeOfDay, Does.Not.Contain("RenderSettings.sun ="));
            Assert.That(timeOfDay, Does.Not.Contain("RenderSettings.ambientMode ="));
            Assert.That(timeOfDay, Does.Not.Contain("RenderSettings.ambientLight ="));
            Assert.That(waterManager, Does.Not.Contain("_SolSunDirectionID"));
            Assert.That(waterManager, Does.Not.Contain("_SolLightningFlashID"));

            Assert.That(director, Does.Contain("ApplyDirectional(frame.Sun)"));
            Assert.That(director, Does.Contain("ApplyDirectional(frame.Moon)"));
            Assert.That(director, Does.Contain("RenderSettings.sun = frame.DominantLight"));
            Assert.That(director, Does.Contain("RenderSettings.ambientMode = ambientMode"));
        }
    }
}
