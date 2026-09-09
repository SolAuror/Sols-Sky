using System.IO;
using NUnit.Framework;
using Sol.Environment.EditorTools;
using Sol.ToD;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sol.Tests.Editor
{
    public sealed class SolSkyOverhaulTests
    {
        SolSkyProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ScriptableObject.CreateInstance<SolSkyProfile>();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        [TestCase(-0.24f)]
        [TestCase(-0.05f)]
        [TestCase(0f)]
        [TestCase(0.2f)]
        [TestCase(1f)]
        public void ResolverIsFiniteAcrossSolarStates(float sunY)
        {
            SolSkyFrame frame = Resolve(sunY, 0.4f, 0.6f, 4200f);
            Assert.That(frame.IsValid, Is.True);
            for (int i = -100; i <= 100; i++)
            {
                Color color = frame.EvaluateRadiance(new Vector3(1f, i / 100f, 0.25f));
                Assert.That(float.IsFinite(color.r) && float.IsFinite(color.g)
                            && float.IsFinite(color.b), Is.True);
            }
        }

        [Test]
        public void GradientWeightsAreNormalizedAndHorizonIsContinuous()
        {
            SolSkyFrame frame = Resolve(0f, 0f, 0f, 0f);
            Color left = frame.EvaluateRadiance(new Vector3(1f, -0.0001f, 0f));
            Color right = frame.EvaluateRadiance(new Vector3(1f, 0.0001f, 0f));
            Assert.That(ColorDistance(left, right), Is.LessThan(0.005f));
            for (int i = -100; i <= 100; i++)
            {
                Vector3 weights = SolSkyResolver.NormalizedGradientWeights(
                    i / 100f, frame.GradientParameters);
                Assert.That(weights.x + weights.y + weights.z, Is.EqualTo(1f).Within(1e-5f));
                Assert.That(weights.x, Is.GreaterThanOrEqualTo(0f));
                Assert.That(weights.y, Is.GreaterThanOrEqualTo(0f));
                Assert.That(weights.z, Is.GreaterThanOrEqualTo(0f));
            }
        }

        [Test]
        public void AltitudeAndWeatherTransitionsRemainContinuous()
        {
            SolSkyFrame previous = Resolve(0.1f, 0f, 0f, 0f);
            for (int i = 1; i <= 100; i++)
            {
                float t = i / 100f;
                SolSkyFrame next = Resolve(0.1f, t, t, t * 12000f);
                Assert.That(ColorDistance(previous.Zenith, next.Zenith), Is.LessThan(0.04f));
                Assert.That(Mathf.Abs(previous.FogDensity - next.FogDensity), Is.LessThan(0.001f));
                previous = next;
            }
        }

        [Test]
        public void LightningChangesPresentedButNotStableAmbient()
        {
            SolSkyFrame stable = Resolve(0.2f, 0.6f, 0f, 0f);
            SolSkyFrame flash = Resolve(0.2f, 0.6f, 1f, 0f);
            Assert.That(ColorDistance(stable.StableAmbientSky, flash.StableAmbientSky), Is.LessThan(1e-6f));
            Assert.That(ColorDistance(stable.PresentedAmbientSky, flash.PresentedAmbientSky), Is.GreaterThan(0.1f));
        }

        [Test]
        public void AtmosphereConvergenceIsIdempotentAtSharedRadiance()
        {
            Color target = Resolve(0.05f, 0.2f, 0f, 0f).EvaluateRadiance(Vector3.forward);
            foreach (float transmittance in new[] { 0f, 0.15f, 0.5f, 0.92f, 1f })
            {
                Color result = target * transmittance + target * (1f - transmittance);
                Assert.That(ColorDistance(result, target), Is.LessThan(1e-6f));
            }
        }

        [Test]
        public void ProfileSwitchInvalidatesHistoryAndNullRestoresCompatibilityFallback()
        {
            GameObject owner = new("Sky Profile Switch Test");
            try
            {
                TimeOfDay time = owner.AddComponent<TimeOfDay>();
                int before = SolCloudController.Active.HistoryRevision;
                time.SetSkyProfile(_profile);
                Assert.That(time.SkyProfile, Is.SameAs(_profile));
                Assert.That(time.CurrentSkyFrame.IsValid, Is.True);
                Assert.That(SolCloudController.Active.HistoryRevision, Is.GreaterThan(before));
                time.SetSkyProfile(null);
                Assert.That(time.SkyProfile, Is.Null);
                Assert.That(time.CurrentSkyFrame.IsValid, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void MigrationIsIdempotent()
        {
            const string path = "Assets/__SolSkyMigrationIdempotence.asset";
            AssetDatabase.DeleteAsset(path);
            GameObject owner = new("Sky Migration Test");
            try
            {
                TimeOfDay time = owner.AddComponent<TimeOfDay>();
                SolSkyProfileMigration.Result first =
                    SolSkyProfileMigration.CreateOrReuse(time, path);
                SolSkyProfileMigration.Result second =
                    SolSkyProfileMigration.CreateOrReuse(time, path);
                Assert.That(first.Created, Is.True);
                Assert.That(second.Created, Is.False);
                Assert.That(second.Profile, Is.SameAs(first.Profile));
            }
            finally
            {
                Object.DestroyImmediate(owner);
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void StellarBakerChecksumIsDeterministicAndEdgesUseContinuousDirections()
        {
            Assert.That(SolStellarBackdropBaker.ContentChecksum(16, 42),
                Is.EqualTo(SolStellarBackdropBaker.ContentChecksum(16, 42)));
            Vector3 positiveXTop = SolStellarBackdropBaker.Direction(
                CubemapFace.PositiveX, 0.5f, 0f);
            Vector3 positiveYRight = SolStellarBackdropBaker.Direction(
                CubemapFace.PositiveY, 1f, 0.5f);
            Assert.That(Vector3.Angle(positiveXTop, positiveYRight), Is.LessThan(0.001f));
            Color a = SolStellarBackdropBaker.Evaluate(positiveXTop, 42, 0, 1024);
            Color b = SolStellarBackdropBaker.Evaluate(positiveYRight, 42, 0, 1024);
            Assert.That(ColorDistance(a, b), Is.LessThan(1e-5f));
        }

        [Test]
        public void SharedAtmosphereAndWaterContractsRemainWiredWithoutExtraPasses()
        {
            string atmosphere = File.ReadAllText(
                "Assets/Earth-Sky-Water/Water/Shaders/SolAtmosphere.hlsl");
            string atmosphereShader = File.ReadAllText(
                "Assets/Earth-Sky-Water/Shaders/SolAtmosphere.shader");
            string water = File.ReadAllText(
                "Assets/Earth-Sky-Water/Scripts/Rendering/SolOceanClipmap.cs");
            string body = File.ReadAllText(
                "Assets/Earth-Sky-Water/TimeOfDay/CelestialBodies/CelestialBody.shader");
            string skybox = File.ReadAllText(
                "Assets/Earth-Sky-Water/TimeOfDay/CelestialBodies/Sol_Skybox.shader");
            StringAssert.Contains("SolEvaluateResolvedSkyRadiance", atmosphere);
            StringAssert.Contains("SolSkyFrame frame", water);
            StringAssert.Contains("SolApplyAtmosphere", body);
            foreach (string feature in new[]
            {
                "StarField", "GalaxyIntensity", "AuroraIntensity", "SunDiscColor",
                "MoonSurfaceTex", "SolarEclipseFactor", "LunarEclipseFactor",
                "CoronaColor", "SolSkyStellarBackdrop"
            })
                StringAssert.Contains(feature, skybox);
            Assert.That(System.Text.RegularExpressions.Regex.Matches(
                atmosphereShader, @"(?m)^\s*Pass\s*$").Count, Is.EqualTo(5));
        }

        [Test]
        public void ShippedProfilesAndLunarCreditArePresent()
        {
            SolSkyProfile grounded = AssetDatabase.LoadAssetAtPath<SolSkyProfile>(
                "Assets/Earth-Sky-Water/Sky Profiles/Sol_Sky_Grounded.asset");
            SolSkyProfile legacy = AssetDatabase.LoadAssetAtPath<SolSkyProfile>(
                "Assets/Earth-Sky-Water/Sky Profiles/Sol_Sky_Legacy.asset");
            Assert.That(grounded, Is.Not.Null);
            Assert.That(legacy, Is.Not.Null);
            Assert.That(grounded.lunarSurface, Is.Not.Null);
            Assert.That(Shader.Find("Sol/Skybox"), Is.Not.Null);
            Assert.That(File.Exists("Assets/Earth-Sky-Water/TimeOfDay/Textures/NASA_CGI_Moon_Kit_Credit.md"), Is.True);
        }

        SolSkyFrame Resolve(float sunY, float weather, float lightning, float altitude)
        {
            Vector3 sun = new Vector3(Mathf.Sqrt(Mathf.Max(0f, 1f - sunY * sunY)), sunY, 0f);
            return SolSkyResolver.Resolve(_profile, new SolSkyResolveInput(
                sun, -sun, 0f, 0f, weather, weather, weather, 0f, lightning,
                altitude, true, true, 1));
        }

        static float ColorDistance(Color a, Color b)
            => Mathf.Sqrt((a.r - b.r) * (a.r - b.r)
                        + (a.g - b.g) * (a.g - b.g)
                        + (a.b - b.b) * (a.b - b.b));

    }
}
