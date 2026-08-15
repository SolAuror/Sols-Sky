using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Sol.Tests.Editor
{
    public sealed class Water2EditorTests
    {
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

                Vector4 authoredMapping = new(12f, -8f, 640f, 320f);
                Reflection.Set(profile, "shorelineDataMapping", authoredMapping);
                Reflection.Set(profile, "shorelineDepthRange", 24f);
                Reflection.Set(profile, "shorelineDistanceRange", 70f);
                Reflection.Set(profile, "shallowWaveAttenuationDepth", 5.5f);
                Reflection.Set(profile, "shorelineContactFade", 0.42f);
                Reflection.Set(profile, "shorelineNormalFlattening", 0.76f);
                string json = EditorJsonUtility.ToJson(profile);
                EditorJsonUtility.FromJsonOverwrite(json, copy);

                Assert.That(Reflection.Get<Vector4>(copy, "shorelineDataMapping"),
                    Is.EqualTo(authoredMapping));
                Assert.That(Reflection.Get<float>(copy, "shorelineDepthRange"), Is.EqualTo(24f));
                Assert.That(Reflection.Get<float>(copy, "shorelineDistanceRange"), Is.EqualTo(70f));
                Assert.That(Reflection.Get<float>(copy, "shallowWaveAttenuationDepth"), Is.EqualTo(5.5f));
                Assert.That(Reflection.Get<float>(copy, "shorelineContactFade"), Is.EqualTo(0.42f));
                Assert.That(Reflection.Get<float>(copy, "shorelineNormalFlattening"), Is.EqualTo(0.76f));
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
        public void WaterProfile_VisualTuningControlsValidateAndSerialize()
        {
            Type profileType = Reflection.FindType("Sol.Water.SolWaterProfile");
            ScriptableObject profile = ScriptableObject.CreateInstance(profileType);
            ScriptableObject copy = ScriptableObject.CreateInstance(profileType);
            try
            {
                Reflection.Set(profile, "foamTextureScale", -3f);
                Reflection.Set(profile, "foamTextureContrast", 12f);
                Reflection.Set(profile, "foamBrightness", -1f);
                Reflection.Set(profile, "skyReflectionStrength", 4f);
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

                Reflection.Set(profile, "foamTextureScale", 0.055f);
                Reflection.Set(profile, "foamTextureContrast", 2.4f);
                Reflection.Set(profile, "foamBrightness", 0.62f);
                Reflection.Set(profile, "skyReflectionStrength", 0.9f);
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
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
                UnityEngine.Object.DestroyImmediate(copy);
            }
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

            Assert.That((float)visibleDistance.Invoke(null, new object[] { 0 }), Is.EqualTo(40f));
            Assert.That((float)visibleDistance.Invoke(null, new object[] { 3 }), Is.EqualTo(4800f));
            Assert.That((float)heightScale.Invoke(null, new object[] { 2 }), Is.EqualTo(0.6f));
            Assert.That((float)checkerboard.Invoke(null, new object[] { 0, 0 }), Is.EqualTo(1f));
            Assert.That((float)checkerboard.Invoke(null, new object[] { 1, 0 }), Is.EqualTo(-1f));

            float calmForward = (float)directional.Invoke(null,
                new object[] { Vector2.right, Vector2.right, 2, 0f, false });
            float calmSide = (float)directional.Invoke(null,
                new object[] { Vector2.up, Vector2.right, 2, 0f, false });
            float turbulentForward = (float)directional.Invoke(null,
                new object[] { Vector2.right, Vector2.right, 2, 1f, false });
            Assert.That(calmForward, Is.GreaterThan(calmSide));
            Assert.That(turbulentForward, Is.GreaterThan(0f));
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
    }
}
