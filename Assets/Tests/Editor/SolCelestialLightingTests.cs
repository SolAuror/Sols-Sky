using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Sol.Lighting;
using Sol.ToD;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Sol.Tests.Editor
{
    public sealed class SolCelestialLightingTests
    {
        [Test]
        public void VisibleMoonEmitsAlongsideSunWithSkyProfile()
        {
            var owner = new GameObject("Overlapping celestial test");
            var profile = ScriptableObject.CreateInstance<SolSkyProfile>();
            try
            {
                var time = owner.AddComponent<TimeOfDay>();
                Set(time, "skyProfile", profile);
                Set(time, "timeOfDay", .625f); // 3pm, sun still well above horizon.
                Set(time, "initialLunarPhase", .75f);
                Set(time, "lunarTiltDegrees", 0f);
                Invoke(time, "UpdateEditModePreview");
                Invoke(time, "RefreshEnvironmentFromWeather");
                var frame = SolLightingDirector.ResolveFrame();
                Assert.That(frame.Sun.Direction.y, Is.GreaterThan(.2f));
                Assert.That(frame.Moon.Direction.y, Is.GreaterThan(.2f));
                Assert.That(frame.Sun.Source.enabled, Is.True);
                Assert.That(frame.Moon.Source.enabled, Is.True);
                Assert.That(frame.Moon.Intensity, Is.GreaterThan(.05f));
                Assert.That(Shader.GetGlobalInt("_SolCelestialLightCount"), Is.EqualTo(2));
                Vector4[] directions = Shader.GetGlobalVectorArray("_SolCelestialDirections");
                Assert.That(Vector3.Distance(directions[0], frame.Sun.Direction), Is.LessThan(1e-5));
                Assert.That(Vector3.Distance(directions[1], frame.Moon.Direction), Is.LessThan(1e-5));
            }
            finally { Object.DestroyImmediate(owner); Object.DestroyImmediate(profile); }
            Assert.That(Shader.GetGlobalInt("_SolCelestialLightCount"), Is.Zero,
                "Disabling the owner must not leave emitters in the atmosphere.");
        }

        [Test]
        public void SunsetKeepsWarmRadianceWhileDiscIsAboveHorizon()
        {
            var owner = new GameObject("Sunset radiance test");
            try
            {
                var time = owner.AddComponent<TimeOfDay>();
                Set(time, "timeOfDay", .742f);
                Invoke(time, "UpdateSun", .5f);
                var candidate = (SolDirectionalLightState)typeof(TimeOfDay)
                    .GetProperty("SunLightingCandidate", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(time);
                Assert.That(candidate.Direction.y, Is.InRange(.04f, .06f));
                Assert.That(candidate.Color.r, Is.GreaterThan(.9f), "Color must not fade to black.");
                Assert.That(candidate.Radiance.r, Is.GreaterThan(.3f));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [TestCase(-.06f, 0f)]
        [TestCase(-.05f, 0f)]
        [TestCase(.02f, 1f)]
        [TestCase(.5f, 1f)]
        public void EachBodyUsesItsOwnSmoothHorizonVisibility(float elevation, float expected)
            => Assert.That(SolLightingResolver.CelestialVisibility(elevation), Is.EqualTo(expected).Within(1e-5));

        [Test]
        public void ShadowOwnershipChangesWithoutChangingEmittedRadiance()
        {
            Assert.That(SolLightingResolver.MainShadowVisibility(1f, 1.05f), Is.Zero);
            Assert.That(SolLightingResolver.MainShadowVisibility(1.11f, 1f), Is.Zero);
            Assert.That(SolLightingResolver.MainShadowVisibility(2f, 1f), Is.EqualTo(1f));
            Assert.That(SolLightingResolver.MainShadowVisibility(1f, 0f), Is.EqualTo(1f));
        }

        [Test]
        public void AdditionalBodyCreatesLightAndParticipatesInDirector()
        {
            var owner = new GameObject("Additional celestial test");
            var prefab = new GameObject("Fantasy moon prefab");
            prefab.AddComponent<CelestialBody>();
            var config = ScriptableObject.CreateInstance<CelestialBodyConfig>();
            config.hasLight = true;
            config.lightColor = Color.green;
            config.initialPhase = .25f;
            config.maxLightIntensity = 3f;
            config.castShadows = false;
            TimeOfDay time = null;
            try
            {
                time = owner.AddComponent<TimeOfDay>();
                Set(time, "tertiaryPlanets", new[] { new TertiaryPlanetEntry { prefab = prefab, config = config } });
                Invoke(time, "SpawnCelestialBodies");
                Invoke(time, "UpdateTertiaryPlanets", 0f);
                Invoke(time, "RefreshEnvironmentFromWeather");
                var body = time.GetTertiaryPlanet(0);
                Assert.That(body.AttachedLight, Is.Not.Null);
                Assert.That(body.AttachedLight.enabled, Is.True);
                Assert.That(body.AttachedLight.intensity, Is.EqualTo(3f).Within(.001f));
                Assert.That(body.AttachedLight.shadows, Is.EqualTo(LightShadows.None));
                Assert.That(SolLightingDirector.ResolveFrame().DominantLight, Is.EqualTo(body.AttachedLight));
                Assert.That(Shader.GetGlobalInt("_SolCelestialLightCount"), Is.EqualTo(1));

                body.Direction = Vector3.down;
                Invoke(time, "RefreshEnvironmentFromWeather");
                Assert.That(body.AttachedLight.enabled, Is.False);
                Assert.That(Shader.GetGlobalInt("_SolCelestialLightCount"), Is.Zero);
                body.Direction = Vector3.up;
                config.hasLight = false;
                Invoke(time, "RefreshEnvironmentFromWeather");
                Assert.That(body.AttachedLight.enabled, Is.False);
            }
            finally
            {
                // Runtime OnDestroy uses deferred Destroy for instantiated bodies.
                if (time != null) Set(time, "tertiaryInstances", null);
                Object.DestroyImmediate(owner);
                Object.DestroyImmediate(prefab);
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void AtmosphereGpuAddsIndependentLobesAndShadowsOnlyTheOwner()
        {
            var material = new Material(Shader.Find("Hidden/Sol/Tests/CelestialAtmosphere"));
            var source = new GameObject("Shadow owner").AddComponent<Light>();
            var globals = new Dictionary<string, Vector4>();
            string[] vectors = { "_SolAtmosphereParams2", "_SolAtmosphereVolumetricParams",
                "_SolAtmosphereLightingParams", "_SolAtmosphereSkyParams", "_SolAtmosphereSunDirection" };
            foreach (var name in vectors) globals[name] = Shader.GetGlobalVector(name);
            float flash = Shader.GetGlobalFloat("_SolAtmosphereLightning");
            try
            {
                Shader.SetGlobalVector("_SolAtmosphereParams2", new Vector4(1, .6f, 0, 0));
                Shader.SetGlobalVector("_SolAtmosphereVolumetricParams", new Vector4(1, 0, 0, 0));
                Shader.SetGlobalVector("_SolAtmosphereLightingParams", new Vector4(0, 0, 0, 100));
                Shader.SetGlobalVector("_SolAtmosphereSkyParams", Vector4.zero);
                Shader.SetGlobalVector("_SolAtmosphereSunDirection", Vector3.up);
                Shader.SetGlobalFloat("_SolAtmosphereLightning", 0);
                var sun = new SolDirectionalLightState(source, Vector3.up, Color.red, 1, 1, true);
                var moon = new SolDirectionalLightState(null, Vector3.forward, Color.blue, 1, 1, true);
                var buffer = new SolCelestialLighting();
                material.SetVector("_TestView", Vector3.forward);
                material.SetFloat("_TestShadow", 1);
                Publish(buffer, sun, default);
                Color sunOnly = Sample(material);
                Publish(buffer, default, moon);
                Color moonOnly = Sample(material);
                Publish(buffer, sun, moon);
                Color both = Sample(material);
                Assert.That(both.r, Is.EqualTo(sunOnly.r).Within(.001f));
                Assert.That(both.b, Is.EqualTo(moonOnly.b).Within(.001f));
                Assert.That(both.b, Is.GreaterThan(both.r * 5), "Moon lobe must stay at the moon.");
                material.SetFloat("_TestShadow", 0);
                Color shadowed = Sample(material);
                Assert.That(shadowed.r, Is.LessThan(.001f));
                Assert.That(shadowed.b, Is.EqualTo(moonOnly.b).Within(.001f));
                Publish(buffer, default, default);
                Assert.That(Sample(material).maxColorComponent, Is.LessThan(.001f));
            }
            finally
            {
                foreach (var pair in globals) Shader.SetGlobalVector(pair.Key, pair.Value);
                Shader.SetGlobalFloat("_SolAtmosphereLightning", flash);
                SolCelestialLighting.Clear();
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(source.gameObject);
            }
        }

        static void Publish(SolCelestialLighting buffer, SolDirectionalLightState sun, SolDirectionalLightState moon)
            => buffer.Publish(new SolLightingFrame(1, sun, moon, SolDominantLightKind.Sun,
                default, default, 1, 0, 1, 0, 0), Array.Empty<SolDirectionalLightState>());

        static Color Sample(Material material)
        {
            var target = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var texture = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(Texture2D.blackTexture, target, material);
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                texture.Apply();
                return texture.GetPixel(2, 2);
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(texture);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        static void Set(object target, string field, object value)
            => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        static void Invoke(object target, string method, params object[] args)
            => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    }
}
