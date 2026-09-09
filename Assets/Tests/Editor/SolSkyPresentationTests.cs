using System;
using System.Reflection;
using NUnit.Framework;
using Sol.Environment.EditorTools;
using Sol.ToD;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Sol.Tests.Editor
{
    // Sample the production shader, including Unity's actual cube-face convention.
    // Comparing the baker to itself cannot detect an incorrectly oriented cubemap.
    public sealed class SolSkyPresentationTests
    {
        Material _material;
        float _previousFrameActive, _previousBackdropActive;
        Vector4 _previousStellarParams;
        Texture _previousBackdrop;

        [SetUp]
        public void SetUp()
        {
            _previousFrameActive = Shader.GetGlobalFloat("_SolSkyFrameActive");
            _previousBackdropActive = Shader.GetGlobalFloat("_SolSkyStellarBackdropActive");
            _previousStellarParams = Shader.GetGlobalVector("_SolSkyStellarParams");
            _previousBackdrop = Shader.GetGlobalTexture("_SolSkyStellarBackdrop");
            Shader.SetGlobalFloat("_SolSkyFrameActive", 0);
            Shader.SetGlobalFloat("_SolSkyStellarBackdropActive", 0);
            Shader.SetGlobalVector("_SolSkyStellarParams", Vector4.zero);
            _material = new Material(Shader.Find("Sol/Skybox"));
            foreach (string name in new[] { "_ZenithColor", "_HorizonColor", "_NadirColor",
                         "_SunDiscColor", "_CoronaColor", "_MoonColor", "_MoonDarkColor" })
                _material.SetColor(name, Color.black);
            foreach (string name in new[] { "_StarIntensity", "_GalaxyIntensity",
                         "_AuroraIntensity", "_SunGlowIntensity", "_HazeIntensity",
                         "_TwilightIntensity", "_HorizonRefraction", "_HorizonFlatten",
                         "_SunExtinction" })
                _material.SetFloat(name, 0);
            _material.SetFloat("_ZenithBlend", 0.01f);
            _material.SetVector("_SunDirection", Vector3.down);
            _material.SetVector("_MoonDirection", Vector3.down);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_material);
            Shader.SetGlobalFloat("_SolSkyFrameActive", _previousFrameActive);
            Shader.SetGlobalFloat("_SolSkyStellarBackdropActive", _previousBackdropActive);
            Shader.SetGlobalVector("_SolSkyStellarParams", _previousStellarParams);
            Shader.SetGlobalTexture("_SolSkyStellarBackdrop", _previousBackdrop);
        }

        [Test]
        public void BakedFaceOrientationMatchesGpuSamplingIncludingEdges()
        {
            const int size = 64;
            Vector3 axis = new Vector3(0.2f, 0.7f, 0.5f).normalized;
            var cube = new Cubemap(size, TextureFormat.RGBAHalf, false);
            try
            {
                for (int face = 0; face < 6; face++)
                {
                    var colors = new Color[size * size];
                    for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        Vector3 direction = SolStellarBackdropBaker.Direction(
                            (CubemapFace)face, (x + 0.5f) / size, (y + 0.5f) / size);
                        colors[y * size + x] = new Color(0, 0, 0,
                            0.5f + Vector3.Dot(direction, axis) * 0.25f);
                    }
                    cube.SetPixels(colors, (CubemapFace)face);
                }
                cube.Apply();
                Shader.SetGlobalTexture("_SolSkyStellarBackdrop", cube);
                Shader.SetGlobalFloat("_SolSkyStellarBackdropActive", 1);
                Shader.SetGlobalVector("_SolSkyStellarParams", new Vector4(1, 0, 0, 0));
                _material.SetColor("_GalaxyColor1", Color.white);
                _material.SetFloat("_NightFactor", 1);
                foreach (Vector3 direction in new[] {
                    new Vector3(1, .4f, .2f), new Vector3(-1, .4f, -.2f),
                    new Vector3(.2f, 1, .4f), new Vector3(.2f, -1, .4f),
                    new Vector3(.2f, .4f, 1), new Vector3(.2f, .4f, -1),
                    new Vector3(1, .999f, .2f), new Vector3(1, 1.001f, .2f),
                    new Vector3(.2f, .999f, 1), new Vector3(.2f, 1.001f, 1) })
                {
                    bool below = direction.y < 0;
                    _material.SetFloat("_StarRotation", below ? Mathf.PI : 0);
                    Vector3 view = below
                        ? new Vector3(direction.x, -direction.y, -direction.z) : direction;
                    float expected = .5f + Vector3.Dot(direction.normalized, axis) * .25f;
                    Color[] pixels = Render(_material, view, .1f);
                    Assert.That(pixels[32 * 64 + 32].r, Is.EqualTo(expected).Within(.005f),
                        $"Cubemap orientation differs at {direction}.");
                }
            }
            finally { Object.DestroyImmediate(cube); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void StarsTwinkleVisiblyWithStationaryCloudTime(bool baked)
        {
            Cubemap cube = null;
            try
            {
                if (baked)
                {
                    cube = new Cubemap(4, TextureFormat.RGBAHalf, false);
                    var colors = new Color[16];
                    Array.Fill(colors, new Color(1, .5f, .3f, 0));
                    for (int face = 0; face < 6; face++) cube.SetPixels(colors, (CubemapFace)face);
                    cube.Apply();
                    Shader.SetGlobalTexture("_SolSkyStellarBackdrop", cube);
                    Shader.SetGlobalFloat("_SolSkyStellarBackdropActive", 1);
                }
                _material.SetFloat("_StarIntensity", 10);
                _material.SetFloat("_StarHeight", -0.08f); // Older profile fallback remains usable.
                _material.SetFloat("_StarPower", 20);
                _material.SetFloat("_StarTwinkleAmount", .65f);
                _material.SetFloat("_StarTwinkleSpeed", 1.25f);
                _material.SetFloat("_CloudTime", 0);
                Color[] first = Render(_material, Vector3.up, 90);
                _material.SetFloat("_StarTime", .25f);
                Color[] later = Render(_material, Vector3.up, 90);
                float brightness = 0, difference = 0;
                for (int i = 0; i < first.Length; i++)
                {
                    brightness = Mathf.Max(brightness, first[i].maxColorComponent);
                    difference = Mathf.Max(difference, Mathf.Abs(first[i].r - later[i].r),
                        Mathf.Abs(first[i].g - later[i].g), Mathf.Abs(first[i].b - later[i].b));
                }
                Assert.That(brightness, Is.GreaterThan(.001f), "Control must contain stars.");
                Assert.That(difference / brightness, Is.GreaterThan(.1f),
                    "Stars must visibly change over a quarter-second with cloud time frozen.");
            }
            finally { if (cube != null) Object.DestroyImmediate(cube); }
        }

        [TestCase(-1f, 1f)]
        [TestCase(0f, .5f)]
        [TestCase(1f, 0f)]
        public void MoonCenterTracksFullQuarterAndNewPhase(float sunY, float expected)
        {
            _material.SetColor("_MoonColor", Color.white);
            _material.SetTexture("_MoonSurfaceTex", Texture2D.whiteTexture);
            _material.SetFloat("_MoonDiscSize", Mathf.Cos(2.25f * Mathf.Deg2Rad));
            _material.SetFloat("_MoonSharpness", 4);
            _material.SetVector("_MoonDirection", Vector3.up);
            _material.SetVector("_SunDirection", new Vector3(0, sunY, 1 - Mathf.Abs(sunY)));
            Color[] pixels = Render(_material, Vector3.up, .1f);
            Assert.That(pixels[32 * 64 + 32].r, Is.EqualTo(expected).Within(.005f));
        }

        [TestCase(30f, 2f, 0f, 3.5f, 4.5f, 0f, 0f)]
        [TestCase(30f, 0f, 0f, 3.5f, 4.5f, 0f, 0f)]
        [TestCase(30f, 0f, 0f, 4f, 2f, 0f, 0f)]
        [TestCase(3f, 2f, 0f, 3.5f, 4.5f, 1f, .45f)]
        [TestCase(5f, 0f, 1.5f, 3.5f, 4.5f, 1f, .8f)]
        [TestCase(5.47f, .48f, 0f, 3.5f, 4.5f, 1f, .45f)]
        public void ForegroundMoonGpuCoverageMatchesSolarLighting(float altitude, float azimuthOffset,
            float altitudeOffset, float sunDiameter, float moonDiameter, float refraction, float flatten)
        {
            Vector3 sun = Quaternion.AngleAxis(-altitude, Vector3.right) * Vector3.forward;
            Vector3 moon = Quaternion.AngleAxis(azimuthOffset, Vector3.up)
                * Quaternion.AngleAxis(-altitudeOffset, Vector3.right) * sun;
            _material.SetVector("_SunDirection", sun);
            _material.SetColor("_SunDiscColor", Color.white);
            _material.SetFloat("_SunDiscSize", Mathf.Cos(sunDiameter * .5f * Mathf.Deg2Rad));
            _material.SetFloat("_MoonDiscSize", Mathf.Cos(moonDiameter * .5f * Mathf.Deg2Rad));
            _material.SetFloat("_SunLimbDarkening", 0);
            _material.SetFloat("_HorizonRefraction", refraction);
            _material.SetFloat("_HorizonFlatten", flatten);
            _material.SetFloat("_SolarEclipseFactor", 0); // Coverage must never depend on this fade.
            double unobstructed = SolarEnergy(Render(_material, sun, 10, 512));
            Assert.That(unobstructed, Is.GreaterThan(1000));
            _material.SetVector("_MoonDirection", moon);
            double eclipsed = SolarEnergy(Render(_material, sun, 10, 512));
            float expected = SolEclipseGeometry.SolarOcclusion(sun, moon, sunDiameter, moonDiameter, refraction, flatten);
            Assert.That(1d - eclipsed / unobstructed, Is.EqualTo(expected).Within(.006),
                "Lighting coverage must match the production shader's opaque lunar silhouette.");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SunAndMoonGpuDiscsDisappearBelowHorizon(bool sun)
        {
            _material.SetColor(sun ? "_SunDiscColor" : "_MoonColor", Color.white);
            _material.SetTexture("_MoonSurfaceTex", Texture2D.whiteTexture);
            _material.SetFloat("_SunLimbDarkening", 0);
            _material.SetFloat("_MoonSharpness", 4);
            foreach (float elevation in new[] { .06f, -.06f })
            {
                Vector3 direction = new Vector3(0, elevation, 1).normalized;
                _material.SetVector("_SunDirection", sun ? direction : -direction);
                _material.SetVector("_MoonDirection", sun ? -direction : direction);
                Color[] pixels = Render(_material, direction, .1f);
                Assert.That(pixels[32 * 64 + 32].r,
                    Is.EqualTo(elevation > 0 ? 1f : 0f).Within(.005f));
            }
        }

        static double SolarEnergy(Color[] pixels)
        {
            double total = 0;
            foreach (Color pixel in pixels) total += pixel.r;
            return total;
        }

        [Test]
        public void TwinkleClockIgnoresCloudSpeedAndFastForwardButRespectsPause()
        {
            var owner = new GameObject("Sky presentation clock test");
            try
            {
                TimeOfDay time = owner.AddComponent<TimeOfDay>();
                Set(time, "animateInEditMode", true);
                Set(time, "cloudBaseSpeed", 0f);
                time.SetTimeScale(100);
                Set(time, "_editorClockStamp", EditorApplication.timeSinceStartup - 1d);
                typeof(TimeOfDay).GetMethod("AdvanceEditModeClock", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(time, null);
                float first = (float)Get(time, "starTime");
                Assert.That(first, Is.EqualTo(.1f).Within(.001f)); // Bounded editor delta.
                time.SetPaused(true);
                Set(time, "_editorClockStamp", EditorApplication.timeSinceStartup - 1d);
                typeof(TimeOfDay).GetMethod("AdvanceEditModeClock", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(time, null);
                Assert.That((float)Get(time, "starTime"), Is.EqualTo(first));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        static void Set(object target, string name, object value)
            => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

        static object Get(object target, string name)
            => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);

        internal static Color[] Render(Material material, Vector3 direction, float fieldOfView, int size = 64)
        {
            var cameraObject = new GameObject("Sky shader test camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.transform.rotation = Quaternion.LookRotation(direction,
                Mathf.Abs(direction.normalized.y) > .99f ? Vector3.forward : Vector3.up);
            camera.fieldOfView = fieldOfView;
            camera.aspect = 1;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 10;
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            rt.Create();
            RenderTexture previous = RenderTexture.active;
            var texture = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true);
            var command = new CommandBuffer();
            try
            {
                command.SetRenderTarget(rt);
                command.ClearRenderTarget(true, true, Color.magenta);
                command.SetViewProjectionMatrices(camera.worldToCameraMatrix,
                    GL.GetGPUProjectionMatrix(camera.projectionMatrix, true));
                command.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0);
                Graphics.ExecuteCommandBuffer(command);
                RenderTexture.active = rt;
                texture.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                texture.Apply();
                return texture.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                command.Release();
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(primitive);
                Object.DestroyImmediate(cameraObject);
            }
        }
    }
}
