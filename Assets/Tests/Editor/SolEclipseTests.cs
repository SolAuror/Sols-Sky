using System.Reflection;
using NUnit.Framework;
using Sol.Lighting;
using Sol.ToD;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Sol.Tests.Editor
{
    public sealed class SolEclipseTests
    {
        [TestCase(0f, 1f)]
        [TestCase(2f, .3910022f)]
        [TestCase(4f, 0f)]
        [TestCase(180f, 0f)]
        public void EqualDiscsHaveExpectedOverlap(float separation, float expected)
        {
            Vector3 moon = Quaternion.AngleAxis(separation, Vector3.right) * Vector3.up;
            Assert.That(SolEclipseGeometry.SolarOcclusion(Vector3.up, moon, 4, 4),
                Is.EqualTo(expected).Within(.0001f));
        }

        [Test]
        public void AuthoredSizeDeterminesTotalOrAnnularEclipse()
        {
            Vector3 direction = new Vector3(.4f, .3f, .7f).normalized;
            Assert.That(SolEclipseGeometry.SolarOcclusion(direction, direction, 3.5f, 4.5f), Is.EqualTo(1));
            Assert.That(SolEclipseGeometry.SolarOcclusion(direction, direction, 4, 2), Is.EqualTo(.25f).Within(1e-5));
            Assert.That(SolEclipseGeometry.SolarOcclusion(direction, direction, 4, 4), Is.EqualTo(1));
        }

        [Test]
        public void IngressIsContinuousAndMonotonic()
        {
            float previous = 0;
            for (int i = 0; i <= 400; i++)
            {
                Vector3 moon = Quaternion.AngleAxis(4f - i * .01f, Vector3.right) * Vector3.up;
                float coverage = SolEclipseGeometry.SolarOcclusion(Vector3.up, moon, 3.5f, 4.5f);
                Assert.That(coverage, Is.InRange(previous - 1e-6f, previous + .02f));
                previous = coverage;
            }
            Assert.That(previous, Is.EqualTo(1));
        }

        [TestCase(0f)]
        [TestCase(.5f)]
        [TestCase(1f)]
        public void EclipseDimmingIsAppliedOnceAndKeepsUncoveredSunColor(float coverage)
        {
            var owner = new GameObject("Eclipse lighting test");
            var profile = ScriptableObject.CreateInstance<SolSkyProfile>();
            try
            {
                var time = owner.AddComponent<TimeOfDay>();
                // Concentric smaller moons give an exact area fraction. Keep both
                // at low elevation to catch the previous zenith-only eclipse bias.
                profile.sunAngularDiameter = 4f;
                profile.moonAngularDiameter = coverage > 0 ? 4f * Mathf.Sqrt(coverage) : .1f;
                profile.horizonFlatten = 0;
                profile.horizonRefraction = 0;
                Set(time, "skyProfile", profile);
                Set(time, "eclipseApexBias", 5f);
                Vector3 direction = new Vector3(0, .05f, 1).normalized;
                Set(time, "cachedSunDirection", direction);
                Set(time, "cachedMoonDirection", coverage > 0 ? direction : Vector3.up);
                Set(time, "cachedSunLightIntensity", 2f);
                Set(time, "cachedSunLightColor", Color.yellow);
                Set(time, "cachedSunLightEnabled", true);
                Set(time, "cachedDayFactor", 1f);
                // An arbitrary phase must not make an overlapping opaque disc transparent.
                Set(time, "currentLunarPhase", .25f);
                Invoke(time, "UpdateEclipses");
                var sun = (SolDirectionalLightState)typeof(TimeOfDay)
                    .GetProperty("SunLightingCandidate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(time);
                Assert.That(time.SolarEclipseStrength, Is.EqualTo(coverage).Within(.0001f));
                Assert.That(sun.Intensity, Is.EqualTo(2f * (1f - coverage)).Within(.0001f));
                Assert.That(sun.Color, Is.EqualTo(Color.yellow));

                var input = new SolSkyResolveInput(direction, direction, coverage, 0, 0, 0, 0, 0, 0, 0, false, false, 1);
                SolSkyFrame frame = SolSkyResolver.Resolve(profile, input);
                Assert.That(frame.DirectionalParameters.y,
                    Is.EqualTo(profile.solarAureoleIntensity * (1f - coverage)).Within(.0001f));
            }
            finally { Object.DestroyImmediate(owner); Object.DestroyImmediate(profile); }
        }

        [Test]
        public void MoonStaysCloserAfterRuntimeConfigChanges()
        {
            var owner = new GameObject("Eclipse orbit test");
            var cameraObject = new GameObject("Eclipse observer") { tag = "MainCamera" };
            cameraObject.AddComponent<Camera>();
            var sunObject = new GameObject("Sun");
            var moonObject = new GameObject("Moon");
            var sunConfig = ScriptableObject.CreateInstance<CelestialBodyConfig>();
            var moonConfig = ScriptableObject.CreateInstance<CelestialBodyConfig>();
            TimeOfDay time = null;
            try
            {
                time = owner.AddComponent<TimeOfDay>();
                var sun = sunObject.AddComponent<CelestialBody>();
                var moon = moonObject.AddComponent<CelestialBody>();
                sunConfig.hasLight = moonConfig.hasLight = false;
                sunConfig.orbitDistance = 1000;
                moonConfig.orbitDistance = 600;
                sun.Initialize(sunConfig); moon.Initialize(moonConfig);
                Assert.That(Camera.main, Is.Not.Null, "Orbit refresh requires an enabled observer camera.");
                Set(time, "sunBody", sun); Set(time, "moonBody", moon);
                Set(time, "cachedSunDirection", Vector3.up);
                Set(time, "cachedMoonDirection", Vector3.up);
                Invoke(time, "UpdateCelestialBodies");
                Assert.That(moon.EffectiveOrbitDistance, Is.EqualTo(600));
                sunConfig.orbitDistance = 300;
                moonConfig.orbitDistance = 1200;
                Invoke(time, "UpdateCelestialBodies");
                Assert.That(moon.EffectiveOrbitDistance, Is.LessThan(sun.EffectiveOrbitDistance));
                Assert.That(Vector3.Distance(cameraObject.transform.position, moon.transform.position),
                    Is.LessThan(Vector3.Distance(cameraObject.transform.position, sun.transform.position)));
                Assert.That(moonConfig.orbitDistance, Is.EqualTo(1200), "Runtime ordering must not rewrite authored assets.");
            }
            finally
            {
                if (time != null) { Set(time, "sunBody", null); Set(time, "moonBody", null); }
                Object.DestroyImmediate(owner); Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(sunObject); Object.DestroyImmediate(moonObject);
                Object.DestroyImmediate(sunConfig); Object.DestroyImmediate(moonConfig);
            }
        }

        static void Set(object target, string field, object value)
            => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        static void Invoke(object target, string method)
            => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);
    }
}
