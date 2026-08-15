using System;
using System.Collections.Generic;
using Sol.Environment;
using UnityEngine;

namespace Sol.Water.Rendering
{
    /// <summary>Allocation-free camera-local draw data for authored finite water meshes.</summary>
    internal sealed class SolFiniteWaterDrawSet
    {
        internal readonly struct Item
        {
            internal readonly Mesh Mesh;
            internal readonly Matrix4x4 Matrix;
            internal readonly MaterialPropertyBlock Properties;

            internal Item(Mesh mesh, Matrix4x4 matrix, MaterialPropertyBlock properties)
            {
                Mesh = mesh;
                Matrix = matrix;
                Properties = properties;
            }
        }

        const int MaximumBodies = 128;
        readonly Item[] _items = new Item[MaximumBodies];
        readonly MaterialPropertyBlock[] _properties = new MaterialPropertyBlock[MaximumBodies];
        readonly Vector4[][] _waveA = new Vector4[MaximumBodies][];
        readonly Vector4[][] _waveB = new Vector4[MaximumBodies][];
        readonly Plane[] _frustumPlanes = new Plane[6];

        internal Item[] Items => _items;
        internal int Count { get; private set; }

        internal SolFiniteWaterDrawSet()
        {
            for (int i = 0; i < MaximumBodies; i++)
            {
                _properties[i] = new MaterialPropertyBlock();
                _waveA[i] = new Vector4[8];
                _waveB[i] = new Vector4[8];
            }
        }

        internal void Build(Camera camera, SolWaterWorld world)
        {
            Count = 0;
            if (camera == null || world == null)
                return;
            GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanes);
            IReadOnlyList<SolWaterBody> bodies = world.Bodies;
            for (int i = 0; i < bodies.Count && Count < MaximumBodies; i++)
            {
                SolWaterBody body = bodies[i];
                if (body == null || !body.isActiveAndEnabled || body.IsInfinite)
                    continue;
                Renderer renderer = body.AuthoredSurface;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if ((camera.cullingMask & (1 << renderer.gameObject.layer)) == 0)
                    continue;
                if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, renderer.bounds))
                    continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                    continue;

                int index = Count++;
                MaterialPropertyBlock properties = _properties[index];
                FillProperties(properties, _waveA[index], _waveB[index], body, world);
                _items[index] = new Item(mesh, renderer.localToWorldMatrix, properties);
            }
        }

        static void FillProperties(
            MaterialPropertyBlock properties,
            Vector4[] waveA,
            Vector4[] waveB,
            SolWaterBody body,
            SolWaterWorld world)
        {
            properties.Clear();
            SolWaterProfile profile = body.Profile;
            if (profile == null)
                return;

            int waveCount = profile.gerstnerWaves == null ? 0 : Mathf.Min(8, profile.gerstnerWaves.Length);
            for (int i = 0; i < 8; i++)
            {
                if (i < waveCount)
                {
                    SolGerstnerWave wave = profile.gerstnerWaves[i];
                    Vector2 direction = wave.direction.sqrMagnitude > 0.0001f
                        ? wave.direction.normalized
                        : Vector2.right;
                    waveA[i] = new Vector4(direction.x, direction.y,
                        Mathf.Max(0.01f, wave.wavelength), Mathf.Max(0f, wave.amplitude));
                    waveB[i] = new Vector4(Mathf.Clamp01(wave.steepness), wave.phaseOffset, 0f, 0f);
                }
                else
                {
                    waveA[i] = Vector4.zero;
                    waveB[i] = Vector4.zero;
                }
            }

            SolEnvironmentState environment = SolEnvironmentWorld.Active != null
                ? SolEnvironmentWorld.Active.State
                : default;
            SolDouble3 origin = SolWorldOriginService.Active?.LogicalOrigin ?? default;
            properties.SetVectorArray(SolWaterShaderIds.WaveDataA, waveA);
            properties.SetVectorArray(SolWaterShaderIds.WaveDataB, waveB);
            properties.SetInt(SolWaterShaderIds.WaveCount, waveCount);
            properties.SetFloat(SolWaterShaderIds.BodyHash, body.PrepassHash / 16777215f);
            properties.SetFloat(SolWaterShaderIds.WaveTime, (float)world.WaveTime);
            properties.SetVector(SolWaterShaderIds.WorldOrigin,
                new Vector4((float)origin.X, (float)origin.Y, (float)origin.Z, 0f));
            properties.SetVector(SolWaterShaderIds.Wind,
                new Vector4(environment.Wind.Direction.x, environment.Wind.Direction.y,
                    environment.Wind.Direction.z, environment.Wind.Speed));
            properties.SetVector(SolWaterShaderIds.Weather,
                new Vector4(environment.Wind.Speed, environment.Weather.WaterTurbulence,
                    environment.Weather.Rain, environment.Weather.WaveSpeedMultiplier));
            properties.SetVector(SolWaterShaderIds.WeatherExtended,
                new Vector4(environment.Weather.Cloudiness * profile.cloudShadowStrength,
                    environment.Weather.Lightning * profile.lightningReflectionStrength,
                    profile.rainRoughness, profile.rainNormalStrength));
            properties.SetColor(SolWaterShaderIds.ShallowColor, profile.shallowScattering);
            properties.SetColor(SolWaterShaderIds.DeepColor, profile.deepScattering);
            properties.SetVector(SolWaterShaderIds.Absorption, profile.absorption);
            properties.SetVector(SolWaterShaderIds.Optics,
                new Vector4(profile.indexOfRefraction, profile.smoothness,
                    profile.scatteringStrength, profile.waveSpeed));
            properties.SetVector(SolWaterShaderIds.Spectrum,
                new Vector4(profile.windResponse, 0f, 0f, 0f));
            properties.SetTexture(SolWaterShaderIds.ShorelineMask, profile.shorelineMask);
            properties.SetVector(SolWaterShaderIds.ShorelineParams,
                new Vector4(profile.shorelineMaskMapping.x, profile.shorelineMaskMapping.y,
                    profile.shorelineMaskMapping.z, profile.shorelineMaskMapping.w));
            properties.SetVector(SolWaterShaderIds.ShorelineDetail,
                new Vector4(profile.shorelineMask != null ? profile.shorelineMaskStrength : 0f,
                    profile.shorelineFoamWidth, profile.shorelineWetness,
                    profile.shorelineMask != null ? 1f : 0f));
            properties.SetTexture(SolWaterShaderIds.ShorelineData,
                profile.shorelineData != null ? profile.shorelineData : Texture2D.grayTexture);
            properties.SetVector(SolWaterShaderIds.ShorelineDataMapping,
                profile.shorelineDataMapping);
            properties.SetVector(SolWaterShaderIds.ShorelineDataParams,
                new Vector4(profile.shorelineDepthRange, profile.shorelineDistanceRange,
                    profile.shallowWaveAttenuationDepth, profile.shorelineData != null ? 1f : 0f));
            properties.SetVector(SolWaterShaderIds.ShorelineSurfaceParams,
                new Vector4(profile.shorelineContactFade,
                    profile.shorelineNormalFlattening, 0f, 0f));
            properties.SetColor(SolWaterShaderIds.FoamColor, profile.foamColor);
            properties.SetTexture(SolWaterShaderIds.FoamTexture, profile.foamTexture);
            properties.SetVector(SolWaterShaderIds.FoamDetail,
                new Vector4(profile.foamTextureScale, profile.foamTextureContrast,
                    profile.foamBrightness, profile.foamTexture != null ? 1f : 0f));
            properties.SetVector(SolWaterShaderIds.FoamParams,
                new Vector4(profile.crestFoamStrength, profile.shorelineFoamStrength,
                    profile.causticStrength, profile.causticScale));
            properties.SetVector(SolWaterShaderIds.ReflectionParams,
                new Vector4(profile.skyReflectionStrength, 0f, 0f, 0f));
            SolWaterSkyReflectionState.Resolve().Apply(properties);

            SolWaterInteractionZone zone = body.InteractionZone;
            if (zone != null && zone.IsReady)
            {
                properties.SetTexture(SolWaterShaderIds.InteractionTexture, zone.StateTexture);
                properties.SetVector(SolWaterShaderIds.InteractionMapping, zone.GetShaderMapping());
                properties.SetVector(SolWaterShaderIds.InteractionTexel,
                    new Vector4(1f / Mathf.Max(1, zone.StateTexture.width),
                        1f / Mathf.Max(1, zone.StateTexture.height), 0f, 0f));
                properties.SetFloat(SolWaterShaderIds.InteractionStrength, zone.HeightStrength);
            }
            else
            {
                properties.SetFloat(SolWaterShaderIds.InteractionStrength, 0f);
            }
        }
    }
}
