using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace Sol.Tests.Editor
{
    public sealed class Water2EditorTests
    {
        [Test]
        public void WaterPrepassDescriptor_IsResolvedAndMatchesCameraColorLayout()
        {
            Type feature = Reflection.FindType("Sol.Water.Rendering.SolWaterRendererFeature");
            MethodInfo build = feature.GetMethod("BuildPrepassDescriptor",
                BindingFlags.Static | BindingFlags.NonPublic);
            TextureDesc depth = new(Vector2.one, true, false)
            {
                dimension = TextureDimension.Tex2DArray,
                slices = 2,
                msaaSamples = MSAASamples.MSAA4x,
                bindTextureMS = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                format = GraphicsFormat.D32_SFloat,
            };

            TextureDesc color = (TextureDesc)build.Invoke(null,
                new object[] { depth, "Water prepass regression" });

            Assert.That(color.sizeMode, Is.EqualTo(depth.sizeMode));
            Assert.That(color.scale, Is.EqualTo(depth.scale));
            Assert.That(color.dimension, Is.EqualTo(depth.dimension));
            Assert.That(color.slices, Is.EqualTo(depth.slices));
            Assert.That(color.msaaSamples, Is.EqualTo(MSAASamples.None));
            Assert.That(color.useDynamicScale, Is.EqualTo(depth.useDynamicScale));
            Assert.That(color.colorFormat, Is.EqualTo(GraphicsFormat.R16G16B16A16_SFloat));
            Assert.That(color.depthBufferBits, Is.EqualTo(DepthBits.None));
            Assert.That(color.bindTextureMS, Is.False);
            Assert.That(color.clearBuffer, Is.True);
        }

        [Test]
        public void WaterBodyId_UsesStableEqualityAndHash()
        {
            Type idType = Reflection.FindType("Sol.Water.SolWaterBodyId");
            object first = Activator.CreateInstance(idType, "ocean-main");
            object second = Activator.CreateInstance(idType, "ocean-main");
            object other = Activator.CreateInstance(idType, "lake-west");

            Assert.That(first.Equals(second), Is.True);
            Assert.That(first.Equals(other), Is.False);
            Assert.That(Reflection.Get<int>(first, "StableHash24"),
                Is.EqualTo(Reflection.Get<int>(second, "StableHash24")));
            Assert.That(Reflection.Get<int>(first, "StableHash24"), Is.InRange(0, 0x00ffffff));
        }

        [Test]
        public void Gerstner_PhaseIsContinuousAcrossOriginShift()
        {
            Type profileType = Reflection.FindType("Sol.Water.SolWaterProfile");
            ScriptableObject profile = ScriptableObject.CreateInstance(profileType);
            Type originType = Reflection.FindType("Sol.Environment.SolDouble3");
            object originA = Activator.CreateInstance(originType, 0d, 0d, 0d);
            object originB = Activator.CreateInstance(originType, 1024d, 0d, -512d);
            Type evaluator = Reflection.FindType("Sol.Water.SolWaterWaveEvaluator");
            MethodInfo evaluate = evaluator.GetMethod("Evaluate",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            object sampleA = evaluate.Invoke(null, new object[]
            {
                profile, new Vector2(1040f, -480f), 123.5d, originA,
                new Vector3(0.8f, 0f, 0.2f), 1.3f, 0.4f,
            });
            object sampleB = evaluate.Invoke(null, new object[]
            {
                profile, new Vector2(16f, 32f), 123.5d, originB,
                new Vector3(0.8f, 0f, 0.2f), 1.3f, 0.4f,
            });

            Assert.That(Vector3.Distance(
                Reflection.Get<Vector3>(sampleA, "Displacement"),
                Reflection.Get<Vector3>(sampleB, "Displacement")), Is.LessThan(0.00001f));
            Assert.That(Vector3.Distance(
                Reflection.Get<Vector3>(sampleA, "Normal"),
                Reflection.Get<Vector3>(sampleB, "Normal")), Is.LessThan(0.00001f));
            UnityEngine.Object.DestroyImmediate(profile);
        }

        [Test]
        public void CameraRegistry_IsolatesHistoriesAndRejectsResize()
        {
            GameObject firstObject = new("Water camera A");
            GameObject secondObject = new("Water camera B");
            Camera first = firstObject.AddComponent<Camera>();
            Camera second = secondObject.AddComponent<Camera>();
            Type registry = Reflection.FindType("Sol.Environment.SolEnvironmentCameraRegistry");
            MethodInfo begin = registry.GetMethod("BeginCamera", BindingFlags.Static | BindingFlags.Public);
            MethodInfo end = registry.GetMethod("EndCamera", BindingFlags.Static | BindingFlags.Public);
            MethodInfo clear = registry.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public);

            try
            {
                object contextA = begin.Invoke(null, new object[] { first, new Vector2Int(1280, 720) });
                object contextB = begin.Invoke(null, new object[] { second, new Vector2Int(1280, 720) });
                Assert.That(contextA, Is.Not.SameAs(contextB));
                end.Invoke(null, new[] { contextA });
                end.Invoke(null, new[] { contextB });
                Assert.That((int)registry.GetProperty("Count").GetValue(null), Is.EqualTo(2));

                // Simulate a later camera use so the same-frame idempotence guard does not mask resize rejection.
                Reflection.Set(contextA, "LastFrameUsed", Time.frameCount - 1);
                contextA = begin.Invoke(null, new object[] { first, new Vector2Int(1920, 1080) });
                Assert.That(Reflection.Get<bool>(contextA, "CameraCut"), Is.True);
            }
            finally
            {
                clear.Invoke(null, null);
                UnityEngine.Object.DestroyImmediate(firstObject);
                UnityEngine.Object.DestroyImmediate(secondObject);
            }
        }

        [Test]
        public void Hydrology_ConservesMassAcrossConnectedNodes()
        {
            Type assetType = Reflection.FindType("Sol.Hydrology.SolHydrologyAsset");
            Type nodeType = Reflection.FindType("Sol.Hydrology.SolHydrologyNode");
            Type edgeType = Reflection.FindType("Sol.Hydrology.SolHydrologyEdge");
            ScriptableObject asset = ScriptableObject.CreateInstance(assetType);
            Array nodes = Array.CreateInstance(nodeType, 2);
            nodes.SetValue(CreateNode(nodeType, "high", 10d, 0d, 100d, 10d), 0);
            nodes.SetValue(CreateNode(nodeType, "low", 20d, 0d, 100d, 0d), 1);
            Array edges = Array.CreateInstance(edgeType, 1);
            object edge = Activator.CreateInstance(edgeType);
            Reflection.Set(edge, "id", "reach");
            Reflection.Set(edge, "sourceNodeId", "high");
            Reflection.Set(edge, "targetNodeId", "low");
            Reflection.Set(edge, "conductanceCubicMetresPerSecondPerMetre", 0.25d);
            Reflection.Set(edge, "capacityCubicMetresPerSecond", 10d);
            Reflection.Set(edge, "oneWay", true);
            edges.SetValue(edge, 0);
            Reflection.Set(asset, "nodes", nodes);
            Reflection.Set(asset, "edges", edges);

            GameObject root = new("Hydrology conservation");
            root.SetActive(false);
            Component world = Reflection.Add(root, "Sol.Hydrology.SolHydrologyWorld");
            Reflection.Set(world, "asset", asset);
            root.SetActive(true);
            Reflection.Invoke(world, "Initialize");
            try
            {
                double before = GetVolume(world, "high") + GetVolume(world, "low");
                Reflection.Invoke(world, "SimulateStep", new[] { typeof(double) }, 2d);
                double after = GetVolume(world, "high") + GetVolume(world, "low");
                Assert.That(after, Is.EqualTo(before).Within(1e-9));
                Assert.That(GetVolume(world, "high"), Is.LessThan(10d));
                Assert.That(GetVolume(world, "low"), Is.GreaterThan(20d));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void WaterQuality_TiersExposeExpectedCascadeBudgets()
        {
            Type qualityType = Reflection.FindType("Sol.Water.SolWaterQualityProfile");
            ScriptableObject quality = ScriptableObject.CreateInstance(qualityType);
            try
            {
                Reflection.Set(quality, "tier", Enum.Parse(Reflection.FindType("Sol.Water.SolWaterQualityTier"), "Low"));
                Assert.That(Reflection.Get<int>(quality, "FftCascadeCount"), Is.Zero);
                Reflection.Set(quality, "tier", Enum.Parse(Reflection.FindType("Sol.Water.SolWaterQualityTier"), "Medium"));
                Assert.That(Reflection.Get<int>(quality, "FftResolution"), Is.EqualTo(128));
                Assert.That(Reflection.Get<int>(quality, "FftCascadeCount"), Is.EqualTo(2));
                Reflection.Set(quality, "tier", Enum.Parse(Reflection.FindType("Sol.Water.SolWaterQualityTier"), "High"));
                Assert.That(Reflection.Get<int>(quality, "FftResolution"), Is.EqualTo(256));
                Assert.That(Reflection.Get<int>(quality, "FftCascadeCount"), Is.EqualTo(4));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(quality);
            }
        }

        [Test]
        public void WaterSurfaceFrame_NormalizesAndClampsSharedSurfaceContract()
        {
            Type frameType = Reflection.FindType("Sol.Water.SolWaterSurfaceFrame");
            object frame = Activator.CreateInstance(frameType, new object[]
            {
                new Vector3(1f, 2f, 3f),
                new Vector3(0f, 2f, 0f),
                new Vector3(4f, 0f, -1f),
                2f, -4f, 3f,
            });

            Assert.That(Reflection.Get<Vector3>(frame, "Normal"), Is.EqualTo(Vector3.up));
            Assert.That(Reflection.Get<float>(frame, "Foam"), Is.EqualTo(1f));
            Assert.That(Reflection.Get<float>(frame, "Depth"), Is.EqualTo(0f));
            Assert.That(Reflection.Get<float>(frame, "Confidence"), Is.EqualTo(1f));
        }

        [Test]
        public void WaterProfile_ShorelineAuthoringValidationClampsAndSerializesControls()
        {
            Type profileType = Reflection.FindType("Sol.Water.SolWaterProfile");
            ScriptableObject profile = ScriptableObject.CreateInstance(profileType);
            ScriptableObject copy = ScriptableObject.CreateInstance(profileType);
            try
            {
                Reflection.Set(profile, "shorelineMaskStrength", -2f);
                Reflection.Set(profile, "shorelineFoamWidth", -1f);
                Reflection.Set(profile, "shorelineMaskMapping", new Vector4(4f, -3f, -10f, 0f));
                Reflection.Set(profile, "shorelineDataMapping", new Vector4(-2f, 8f, 0f, -12f));
                Reflection.Set(profile, "shorelineDepthRange", -4f);
                Reflection.Set(profile, "shorelineDistanceRange", 0f);
                Reflection.Set(profile, "shallowWaveAttenuationDepth", -2f);
                Reflection.Set(profile, "shorelineContactFade", 8f);
                Reflection.Set(profile, "shorelineNormalFlattening", -1f);
                Reflection.Set(profile, "shorelineBreakerStrength", 8f);
                Reflection.Set(profile, "shorelineBreakerWidth", -2f);
                Reflection.Set(profile, "shorelineBreakerWavelength", 0f);
                Reflection.Set(profile, "shorelineBreakerSpeed", -4f);
                Reflection.Set(profile, "shorelineBreakerChoppiness", 9f);
                Reflection.Set(profile, "shorelineBreakerFoam", -3f);
                MethodInfo validate = profileType.GetMethod("OnValidate",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                validate.Invoke(profile, null);

                Assert.That(Reflection.Get<float>(profile, "shorelineMaskStrength"), Is.EqualTo(0f));
                Assert.That(Reflection.Get<float>(profile, "shorelineFoamWidth"), Is.GreaterThan(0f));
                Vector4 mapping = Reflection.Get<Vector4>(profile, "shorelineMaskMapping");
                Assert.That(mapping.z, Is.GreaterThan(0f));
                Assert.That(mapping.w, Is.GreaterThan(0f));
                Vector4 dataMapping = Reflection.Get<Vector4>(profile, "shorelineDataMapping");
                Assert.That(dataMapping.z, Is.GreaterThan(0f));
                Assert.That(dataMapping.w, Is.GreaterThan(0f));
                Assert.That(Reflection.Get<float>(profile, "shorelineDepthRange"), Is.EqualTo(0.01f));
                Assert.That(Reflection.Get<float>(profile, "shorelineDistanceRange"), Is.EqualTo(0.01f));
                Assert.That(Reflection.Get<float>(profile, "shallowWaveAttenuationDepth"), Is.EqualTo(0.01f));
                Assert.That(Reflection.Get<float>(profile, "shorelineContactFade"), Is.EqualTo(2f));
                Assert.That(Reflection.Get<float>(profile, "shorelineNormalFlattening"), Is.Zero);
                Assert.That(Reflection.Get<float>(profile, "shorelineBreakerStrength"), Is.EqualTo(2f));
                Assert.That(Reflection.Get<float>(profile, "shorelineBreakerWidth"), Is.EqualTo(0.1f));
                Assert.That(Reflection.Get<float>(profile, "shorelineBreakerWavelength"), Is.EqualTo(0.25f));
                Assert.That(Reflection.Get<float>(profile, "shorelineBreakerSpeed"), Is.Zero);
                Assert.That(Reflection.Get<float>(profile, "shorelineBreakerChoppiness"), Is.EqualTo(2f));
                Assert.That(Reflection.Get<float>(profile, "shorelineBreakerFoam"), Is.Zero);

                Vector4 authoredMapping = new(12f, -8f, 640f, 320f);
                Reflection.Set(profile, "shorelineDataMapping", authoredMapping);
                Reflection.Set(profile, "shorelineDepthRange", 24f);
                Reflection.Set(profile, "shorelineDistanceRange", 70f);
                Reflection.Set(profile, "shallowWaveAttenuationDepth", 5.5f);
                Reflection.Set(profile, "shorelineContactFade", 0.42f);
                Reflection.Set(profile, "shorelineNormalFlattening", 0.76f);
                Reflection.Set(profile, "shorelineBreakerStrength", 0.31f);
                Reflection.Set(profile, "shorelineBreakerWidth", 18f);
                Reflection.Set(profile, "shorelineBreakerWavelength", 7.25f);
                Reflection.Set(profile, "shorelineBreakerSpeed", 1.4f);
                Reflection.Set(profile, "shorelineBreakerChoppiness", 0.42f);
                Reflection.Set(profile, "shorelineBreakerFoam", 0.83f);
                string json = EditorJsonUtility.ToJson(profile);
                EditorJsonUtility.FromJsonOverwrite(json, copy);

                Assert.That(Reflection.Get<Vector4>(copy, "shorelineDataMapping"),
                    Is.EqualTo(authoredMapping));
                Assert.That(Reflection.Get<float>(copy, "shorelineDepthRange"), Is.EqualTo(24f));
                Assert.That(Reflection.Get<float>(copy, "shorelineDistanceRange"), Is.EqualTo(70f));
                Assert.That(Reflection.Get<float>(copy, "shallowWaveAttenuationDepth"), Is.EqualTo(5.5f));
                Assert.That(Reflection.Get<float>(copy, "shorelineContactFade"), Is.EqualTo(0.42f));
                Assert.That(Reflection.Get<float>(copy, "shorelineNormalFlattening"), Is.EqualTo(0.76f));
                Assert.That(Reflection.Get<float>(copy, "shorelineBreakerStrength"), Is.EqualTo(0.31f));
                Assert.That(Reflection.Get<float>(copy, "shorelineBreakerWidth"), Is.EqualTo(18f));
                Assert.That(Reflection.Get<float>(copy, "shorelineBreakerWavelength"), Is.EqualTo(7.25f));
                Assert.That(Reflection.Get<float>(copy, "shorelineBreakerSpeed"), Is.EqualTo(1.4f));
                Assert.That(Reflection.Get<float>(copy, "shorelineBreakerChoppiness"), Is.EqualTo(0.42f));
                Assert.That(Reflection.Get<float>(copy, "shorelineBreakerFoam"), Is.EqualTo(0.83f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        [Test]
        public void ShorelineBaker_DistanceFieldHasCorrectSignAndWorldScale()
        {
            Type utility = Reflection.FindType("SolWaterOverhaulSetupUtility");
            MethodInfo build = utility.GetMethod("BuildDistanceField",
                BindingFlags.Static | BindingFlags.NonPublic);
            bool[] water =
            {
                false, false, true, true, true,
                false, false, true, true, true,
                false, false, true, true, true,
            };
            float[] toLand = (float[])build.Invoke(null,
                new object[] { water, 5, 3, false, 2f, 3f });
            float[] toWater = (float[])build.Invoke(null,
                new object[] { water, 5, 3, true, 2f, 3f });

            Assert.That(toLand[2], Is.EqualTo(2f).Within(0.0001f));
            Assert.That(toLand[4], Is.EqualTo(6f).Within(0.0001f));
            Assert.That(toWater[1], Is.EqualTo(2f).Within(0.0001f));
            Assert.That(toLand[0], Is.Zero);
            Assert.That(toWater[4], Is.Zero);
        }

        [Test]
        public void WaterWaveEvaluator_AttenuatesOnlyBakedShallowShoreSamples()
        {
            Type profileType = Reflection.FindType("Sol.Water.SolWaterProfile");
            Type evaluator = Reflection.FindType("Sol.Water.SolWaterWaveEvaluator");
            ScriptableObject profile = ScriptableObject.CreateInstance(profileType);
            Texture2D shoreline = new(2, 1, TextureFormat.RGHalf, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            try
            {
                shoreline.SetPixels(new[]
                {
                    new Color(0.5f, 0.5f, 0f, 1f),
                    new Color(0.75f, 0.75f, 0f, 1f),
                });
                shoreline.Apply(false, false);
                Reflection.Set(profile, "shorelineData", shoreline);
                Reflection.Set(profile, "shorelineDataMapping", new Vector4(0f, 0f, 2f, 1f));
                Reflection.Set(profile, "shorelineDepthRange", 20f);
                Reflection.Set(profile, "shorelineDistanceRange", 20f);
                Reflection.Set(profile, "shallowWaveAttenuationDepth", 4f);
                Reflection.Set(profile, "shorelineContactFade", 0.3f);
                MethodInfo attenuation = evaluator.GetMethod("EvaluateShorelineAttenuation",
                    BindingFlags.Static | BindingFlags.NonPublic);

                object[] shallowArgs = { profile, new Vector2(-1f, 0f), 0f, false };
                object[] deepArgs = { profile, new Vector2(1f, 0f), 0f, false };
                float shallow = (float)attenuation.Invoke(null, shallowArgs);
                float deep = (float)attenuation.Invoke(null, deepArgs);

                Assert.That(shallow, Is.EqualTo(0f).Within(0.001f));
                Assert.That(deep, Is.EqualTo(1f).Within(0.001f));
                Assert.That((bool)shallowArgs[3], Is.True);
                Assert.That((bool)deepArgs[3], Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(shoreline);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void WaterWaveEvaluator_ShorelineBreakerFollowsSignedDistanceGradient()
        {
            Type profileType = Reflection.FindType("Sol.Water.SolWaterProfile");
            Type evaluator = Reflection.FindType("Sol.Water.SolWaterWaveEvaluator");
            ScriptableObject profile = ScriptableObject.CreateInstance(profileType);
            Texture2D shoreline = new(4, 2, TextureFormat.RGHalf, false, true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            try
            {
                Color[] pixels = new Color[8];
                for (int y = 0; y < 2; y++)
                for (int x = 0; x < 4; x++)
                    pixels[y * 4 + x] = new Color(0.65f, 0.52f + x * 0.035f, 0f, 1f);
                shoreline.SetPixels(pixels);
                shoreline.Apply(false, false);
                Reflection.Set(profile, "shorelineData", shoreline);
                Reflection.Set(profile, "shorelineDataMapping", new Vector4(0f, 0f, 8f, 4f));
                Reflection.Set(profile, "shorelineDepthRange", 10f);
                Reflection.Set(profile, "shorelineDistanceRange", 20f);
                Reflection.Set(profile, "shallowWaveAttenuationDepth", 4f);
                Reflection.Set(profile, "shorelineBreakerStrength", 0.4f);
                Reflection.Set(profile, "shorelineBreakerWidth", 12f);
                Reflection.Set(profile, "shorelineBreakerWavelength", 6f);
                Reflection.Set(profile, "shorelineBreakerSpeed", 1.2f);
                Reflection.Set(profile, "shorelineBreakerChoppiness", 0.5f);
                Reflection.Set(profile, "shorelineBreakerFoam", 0.8f);
                MethodInfo breaker = evaluator.GetMethod("EvaluateShorelineBreaker",
                    BindingFlags.Static | BindingFlags.NonPublic);
                object[] arguments =
                {
                    profile, Vector2.zero, 0.35d, 6f,
                    Vector3.zero, Vector3.up, Vector3.zero, 0f,
                };
                breaker.Invoke(null, arguments);

                Vector3 displacement = (Vector3)arguments[4];
                Vector3 normal = (Vector3)arguments[5];
                Vector3 velocity = (Vector3)arguments[6];
                Assert.That(displacement.sqrMagnitude, Is.GreaterThan(0.000001f));
                Assert.That(Mathf.Abs(displacement.x), Is.GreaterThan(Mathf.Abs(displacement.z)),
                    "The breaker should travel against this X-aligned shoreline gradient.");
                Assert.That(normal.y, Is.GreaterThan(0f));
                Assert.That(float.IsFinite(velocity.x) && float.IsFinite(velocity.y)
                    && float.IsFinite(velocity.z), Is.True);
                Assert.That((float)arguments[7], Is.GreaterThanOrEqualTo(0f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(shoreline);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void WaterProfile_VisualTuningControlsValidateAndSerialize()
        {
            Type profileType = Reflection.FindType("Sol.Water.SolWaterProfile");
            ScriptableObject profile = ScriptableObject.CreateInstance(profileType);
            ScriptableObject copy = ScriptableObject.CreateInstance(profileType);
            Texture2D causticTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Sky-and-Water/Water/Textures/T_CausticMap.png");
            try
            {
                Assert.That(causticTexture, Is.Not.Null);
                Reflection.Set(profile, "foamTextureScale", -3f);
                Reflection.Set(profile, "foamTextureContrast", 12f);
                Reflection.Set(profile, "foamBrightness", -1f);
                Reflection.Set(profile, "skyReflectionStrength", 4f);
                Reflection.Set(profile, "refractionStrength", 5f);
                Reflection.Set(profile, "refractionMaximumDistance", -2f);
                Reflection.Set(profile, "refractionDispersion", -1f);
                Reflection.Set(profile, "refractionMaximumScreenOffset", 1f);
                Reflection.Set(profile, "reflectionIntensity", -1f);
                Reflection.Set(profile, "horizonReflectionStrength", 3f);
                Reflection.Set(profile, "causticStrength", 9f);
                Reflection.Set(profile, "causticScale", 0f);
                MethodInfo validate = profileType.GetMethod("OnValidate",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                validate.Invoke(profile, null);

                Assert.That(Reflection.Get<float>(profile, "foamTextureScale"),
                    Is.EqualTo(0.0001f));
                Assert.That(Reflection.Get<float>(profile, "foamTextureContrast"),
                    Is.EqualTo(8f));
                Assert.That(Reflection.Get<float>(profile, "foamBrightness"), Is.Zero);
                Assert.That(Reflection.Get<float>(profile, "skyReflectionStrength"),
                    Is.EqualTo(1f));
                Assert.That(Reflection.Get<float>(profile, "refractionStrength"),
                    Is.EqualTo(2f));
                Assert.That(Reflection.Get<float>(profile, "refractionMaximumDistance"),
                    Is.EqualTo(0.1f));
                Assert.That(Reflection.Get<float>(profile, "refractionDispersion"), Is.Zero);
                Assert.That(Reflection.Get<float>(profile, "refractionMaximumScreenOffset"),
                    Is.EqualTo(0.1f));
                Assert.That(Reflection.Get<float>(profile, "reflectionIntensity"), Is.Zero);
                Assert.That(Reflection.Get<float>(profile, "horizonReflectionStrength"),
                    Is.EqualTo(1f));
                Assert.That(Reflection.Get<float>(profile, "causticStrength"), Is.EqualTo(5f));
                Assert.That(Reflection.Get<float>(profile, "causticScale"), Is.EqualTo(0.01f));

                Reflection.Set(profile, "foamTextureScale", 0.055f);
                Reflection.Set(profile, "foamTextureContrast", 2.4f);
                Reflection.Set(profile, "foamBrightness", 0.62f);
                Reflection.Set(profile, "skyReflectionStrength", 0.9f);
                Reflection.Set(profile, "refractionStrength", 0.72f);
                Reflection.Set(profile, "refractionMaximumDistance", 24f);
                Reflection.Set(profile, "refractionDispersion", 0.45f);
                Reflection.Set(profile, "refractionMaximumScreenOffset", 0.022f);
                Reflection.Set(profile, "reflectionIntensity", 0.58f);
                Reflection.Set(profile, "horizonReflectionStrength", 0.47f);
                Reflection.Set(profile, "causticStrength", 1.15f);
                Reflection.Set(profile, "causticScale", 3.5f);
                Reflection.Set(profile, "causticTexture", causticTexture);
                string json = EditorJsonUtility.ToJson(profile);
                EditorJsonUtility.FromJsonOverwrite(json, copy);

                Assert.That(Reflection.Get<float>(copy, "foamTextureScale"),
                    Is.EqualTo(0.055f).Within(0.00001f));
                Assert.That(Reflection.Get<float>(copy, "foamTextureContrast"),
                    Is.EqualTo(2.4f).Within(0.00001f));
                Assert.That(Reflection.Get<float>(copy, "foamBrightness"),
                    Is.EqualTo(0.62f).Within(0.00001f));
                Assert.That(Reflection.Get<float>(copy, "skyReflectionStrength"),
                    Is.EqualTo(0.9f).Within(0.00001f));
                Assert.That(Reflection.Get<float>(copy, "refractionStrength"),
                    Is.EqualTo(0.72f).Within(0.00001f));
                Assert.That(Reflection.Get<float>(copy, "refractionMaximumDistance"),
                    Is.EqualTo(24f).Within(0.00001f));
                Assert.That(Reflection.Get<float>(copy, "refractionDispersion"),
                    Is.EqualTo(0.45f).Within(0.00001f));
                Assert.That(Reflection.Get<float>(copy, "refractionMaximumScreenOffset"),
                    Is.EqualTo(0.022f).Within(0.00001f));
                Assert.That(Reflection.Get<float>(copy, "reflectionIntensity"),
                    Is.EqualTo(0.58f).Within(0.00001f));
                Assert.That(Reflection.Get<float>(copy, "horizonReflectionStrength"),
                    Is.EqualTo(0.47f).Within(0.00001f));
                Assert.That(Reflection.Get<float>(copy, "causticStrength"),
                    Is.EqualTo(1.15f).Within(0.00001f));
                Assert.That(Reflection.Get<float>(copy, "causticScale"),
                    Is.EqualTo(3.5f).Within(0.00001f));
                Assert.That(Reflection.Get<Texture2D>(copy, "causticTexture"),
                    Is.SameAs(causticTexture));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        [Test]
        public void WaterDebugMode_ExposesPhaseFourOpticsViews()
        {
            Type debugMode = Reflection.FindType("Sol.Water.Rendering.SolWaterDebugMode");
            CollectionAssert.Contains(Enum.GetNames(debugMode), "Refraction");
            CollectionAssert.Contains(Enum.GetNames(debugMode), "Caustics");
        }

        [Test]
        public void WaterQuality_SsrValidationControlsClampAndSerialize()
        {
            Type qualityType = Reflection.FindType("Sol.Water.SolWaterQualityProfile");
            ScriptableObject quality = ScriptableObject.CreateInstance(qualityType);
            ScriptableObject copy = ScriptableObject.CreateInstance(qualityType);
            try
            {
                Reflection.Set(quality, "ssrMaximumDistance", -1f);
                Reflection.Set(quality, "ssrThickness", 9f);
                Reflection.Set(quality, "ssrEdgeFade", -2f);
                Reflection.Set(quality, "ssrBinarySearchSteps", 20);
                Reflection.Set(quality, "ssrHistoryWeight", 1f);
                Reflection.Set(quality, "ssrDepthTolerance", 0f);
                Reflection.Set(quality, "ssrNormalTolerance", 4f);
                Reflection.Set(quality, "ssrMaximumLuminance", 0f);
                MethodInfo validate = qualityType.GetMethod("OnValidate",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                validate.Invoke(quality, null);

                Assert.That(Reflection.Get<float>(quality, "ssrMaximumDistance"), Is.EqualTo(1f));
                Assert.That(Reflection.Get<float>(quality, "ssrThickness"), Is.EqualTo(2f));
                Assert.That(Reflection.Get<float>(quality, "ssrEdgeFade"), Is.EqualTo(0.001f));
                Assert.That(Reflection.Get<int>(quality, "ssrBinarySearchSteps"), Is.EqualTo(8));
                Assert.That(Reflection.Get<float>(quality, "ssrHistoryWeight"), Is.EqualTo(0.98f));
                Assert.That(Reflection.Get<float>(quality, "ssrDepthTolerance"), Is.EqualTo(0.01f));
                Assert.That(Reflection.Get<float>(quality, "ssrNormalTolerance"), Is.EqualTo(1f));
                Assert.That(Reflection.Get<float>(quality, "ssrMaximumLuminance"), Is.EqualTo(0.1f));

                Reflection.Set(quality, "ssrMaximumDistance", 150f);
                Reflection.Set(quality, "ssrThickness", 0.35f);
                Reflection.Set(quality, "ssrEdgeFade", 0.06f);
                Reflection.Set(quality, "ssrBinarySearchSteps", 5);
                Reflection.Set(quality, "ssrHistoryWeight", 0.86f);
                Reflection.Set(quality, "ssrDepthTolerance", 0.2f);
                Reflection.Set(quality, "ssrNormalTolerance", 0.8f);
                Reflection.Set(quality, "ssrMaximumLuminance", 4f);
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(quality), copy);

                Assert.That(Reflection.Get<float>(copy, "ssrMaximumDistance"), Is.EqualTo(150f));
                Assert.That(Reflection.Get<float>(copy, "ssrThickness"), Is.EqualTo(0.35f));
                Assert.That(Reflection.Get<float>(copy, "ssrEdgeFade"), Is.EqualTo(0.06f));
                Assert.That(Reflection.Get<int>(copy, "ssrBinarySearchSteps"), Is.EqualTo(5));
                Assert.That(Reflection.Get<float>(copy, "ssrHistoryWeight"), Is.EqualTo(0.86f));
                Assert.That(Reflection.Get<float>(copy, "ssrDepthTolerance"), Is.EqualTo(0.2f));
                Assert.That(Reflection.Get<float>(copy, "ssrNormalTolerance"), Is.EqualTo(0.8f));
                Assert.That(Reflection.Get<float>(copy, "ssrMaximumLuminance"), Is.EqualTo(4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(quality);
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        [Test]
        public void CameraRegistry_InvalidatesWaterReflectionHistoryWhenSignatureChanges()
        {
            GameObject cameraObject = new("Water SSR history camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            Type registry = Reflection.FindType("Sol.Environment.SolEnvironmentCameraRegistry");
            MethodInfo begin = registry.GetMethod("BeginCamera", BindingFlags.Static | BindingFlags.Public);
            MethodInfo updateSignature = registry.GetMethod("UpdateWaterReflectionSignature",
                BindingFlags.Static | BindingFlags.Public);
            MethodInfo clear = registry.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public);
            try
            {
                object context = begin.Invoke(null, new object[] { camera, new Vector2Int(320, 180) });
                Assert.That((bool)updateSignature.Invoke(null, new object[] { context, 101 }), Is.True);
                Assert.That(Reflection.Get<bool>(context, "CameraCut"), Is.True);

                Reflection.Set(context, "CameraCut", false);
                Assert.That((bool)updateSignature.Invoke(null, new object[] { context, 101 }), Is.False);
                Assert.That(Reflection.Get<bool>(context, "CameraCut"), Is.False);

                Assert.That((bool)updateSignature.Invoke(null, new object[] { context, 202 }), Is.True);
                Assert.That(Reflection.Get<bool>(context, "CameraCut"), Is.True);
            }
            finally
            {
                clear.Invoke(null, null);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void OceanSpectrum_IsFiniteAndCascadeTransitionsConserveEnergy()
        {
            Type spectrum = Reflection.FindType("Sol.Water.SolOceanSpectrumMath");
            MethodInfo distribution = spectrum.GetMethod("PiersonMoskowitz",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo band = spectrum.GetMethod("CascadeBand",
                BindingFlags.Static | BindingFlags.NonPublic);

            for (float omega = 0.1f; omega <= 8f; omega += 0.1f)
            {
                float energy = (float)distribution.Invoke(null, new object[] { omega, 8f });
                Assert.That(float.IsFinite(energy), Is.True);
                Assert.That(energy, Is.GreaterThanOrEqualTo(0f));
            }

            foreach (float waveNumber in new[] { 0.05f, 0.2f, 0.9f })
            {
                float total = 0f;
                for (int cascade = 0; cascade < 4; cascade++)
                    total += (float)band.Invoke(null, new object[] { cascade, waveNumber });
                Assert.That(total, Is.EqualTo(1f).Within(0.015f),
                    $"Cascade bands should form a partition at k={waveNumber}.");
            }
        }

        [Test]
        public void OceanSpectrum_MatchesWaterFxPhaseAndLodContracts()
        {
            Type spectrum = Reflection.FindType("Sol.Water.SolOceanSpectrumMath");
            MethodInfo visibleDistance = spectrum.GetMethod("CascadeVisibleDistance",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo heightScale = spectrum.GetMethod("CascadeHeightScale",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo directional = spectrum.GetMethod("DirectionalSpreading",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo checkerboard = spectrum.GetMethod("CheckerboardSign",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo centeredIndex = spectrum.GetMethod("CenteredIndex",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That((float)visibleDistance.Invoke(null, new object[] { 0 }), Is.EqualTo(40f));
            Assert.That((float)visibleDistance.Invoke(null, new object[] { 3 }), Is.EqualTo(4800f));
            Assert.That((float)heightScale.Invoke(null, new object[] { 2 }), Is.EqualTo(0.6f));
            Assert.That((float)checkerboard.Invoke(null, new object[] { 0, 0 }), Is.EqualTo(1f));
            Assert.That((float)checkerboard.Invoke(null, new object[] { 1, 0 }), Is.EqualTo(-1f));
            Assert.That((Vector2Int)centeredIndex.Invoke(null, new object[] { 0, 0, 256 }),
                Is.EqualTo(new Vector2Int(-128, -128)));
            Assert.That((Vector2Int)centeredIndex.Invoke(null, new object[] { 128, 128, 256 }),
                Is.EqualTo(Vector2Int.zero));
            Assert.That((Vector2Int)centeredIndex.Invoke(null, new object[] { 255, 255, 256 }),
                Is.EqualTo(new Vector2Int(127, 127)));

            float calmForward = (float)directional.Invoke(null,
                new object[] { Vector2.right, Vector2.right, 2, 0f, false });
            float calmSide = (float)directional.Invoke(null,
                new object[] { Vector2.up, Vector2.right, 2, 0f, false });
            float turbulentForward = (float)directional.Invoke(null,
                new object[] { Vector2.right, Vector2.right, 2, 1f, false });
            Assert.That(calmForward, Is.GreaterThan(calmSide));
            Assert.That(turbulentForward, Is.GreaterThan(0f));
        }

        [Test]
        public void RiverGeometry_GeneratesContinuousBoundedMeshAndSamplesFlowDepth()
        {
            GameObject river = CreateSplineObject("Phase 5 river",
                new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 10f),
                new Vector3(5f, 0f, 20f));
            try
            {
                Type riverType = Reflection.FindType("Sol.Water.SolRiverGeometry");
                Component geometry = river.AddComponent(riverType);
                Reflection.Set(geometry, "width", 6f);
                Reflection.Set(geometry, "depth", 2.5f);
                Reflection.Set(geometry, "flowSpeed", 3f);
                Reflection.Set(geometry, "maximumSegmentLength", 1f);
                Assert.That((bool)riverType.GetMethod("Rebuild").Invoke(geometry, null), Is.True);

                Mesh mesh = Reflection.Get<Mesh>(geometry, "GeneratedMesh");
                Assert.That(mesh, Is.Not.Null);
                Assert.That(mesh.vertexCount, Is.GreaterThan(20));
                Assert.That(mesh.bounds.size.z, Is.GreaterThan(19f));
                Assert.That(mesh.bounds.size.x, Is.GreaterThanOrEqualTo(6f));

                object[] sampleArguments = { new Vector3(0f, -1f, 5f), null };
                Assert.That((bool)riverType.GetMethod("TrySample").Invoke(geometry, sampleArguments), Is.True);
                object sample = sampleArguments[1];
                Assert.That(Reflection.Get<float>(sample, "ChannelDepth"), Is.EqualTo(2.5f).Within(0.01f));
                Vector3 flow = Reflection.Get<Vector3>(sample, "Flow");
                Assert.That(flow.magnitude, Is.EqualTo(3f).Within(0.01f));
                Assert.That(flow.z, Is.GreaterThan(0f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(river);
            }
        }

        [Test]
        public void WaterfallGeometry_RibbonsAndPlungePoolHaveStableOrientationAndBounds()
        {
            GameObject waterfall = CreateSplineObject("Phase 5 waterfall",
                new Vector3(0f, 12f, 0f), new Vector3(0f, 6f, 1f),
                new Vector3(0f, 0f, 2f));
            try
            {
                Type waterfallType = Reflection.FindType("Sol.Water.SolWaterfallGeometry");
                Component geometry = waterfall.AddComponent(waterfallType);
                Reflection.Set(geometry, "width", 5f);
                Reflection.Set(geometry, "ribbonCount", 4);
                Reflection.Set(geometry, "plungePoolRadius", 3f);
                Assert.That((bool)waterfallType.GetMethod("Rebuild").Invoke(geometry, null), Is.True);

                Mesh mesh = Reflection.Get<Mesh>(geometry, "GeneratedMesh");
                Assert.That(mesh.bounds.size.y, Is.GreaterThan(11f));
                Assert.That(mesh.bounds.size.x, Is.GreaterThanOrEqualTo(5f));
                Vector3[] normals = mesh.normals;
                Assert.That(normals.Length, Is.EqualTo(mesh.vertexCount));
                Assert.That(Array.Exists(normals, normal => Mathf.Abs(normal.z) > 0.5f), Is.True,
                    "The falling sheet must retain a non-upward ribbon normal.");

                object[] impactArguments = { new Vector3(0f, -0.5f, 2f), null };
                Assert.That((bool)waterfallType.GetMethod("TrySample").Invoke(geometry, impactArguments), Is.True);
                Assert.That(Reflection.Get<float>(impactArguments[1], "Foam"), Is.GreaterThan(0.5f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(waterfall);
            }
        }

        [Test]
        public void LakeGeometry_ClosedSplineTriangulatesAndUsesSharedGeometryContract()
        {
            GameObject lake = CreateSplineObject("Phase 5 lake",
                new Vector3(-5f, 0f, -4f), new Vector3(-5f, 0f, 4f),
                new Vector3(5f, 0f, 4f), new Vector3(5f, 0f, -4f));
            try
            {
                object spline = GetSpline(lake);
                spline.GetType().GetProperty("Closed").SetValue(spline, true);
                Type lakeType = Reflection.FindType("Sol.Water.SolLakeGeometry");
                Component geometry = lake.AddComponent(lakeType);
                Reflection.Set(geometry, "depth", 7f);
                Assert.That((bool)lakeType.GetMethod("Rebuild").Invoke(geometry, null), Is.True);
                Assert.That(Reflection.Get<Mesh>(geometry, "GeneratedMesh").triangles.Length,
                    Is.GreaterThanOrEqualTo(6));
                Assert.That((bool)lakeType.GetMethod("ContainsPoint").Invoke(geometry,
                    new object[] { Vector3.zero }), Is.True);
                Assert.That((bool)lakeType.GetMethod("ContainsPoint").Invoke(geometry,
                    new object[] { new Vector3(20f, 0f, 20f) }), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(lake);
            }
        }

        [Test]
        public void FiniteGeometry_QueryRemainsContinuousAcrossOriginShift()
        {
            GameObject river = CreateSplineObject("Origin-shift river",
                new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 20f));
            try
            {
                Type riverType = Reflection.FindType("Sol.Water.SolRiverGeometry");
                Component geometry = river.AddComponent(riverType);
                riverType.GetMethod("Rebuild").Invoke(geometry, null);
                object[] beforeArguments = { new Vector3(0f, 0f, 10f), null };
                Assert.That((bool)riverType.GetMethod("TrySample").Invoke(geometry, beforeArguments), Is.True);
                Vector3 before = Reflection.Get<Vector3>(beforeArguments[1], "Position");

                Vector3 shift = new(1024f, 0f, -512f);
                Component body = river.GetComponent(Reflection.FindType("Sol.Water.SolWaterBody"));
                Type originType = Reflection.FindType("Sol.Environment.SolDouble3");
                body.GetType().GetMethod("OnSolOriginShift").Invoke(body,
                    new[] { (object)shift, Activator.CreateInstance(originType, 1024d, 0d, -512d) });
                object[] afterArguments = { new Vector3(-1024f, 0f, 522f), null };
                Assert.That((bool)riverType.GetMethod("TrySample").Invoke(geometry, afterArguments), Is.True);
                Vector3 after = Reflection.Get<Vector3>(afterArguments[1], "Position");
                Assert.That(Vector3.Distance(before - shift, after), Is.LessThan(0.001f));
                Assert.That(Reflection.Get<Vector3>(beforeArguments[1], "Flow"),
                    Is.EqualTo(Reflection.Get<Vector3>(afterArguments[1], "Flow")));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(river);
            }
        }

        [Test]
        public void WaterBodyRegistry_UnregistersGeometryWhenAdditiveCellCloses()
        {
            GameObject worldObject = null;
            Type worldType = Reflection.FindType("Sol.Water.SolWaterWorld");
            Component world = (Component)worldType.GetProperty("Active",
                BindingFlags.Static | BindingFlags.Public).GetValue(null);
            UnityEngine.SceneManagement.Scene cell = default;
            string hostScenePath = null;
            try
            {
                if (world == null)
                {
                    worldObject = new GameObject("Phase 5 registry world");
                    world = worldObject.AddComponent(worldType);
                }
                object bodies = world.GetType().GetProperty("Bodies").GetValue(world);
                int baseline = (int)bodies.GetType().GetProperty("Count").GetValue(bodies);
                // Unity refuses to add a scene additively while the active scene is
                // untitled, which is exactly the state a batch-mode run starts in. Give
                // the host scene a path for the duration of the test; the asset is
                // removed again in the finally block.
                if (string.IsNullOrEmpty(
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().path))
                {
                    hostScenePath = "Assets/__SolWaterRegistryHost.unity";
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(
                        UnityEngine.SceneManagement.SceneManager.GetActiveScene(),
                        hostScenePath);
                }
                cell = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Additive);
                GameObject river = CreateSplineObject("Streamed river",
                    Vector3.zero, Vector3.forward * 10f);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(river, cell);
                river.AddComponent(Reflection.FindType("Sol.Water.SolRiverGeometry"));

                int registered = (int)bodies.GetType().GetProperty("Count").GetValue(bodies);
                Assert.That(registered, Is.EqualTo(baseline + 1));

                Assert.That(UnityEditor.SceneManagement.EditorSceneManager.CloseScene(cell, true), Is.True);
                int remaining = (int)bodies.GetType().GetProperty("Count").GetValue(bodies);
                Assert.That(remaining, Is.EqualTo(baseline));
                cell = default;
            }
            finally
            {
                if (cell.IsValid())
                    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(cell, true);
                if (worldObject != null)
                    UnityEngine.Object.DestroyImmediate(worldObject);
                if (!string.IsNullOrEmpty(hostScenePath))
                {
                    UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                        UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                        UnityEditor.SceneManagement.NewSceneMode.Single);
                    AssetDatabase.DeleteAsset(hostScenePath);
                }
            }
        }

        static GameObject CreateSplineObject(string name, params Vector3[] knots)
        {
            GameObject gameObject = new(name);
            Type containerType = Reflection.FindType("UnityEngine.Splines.SplineContainer");
            Component container = gameObject.AddComponent(containerType);
            object spline = containerType.GetProperty("Spline").GetValue(container);
            Type float3Type = Reflection.FindType("Unity.Mathematics.float3");
            Type tangentModeType = Reflection.FindType("UnityEngine.Splines.TangentMode");
            MethodInfo add = spline.GetType().GetMethod("Add", new[] { float3Type, tangentModeType });
            object automatic = Enum.Parse(tangentModeType, "AutoSmooth");
            for (int i = 0; i < knots.Length; i++)
            {
                Vector3 point = knots[i];
                object float3 = Activator.CreateInstance(float3Type, point.x, point.y, point.z);
                add.Invoke(spline, new[] { float3, automatic });
            }
            return gameObject;
        }

        [Test]
        public void WaterOptics_AbsorptionFallsOffMonotonicallyAndKeepsABlueTail()
        {
            Type optics = Reflection.FindType("Sol.Water.SolWaterOpticsMath");
            MethodInfo normalize = optics.GetMethod("NormalizeAbsorption");
            MethodInfo compute = optics.GetMethod("ComputeAbsorption");

            // The shipped Ocean Profile coefficients.
            Vector3 absorptionColor = (Vector3)normalize.Invoke(null,
                new object[] { new Vector3(0.14f, 0.055f, 0.028f) });
            Assert.That(absorptionColor.x, Is.EqualTo(1f).Within(1e-4f),
                "Normalization should pin the red channel to one.");
            Assert.That(absorptionColor.z, Is.LessThan(absorptionColor.y),
                "Blue must be absorbed more slowly than green.");

            Vector4 previous = (Vector4)compute.Invoke(null,
                new object[] { 18f, absorptionColor, 0f });
            foreach (float rayLength in new[] { 0.5f, 1f, 2f, 5f, 10f, 25f, 100f, 5000f })
            {
                Vector4 current = (Vector4)compute.Invoke(null,
                    new object[] { 18f, absorptionColor, rayLength });

                Assert.That(float.IsFinite(current.x) && float.IsFinite(current.y)
                    && float.IsFinite(current.z) && float.IsFinite(current.w), Is.True,
                    $"Absorption must stay finite at {rayLength} m.");
                Assert.That(current.x, Is.LessThanOrEqualTo(previous.x + 1e-5f),
                    $"Transmittance must not increase with depth at {rayLength} m.");
                Assert.That(current.w, Is.GreaterThanOrEqualTo(previous.w - 1e-5f),
                    $"Extinction must not decrease with depth at {rayLength} m.");
                Assert.That(current.x, Is.LessThanOrEqualTo(current.z + 1e-5f),
                    $"Red must never outlast blue at {rayLength} m.");
                previous = current;
            }

            // Floors, so deep water stays blue instead of collapsing to black.
            Assert.That(previous.x, Is.EqualTo(0.0005f).Within(1e-5f));
            Assert.That(previous.z, Is.EqualTo(0.025f).Within(1e-5f));
        }

        [Test]
        public void WaterOptics_OpenWaterDoesNotTransmitTheSkyAndPlateaus()
        {
            // Regression guard for the defect where refractionMaximumDistance also
            // bounded the optical path, capping open ocean at a few metres of
            // absorption so the sky behind the surface was transmitted through.
            Type optics = Reflection.FindType("Sol.Water.SolWaterOpticsMath");
            MethodInfo compute = optics.GetMethod("ComputeAbsorption");
            float maximumRayLength = (float)optics.GetField("MaximumRayLength")
                .GetValue(null);

            Vector4 openWater = (Vector4)compute.Invoke(null,
                new object[] { 18f, Vector3.one, maximumRayLength });

            Assert.That(openWater.x, Is.LessThan(0.01f),
                "Open water must not transmit the sky through the surface.");
            Assert.That(openWater.y, Is.LessThan(0.01f));

            // Past min(MaximumClarity, clarityDistance) the extinction integral is
            // saturated, so the look no longer depends on how far away the sea floor is.
            Vector4 atClarity = (Vector4)compute.Invoke(null,
                new object[] { 18f, Vector3.one, 18f });
            Assert.That(openWater.w, Is.EqualTo(atClarity.w).Within(1e-4f),
                "Extinction must plateau once the clarity distance is exceeded.");
            Assert.That(openWater.w, Is.GreaterThan(0.25f),
                "Volume scattering must dominate the surface in open water.");
        }

        [Test]
        public void WaterOptics_ClearerWaterScattersLessInOpenOcean()
        {
            // Documents a coupling that matters when authoring: because the extinction
            // integral coefficient grows quadratically with clarity while the sampled
            // path only grows linearly, raising clarityDistance makes deep open water
            // darker, not brighter. Deep clear ocean reads near-black by design.
            Type optics = Reflection.FindType("Sol.Water.SolWaterOpticsMath");
            MethodInfo compute = optics.GetMethod("ComputeAbsorption");
            float maximumRayLength = (float)optics.GetField("MaximumRayLength")
                .GetValue(null);

            float turbid = ((Vector4)compute.Invoke(null,
                new object[] { 10f, Vector3.one, maximumRayLength })).w;
            float shipped = ((Vector4)compute.Invoke(null,
                new object[] { 18f, Vector3.one, maximumRayLength })).w;
            float clear = ((Vector4)compute.Invoke(null,
                new object[] { 30f, Vector3.one, maximumRayLength })).w;

            Assert.That(turbid, Is.GreaterThan(shipped));
            Assert.That(shipped, Is.GreaterThan(clear));
            Assert.That(clear, Is.GreaterThan(0f));
        }

        [Test]
        public void QualityTiers_DegradeMonotonicallyAndLowDisablesTheSpectrum()
        {
            Type qualityType = Reflection.FindType("Sol.Water.SolWaterQualityProfile");
            Type tierType = Reflection.FindType("Sol.Water.SolWaterQualityTier");
            ScriptableObject quality = ScriptableObject.CreateInstance(qualityType);
            try
            {
                PropertyInfo resolution = qualityType.GetProperty("FftResolution");
                PropertyInfo cascades = qualityType.GetProperty("FftCascadeCount");

                int previousResolution = -1;
                int previousCascades = -1;
                foreach (string tierName in new[] { "Low", "Medium", "High" })
                {
                    Reflection.Set(quality, "tier", Enum.Parse(tierType, tierName));
                    int currentResolution = (int)resolution.GetValue(quality);
                    int currentCascades = (int)cascades.GetValue(quality);

                    Assert.That(currentResolution, Is.GreaterThanOrEqualTo(previousResolution),
                        $"FFT resolution must not fall as tier rises ({tierName}).");
                    Assert.That(currentCascades, Is.GreaterThanOrEqualTo(previousCascades),
                        $"Cascade count must not fall as tier rises ({tierName}).");
                    previousResolution = currentResolution;
                    previousCascades = currentCascades;
                }

                // Low must genuinely disable the spectrum rather than run a small one:
                // the renderer keys the whole FFT chain and the pixel normal path off a
                // non-zero cascade count, and Gerstner is the Low-tier fallback.
                Reflection.Set(quality, "tier", Enum.Parse(tierType, "Low"));
                Assert.That((int)cascades.GetValue(quality), Is.EqualTo(0));
                Assert.That((int)resolution.GetValue(quality), Is.EqualTo(0));

                // Every feature toggle must survive validation at every tier, so a
                // degraded tier cannot silently re-enable something it disabled.
                MethodInfo validate = qualityType.GetMethod("OnValidate",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (string tierName in new[] { "Low", "Medium", "High" })
                {
                    Reflection.Set(quality, "tier", Enum.Parse(tierType, tierName));
                    Reflection.Set(quality, "volumetricSteps", 999);
                    Reflection.Set(quality, "volumetricResolutionScale", 4f);
                    validate.Invoke(quality, null);
                    Assert.That(Reflection.Get<int>(quality, "volumetricSteps"),
                        Is.InRange(4, 32), $"volumetricSteps unclamped at {tierName}.");
                    Assert.That(Reflection.Get<float>(quality, "volumetricResolutionScale"),
                        Is.InRange(0.1f, 1f),
                        $"volumetricResolutionScale unclamped at {tierName}.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(quality);
            }
        }

        [Test]
        public void FiniteBodies_ReceiveTheSameDebugModeAsTheOcean()
        {
            // The finite path hardcoded the debug mode to zero while the ocean passed the
            // real one, so selecting a debug view silently showed final colour on rivers,
            // lakes, waterfalls and flood extents. Both paths must now agree.
            Type drawSetType = Reflection.FindType("Sol.Water.Rendering.SolFiniteWaterDrawSet");
            MethodInfo build = drawSetType.GetMethod("Build",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(build, Is.Not.Null);

            ParameterInfo[] parameters = build.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(3),
                "Build must take the debug mode so finite bodies can honour it.");
            Assert.That(parameters[2].ParameterType.Name, Is.EqualTo("SolWaterDebugMode"));

            MethodInfo fill = drawSetType.GetMethod("FillProperties",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(fill, Is.Not.Null);
            Assert.That(fill.GetParameters()[^1].ParameterType.Name,
                Is.EqualTo("SolWaterDebugMode"),
                "FillProperties must receive the debug mode rather than assume zero.");
        }

        [Test]
        public void HydrologyCellSeed_RestoresStreamedCellsWithoutCreatingOrDestroyingWater()
        {
            Type assetType = Reflection.FindType("Sol.Hydrology.SolHydrologyAsset");
            Type nodeType = Reflection.FindType("Sol.Hydrology.SolHydrologyNode");
            Type edgeType = Reflection.FindType("Sol.Hydrology.SolHydrologyEdge");
            Type cellType = Reflection.FindType("Sol.Streaming.SolEnvironmentCellId");

            ScriptableObject asset = ScriptableObject.CreateInstance(assetType);
            object streamedCell = Activator.CreateInstance(cellType, "cell-north");

            Array nodes = Array.CreateInstance(nodeType, 2);
            object streamed = CreateNode(nodeType, "streamed", 40d, 0d, 100d, 0d);
            Reflection.Set(streamed, "cellId", streamedCell);
            object resident = CreateNode(nodeType, "resident", 60d, 0d, 100d, 0d);
            nodes.SetValue(streamed, 0);
            nodes.SetValue(resident, 1);
            Reflection.Set(asset, "nodes", nodes);
            Reflection.Set(asset, "edges", Array.CreateInstance(edgeType, 0));

            GameObject root = new("Hydrology cell handoff");
            root.SetActive(false);
            try
            {
                Component world = Reflection.Add(root, "Sol.Hydrology.SolHydrologyWorld");
                Reflection.Set(world, "asset", asset);
                root.SetActive(true);
                // SolHydrologyWorld is not [ExecuteAlways], so Awake never fires in
                // EditMode and the node arrays stay empty until Initialize is called.
                Reflection.Invoke(world, "Initialize");

                MethodInfo capture = world.GetType().GetMethod("CaptureCellSeed");
                MethodInfo restore = world.GetType().GetMethod("RestoreCellSeed");
                MethodInfo totalVolume = world.GetType().GetMethod("TotalVolume");

                double before = (double)totalVolume.Invoke(world, null);
                Assert.That(before, Is.EqualTo(100d).Within(1e-6));

                // Unloading the cell must record its state, not discard it.
                Assert.That((bool)capture.Invoke(world, new[] { streamedCell }), Is.True);
                Assert.That((double)totalVolume.Invoke(world, null),
                    Is.EqualTo(before).Within(1e-6),
                    "Capturing a cell seed must not change stored water.");

                // Simulate the cell being away and its node drifting, as a re-init would.
                // ApplyCommand takes an 'in' parameter, so resolve it directly rather
                // than through the helper, which matches on an empty signature.
                Type commandType = Reflection.FindType("Sol.Hydrology.SolHydrologyCommand");
                object setVolume = Activator.CreateInstance(commandType,
                    Enum.Parse(Reflection.FindType("Sol.Hydrology.SolHydrologyCommandType"),
                        "SetVolume"),
                    "streamed", 5d, 0ul);
                MethodInfo applyCommand = world.GetType().GetMethod("ApplyCommand");
                Assert.That((bool)applyCommand.Invoke(world, new[] { setVolume }), Is.True);
                Assert.That((double)totalVolume.Invoke(world, null),
                    Is.EqualTo(65d).Within(1e-6));

                // Reloading restores exactly what was there, so the round trip is
                // mass-neutral rather than silently creating or destroying water.
                Assert.That((bool)restore.Invoke(world, new[] { streamedCell }), Is.True);
                Assert.That((double)totalVolume.Invoke(world, null),
                    Is.EqualTo(before).Within(1e-6),
                    "An unload/reload round trip must conserve mass.");
                Assert.That(GetVolume(world, "streamed"), Is.EqualTo(40d).Within(1e-6));
                Assert.That(GetVolume(world, "resident"), Is.EqualTo(60d).Within(1e-6),
                    "Restoring one cell must not disturb resident nodes.");

                // A cell that was never captured has no seed, so a first load correctly
                // keeps whatever the asset authored.
                object unknownCell = Activator.CreateInstance(cellType, "cell-south");
                Assert.That((bool)restore.Invoke(world, new[] { unknownCell }), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void FloodExtent_CoversTerrainBelowTheWaterLevelAndFadesAtTheEdge()
        {
            Type math = Reflection.FindType("Sol.Hydrology.SolFloodExtentMath");
            MethodInfo submergence = math.GetMethod("Submergence");
            MethodInfo cellIsFlooded = math.GetMethod("CellIsFlooded");
            MethodInfo edgeFade = math.GetMethod("EdgeFade");
            MethodInfo shouldRebuild = math.GetMethod("ShouldRebuild");
            MethodInfo floodedArea = math.GetMethod("FloodedArea");

            Assert.That((float)submergence.Invoke(null, new object[] { 2f, 5f }),
                Is.EqualTo(3f).Within(1e-4f));
            Assert.That((float)submergence.Invoke(null, new object[] { 7f, 5f }),
                Is.LessThan(0f), "Terrain above the level is dry.");

            // A cell counts as flooded if any corner is under, so the boundary cell is
            // kept rather than dropped the moment one corner pokes out.
            Assert.That((bool)cellIsFlooded.Invoke(null,
                new object[] { 9f, 9f, 9f, 1f, 5f }), Is.True);
            Assert.That((bool)cellIsFlooded.Invoke(null,
                new object[] { 9f, 9f, 9f, 9f, 5f }), Is.False);

            // Edge fade ramps with depth so the shoreline is not pinned to the grid.
            Assert.That((float)edgeFade.Invoke(null, new object[] { 5f, 5f, 0.5f }),
                Is.EqualTo(0f).Within(1e-4f));
            Assert.That((float)edgeFade.Invoke(null, new object[] { 4.75f, 5f, 0.5f }),
                Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That((float)edgeFade.Invoke(null, new object[] { 1f, 5f, 0.5f }),
                Is.EqualTo(1f).Within(1e-4f));

            // Hysteresis: a continuously simulating node must not rebuild every frame.
            Assert.That((bool)shouldRebuild.Invoke(null, new object[] { 5f, 5.001f, 0.05f }),
                Is.False);
            Assert.That((bool)shouldRebuild.Invoke(null, new object[] { 5f, 5.2f, 0.05f }),
                Is.True);
            Assert.That((bool)shouldRebuild.Invoke(null,
                new object[] { float.NaN, 5f, 0.05f }), Is.True,
                "An unbuilt extent must always build.");

            Assert.That((double)floodedArea.Invoke(null, new object[] { 25, 2f }),
                Is.EqualTo(100d).Within(1e-6));
        }

        [Test]
        public void FloodGeometry_ExtentGrowsWithLevelOverASlopedTerrain()
        {
            GameObject host = new("Flood extent probe");
            host.SetActive(false);
            try
            {
                Type floodType = Reflection.FindType("Sol.Hydrology.SolFloodGeometry");
                Component flood = host.AddComponent(floodType);

                // A ramp rising one metre per column. Raising the level must flood
                // strictly more of it, and the covered area must be monotonic.
                const int columns = 16;
                const int rows = 4;
                float[] heights = new float[columns * rows];
                for (int row = 0; row < rows; row++)
                    for (int column = 0; column < columns; column++)
                        heights[row * columns + column] = column;

                MethodInfo inject = floodType.GetMethod("SetTerrainHeightsForTesting",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo rebuild = floodType.GetMethod("Rebuild");
                PropertyInfo area = floodType.GetProperty("FloodedArea");

                double previousArea = -1d;
                foreach (float level in new[] { 1f, 4f, 8f, 12f })
                {
                    Reflection.Set(flood, "fallbackLevel", level);
                    inject.Invoke(flood, new object[] { heights, columns, rows });
                    rebuild.Invoke(flood, null);

                    double current = (double)area.GetValue(flood);
                    Assert.That(current, Is.GreaterThan(previousArea),
                        $"Flooded area must grow with level (level {level}).");
                    previousArea = current;
                }

                // Dropping the level below all terrain must leave nothing flooded.
                Reflection.Set(flood, "fallbackLevel", -10f);
                inject.Invoke(flood, new object[] { heights, columns, rows });
                Assert.That((bool)rebuild.Invoke(flood, null), Is.False,
                    "A level below every sample should flood nothing.");
                Assert.That((double)area.GetValue(flood), Is.EqualTo(0d).Within(1e-6));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void DisplacementInversion_FindsTheSurfacePointAboveTheQuery()
        {
            Type inversion = Reflection.FindType("Sol.Water.SolWaterDisplacementInversion");
            MethodInfo solve = inversion.GetMethod("Solve");
            MethodInfo residual = inversion.GetMethod("ResidualError");
            Type delegateType = inversion.GetNestedType("HorizontalDisplacement");
            int iterations = (int)inversion.GetField("DefaultIterations").GetValue(null);

            // Choppiness 1.0 over a 20 m wavelength is steepness 0.31, which is a
            // realistically choppy sea and well inside what the shipped profiles ask for.
            const float wavelength = 20f;
            const float choppiness = 1f;
            float k = 2f * Mathf.PI / wavelength;
            Func<Vector2, Vector2> field = p =>
                new Vector2(choppiness * Mathf.Sin(p.x * k), 0f);
            Delegate displacement = Delegate.CreateDelegate(delegateType, field.Target,
                field.Method);

            foreach (float target in new[] { 0f, 2.5f, 5f, 7.5f, 11f, 17f, -6f })
            {
                Vector2 targetXZ = new(target, 0f);
                Vector2 solved = (Vector2)solve.Invoke(null,
                    new object[] { targetXZ, displacement, iterations });

                // The whole point: solved + displacement(solved) must land on target.
                Vector2 landed = solved + field(solved);
                Assert.That(landed.x, Is.EqualTo(target).Within(0.01f),
                    $"Inversion did not converge for target {target}.");
                Assert.That((float)residual.Invoke(null,
                    new object[] { targetXZ, solved, displacement }),
                    Is.LessThan(0.01f));

                // A naive lookup evaluates at the query directly. Where displacement is
                // non-zero that is measurably wrong, which is the defect being fixed.
                if (Mathf.Abs(field(targetXZ).x) > 0.1f)
                    Assert.That(Mathf.Abs(solved.x - target), Is.GreaterThan(0.05f),
                        "Inversion should differ from the naive lookup under chop.");
            }
        }

        [Test]
        public void DisplacementInversion_ReportsResidualAsWavesApproachBreaking()
        {
            // The fixed point contracts by roughly the wave steepness per step, so it is
            // fast on ordinary water and slow as a wave nears breaking. This documents
            // where the accuracy actually sits rather than assuming it is exact, and
            // pins that the solver reports its own error instead of lying about it.
            Type inversion = Reflection.FindType("Sol.Water.SolWaterDisplacementInversion");
            MethodInfo solve = inversion.GetMethod("Solve");
            MethodInfo residual = inversion.GetMethod("ResidualError");
            Type delegateType = inversion.GetNestedType("HorizontalDisplacement");
            int iterations = (int)inversion.GetField("DefaultIterations").GetValue(null);

            const float wavelength = 20f;
            float k = 2f * Mathf.PI / wavelength;

            float WorstResidual(float choppiness)
            {
                Func<Vector2, Vector2> field = p =>
                    new Vector2(choppiness * Mathf.Sin(p.x * k), 0f);
                Delegate displacement = Delegate.CreateDelegate(delegateType,
                    field.Target, field.Method);
                float worst = 0f;
                for (int i = 0; i <= 16; i++)
                {
                    Vector2 targetXZ = new(i * wavelength / 16f, 0f);
                    Vector2 solved = (Vector2)solve.Invoke(null,
                        new object[] { targetXZ, displacement, iterations });
                    worst = Mathf.Max(worst, (float)residual.Invoke(null,
                        new object[] { targetXZ, solved, displacement }));
                }
                return worst;
            }

            // Everything up to a steep but unbroken sea resolves to well under a centimetre.
            Assert.That(WorstResidual(0.4f), Is.LessThan(0.01f));
            Assert.That(WorstResidual(1.0f), Is.LessThan(0.01f));
            Assert.That(WorstResidual(1.5f), Is.LessThan(0.01f));

            // Beyond that the solve degrades, and must say so rather than pretend.
            float breaking = WorstResidual(2.2f);
            Assert.That(breaking, Is.GreaterThan(0.01f),
                "Near-breaking chop is expected to leave residual error.");
            Assert.That(breaking, Is.LessThan(0.1f),
                "Residual should stay bounded even near breaking.");
        }

        [Test]
        public void DisplacementInversion_DegradesConfidenceWhenItCannotConverge()
        {
            Type inversion = Reflection.FindType("Sol.Water.SolWaterDisplacementInversion");
            MethodInfo confidence = inversion.GetMethod("ConfidenceFromResidual");

            Assert.That((float)confidence.Invoke(null, new object[] { 0f }),
                Is.EqualTo(1f).Within(1e-4f), "A converged solve is fully trusted.");
            Assert.That((float)confidence.Invoke(null, new object[] { 0.25f }),
                Is.EqualTo(0.75f).Within(1e-4f));
            Assert.That((float)confidence.Invoke(null, new object[] { 5f }),
                Is.EqualTo(0f).Within(1e-4f),
                "A metre of unresolved horizontal error is not a usable sample.");
        }

        [Test]
        public void WeatherWind_ConvertsToMetresPerSecondBeforeDrivingTheSpectrum()
        {
            // WeatherProfile authors windStrength as a 0-3 normalised multiplier, but the
            // ocean spectrum derives its Pierson-Moskowitz peak frequency from a real
            // speed in m/s. Passing the multiplier through unconverted put every wave
            // shorter than two metres and the ocean rendered flat, independent of
            // spectralStrength. This pins the unit contract.
            GameObject host = new("Wind conversion probe");
            host.SetActive(false);
            try
            {
                Component world = Reflection.Add(host, "Sol.Environment.SolEnvironmentWorld");
                float conversion = Reflection.Get<float>(world, "windStrengthToMetresPerSecond");
                Assert.That(conversion, Is.GreaterThan(1f),
                    "Weather wind strength must be scaled into metres per second.");

                // Peak wavelength the spectrum resolves for a nominal windStrength of 1.
                const float gravity = 9.81f;
                float windSpeed = Mathf.Max(1.5f, 1f * conversion);
                float peakOmega = 0.87f * gravity / windSpeed;
                float peakWaveNumber = peakOmega * peakOmega / gravity;
                float peakWavelength = 2f * Mathf.PI / peakWaveNumber;

                Assert.That(peakWavelength, Is.GreaterThan(10f),
                    $"Nominal weather wind produced a {peakWavelength:F1} m peak "
                    + "wavelength; that is ripples, not swell.");
                Assert.That(peakWavelength, Is.LessThan(400f),
                    "Nominal weather wind produced implausibly long swell.");

                // Crest breaking is gated on smoothstep(3, 6, windSpeed), so a nominal
                // wind must clear that or the ocean can never generate foam.
                Assert.That(windSpeed, Is.GreaterThan(3f),
                    "Nominal weather wind cannot produce any crest foam.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void WaterOptics_ScatteringTracksSunElevation()
        {
            Type optics = Reflection.FindType("Sol.Water.SolWaterOpticsMath");
            MethodInfo scattering = optics.GetMethod("VolumeScattering");
            Color turbidity = new(0.035f, 0.32f, 0.36f, 1f);
            Color ambient = new(0.1f, 0.14f, 0.2f, 1f);
            Color sun = Color.white;

            Vector3 noon = (Vector3)scattering.Invoke(null,
                new object[] { turbidity, ambient, sun, Vector3.up, 1f, 1f });
            Vector3 horizon = (Vector3)scattering.Invoke(null,
                new object[] { turbidity, ambient, sun, Vector3.right, 1f, 1f });
            Vector3 night = (Vector3)scattering.Invoke(null,
                new object[] { turbidity, ambient, sun, Vector3.down, 1f, 1f });

            Assert.That(noon.y, Is.GreaterThan(horizon.y),
                "Scattering must be brighter with the sun overhead.");
            Assert.That(horizon.y, Is.GreaterThan(night.y),
                "Scattering must dim as the sun sets.");
            Assert.That(night.y, Is.GreaterThan(0f),
                "Ambient sky must still light the volume at night.");
            Assert.That(noon.z, Is.GreaterThan(noon.x),
                "Turbidity hue must survive the lighting.");

            // Shadowing attenuates the direct term but never the ambient term.
            Vector3 shadowed = (Vector3)scattering.Invoke(null,
                new object[] { turbidity, ambient, sun, Vector3.up, 0f, 1f });
            Assert.That(shadowed.y, Is.LessThan(noon.y));
            Assert.That(shadowed.y, Is.GreaterThan(0f));
        }

        static object GetSpline(GameObject gameObject)
        {
            Type containerType = Reflection.FindType("UnityEngine.Splines.SplineContainer");
            Component container = gameObject.GetComponent(containerType);
            return containerType.GetProperty("Spline").GetValue(container);
        }

        static object CreateNode(Type nodeType, string id, double volume, double minimum, double maximum, double datum)
        {
            object node = Activator.CreateInstance(nodeType);
            Reflection.Set(node, "id", id);
            Reflection.Set(node, "surfaceArea", 10d);
            Reflection.Set(node, "initialVolume", volume);
            Reflection.Set(node, "minimumVolume", minimum);
            Reflection.Set(node, "maximumVolume", maximum);
            Reflection.Set(node, "datumLevel", datum);
            return node;
        }

        static double GetVolume(object world, string id)
        {
            MethodInfo method = world.GetType().GetMethod("TryGetNodeState");
            object[] arguments = { id, null };
            Assert.That((bool)method.Invoke(world, arguments), Is.True);
            return Reflection.Get<double>(arguments[1], "Volume");
        }

        [Test]
        public void FftReadback_CascadeMirrorMatchesTheComputeShaderAndRoundTrips()
        {
            Type readback = Reflection.FindType("Sol.Water.SolWaterFftReadback");
            MethodInfo cascadeSize = readback.GetMethod("CascadeSize",
                BindingFlags.Static | BindingFlags.Public);
            MethodInfo rotate = readback.GetMethod("RotateCascade",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo unrotate = readback.GetMethod("UnrotateCascade",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo cascadeUv = readback.GetMethod("CascadeUv",
                BindingFlags.Static | BindingFlags.NonPublic);

            // The CPU mirror only reports the surface the GPU actually simulated if it
            // reads the same domain per cascade. Assert against the compute shader's own
            // source rather than a copied literal: changing the cascade partition is a
            // live proposal, and a hand-copied constant would drift silently.
            string computePath =
                "Assets/Sky-and-Water/Shaders/Water2/SolWaterFFT.compute";
            string compute = System.IO.File.ReadAllText(computePath);
            int bodyStart = compute.IndexOf("float CascadeSize(uint cascade)",
                StringComparison.Ordinal);
            Assert.That(bodyStart, Is.GreaterThanOrEqualTo(0),
                "CascadeSize was renamed in SolWaterFFT.compute; update this mirror test.");
            int bodyEnd = compute.IndexOf('}', bodyStart);
            string body = compute[bodyStart..bodyEnd];
            // The cascade indices in the ternary are written bare ("cascade == 1") while
            // the domain sizes carry a decimal point ("128.0"), which separates them
            // without depending on how many branches the chain has.
            System.Text.RegularExpressions.MatchCollection literals =
                System.Text.RegularExpressions.Regex.Matches(body, @"\d+\.\d+");
            Assert.That(literals.Count, Is.EqualTo(4),
                "Expected four decimal cascade sizes in SolWaterFFT.compute CascadeSize; "
                + "write them as 32.0 rather than 32 so this mirror check can read them.");
            float[] shaderSizes = new float[4];
            for (int cascade = 0; cascade < 4; cascade++)
                shaderSizes[cascade] = float.Parse(
                    literals[cascade].Value,
                    System.Globalization.CultureInfo.InvariantCulture);

            for (int cascade = 0; cascade < 4; cascade++)
                Assert.That((float)cascadeSize.Invoke(null, new object[] { cascade }),
                    Is.EqualTo(shaderSizes[cascade]),
                    $"CPU cascade {cascade} domain disagrees with SolWaterFFT.compute.");

            // Rotation is applied when sampling and undone on the returned displacement.
            // If the two ever disagree the mirror reports displacement in a rotated frame,
            // which reads as a boat drifting sideways against the visible waves.
            for (int cascade = 0; cascade < 4; cascade++)
            {
                Vector2 value = new(3.7f, -12.25f);
                Vector2 roundTripped = (Vector2)unrotate.Invoke(null,
                    new object[] { rotate.Invoke(null, new object[] { value, cascade }), cascade });
                Assert.That(roundTripped.x, Is.EqualTo(value.x).Within(0.0005f));
                Assert.That(roundTripped.y, Is.EqualTo(value.y).Within(0.0005f));
            }

            // Cascade UVs wrap, so a query far from the origin must still land in range.
            foreach (Vector2 position in new[]
                { Vector2.zero, new Vector2(1234.5f, -9876.5f), new Vector2(-4e5f, 3e5f) })
            {
                for (int cascade = 0; cascade < 4; cascade++)
                {
                    Vector2 uv = (Vector2)cascadeUv.Invoke(null, new object[]
                        { position, (float)cascadeSize.Invoke(null, new object[] { cascade }), cascade });
                    Assert.That(uv.x, Is.InRange(0f, 1f));
                    Assert.That(uv.y, Is.InRange(0f, 1f));
                }
            }
        }
    }
}
