using System.IO;
using NUnit.Framework;

namespace Sol.Tests.Editor
{
    public sealed class SolLandscapeSnowRegressionTests
    {
        const string TerrainSnowIncludePath =
            "Assets/Earth-Sky-Water/Shaders/Terrain/SolTerrainAutoMaterial.hlsl";
        const string EnvironmentWorldPath =
            "Assets/Earth-Sky-Water/Scripts/Core/SolEnvironmentWorld.cs";

        [Test]
        public void AccumulatedSnowCoverage_DoesNotChangeWithTimeOfDayTemperature()
        {
            string shaderSource = File.ReadAllText(TerrainSnowIncludePath);

            Assert.That(shaderSource, Does.Contain("saturate(_Sol_SurfaceSnowCover)"),
                "Terrain snow must be driven by the accumulated surface state.");
            Assert.That(shaderSource, Does.Not.Contain("_Sol_SurfaceTemperature"),
                "The terrain shader must not reveal or hide stored snow as the daily air temperature changes.");
        }

        [Test]
        public void SnowLifecycle_RemainsOwnedByEnvironmentSimulation()
        {
            string worldSource = File.ReadAllText(EnvironmentWorldPath);

            Assert.That(worldSource, Does.Contain("precipitation * ResolveSnowFraction()"),
                "Freezing precipitation must continue to accumulate snow cover.");
            Assert.That(worldSource, Does.Contain("_temperature > 0f && _snowCover > 0f"),
                "Warm weather must continue to thaw accumulated snow over simulation time.");
        }
    }
}
