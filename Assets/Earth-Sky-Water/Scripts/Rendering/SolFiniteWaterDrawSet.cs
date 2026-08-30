using System.Collections.Generic;
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

        internal void Build(Camera camera, SolWaterWorld world, SolWaterDebugMode debugMode)
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
                FillProperties(properties, _waveA[index], _waveB[index], body, world, debugMode);
                _items[index] = new Item(mesh, renderer.localToWorldMatrix, properties);
            }
        }

        static void FillProperties(
            MaterialPropertyBlock properties,
            Vector4[] waveA,
            Vector4[] waveB,
            SolWaterBody body,
            SolWaterWorld world,
            SolWaterDebugMode debugMode)
        {
            properties.Clear();
            SolWaterProfile profile = body.Profile;
            if (profile == null)
                return;

            ISolWaterGeometry geometry = body.Geometry;
            int waveCount = geometry != null && !geometry.SupportsSurfaceWaves
                ? 0 : (profile.gerstnerWaves == null ? 0 : Mathf.Min(8, profile.gerstnerWaves.Length));
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

            SolWaterPropertyTarget target = new(properties);

            // Everything derived from the profile and the environment is written by the
            // shared writer, so a new profile field reaches finite bodies and the ocean
            // together. This block keeps only what a finite body knows that the ocean
            // does not: its own identity, its Gerstner set, and its geometry mode.
            //
            // Debug mode and SSR availability are handed over rather than hardcoded --
            // this path used to publish zero for the debug mode, so selecting a debug
            // view silently showed final colour on rivers, lakes, waterfalls and flood
            // extents while the ocean beside them switched correctly.
            SolWaterQualityProfile quality = world.QualityProfile;
            bool screenSpaceReflections = quality == null
                || (quality.screenSpaceReflections && quality.ssrResolutionScale > 0f);
            SolWaterMaterialState.ApplyProfile(
                target, profile, world, debugMode, screenSpaceReflections);

            properties.SetVectorArray(SolWaterShaderIds.WaveDataA, waveA);
            properties.SetVectorArray(SolWaterShaderIds.WaveDataB, waveB);
            properties.SetInt(SolWaterShaderIds.WaveCount, waveCount);
            properties.SetFloat(SolWaterShaderIds.BodyHash, body.PrepassHash / 16777215f);
            int geometryMode = body.BodyType == SolWaterBodyType.Waterfall ? 2 : 1;
            Vector3 representativeFlow = geometry != null
                ? geometry.RepresentativeFlow : body.AuthoredFlow;
            properties.SetVector(SolWaterShaderIds.GeometryParams,
                new Vector4(geometryMode, 1f, 1f, 0f));
            properties.SetVector(SolWaterShaderIds.BodyFlow,
                new Vector4(representativeFlow.x, representativeFlow.y,
                    representativeFlow.z, representativeFlow.magnitude));
            // Finite meshes use their bounded Gerstner fallback. Ocean FFT displacement is
            // clipmap-oriented and cannot be safely reused on river bends or vertical sheets.
            properties.SetVector(SolWaterShaderIds.SpectralParams, Vector4.zero);

            SolWaterMaterialState.ApplyInteractionZone(target, body.InteractionZone);
        }
    }
}
