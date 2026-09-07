using System.IO;
using NUnit.Framework;
using Sol.Environment;
using Sol.Lighting;

namespace Sol.Tests.Editor
{
    public sealed class SolLightingDiagnosticsTests
    {
        [Test]
        public void BudgetTracksLightingGaugesAndSchedulerRequests()
        {
            SolEnvironmentBudget.Counters before = SolEnvironmentBudget.Current;

            SolEnvironmentBudget.SetLightingState(12, 8, 7, 3,
                (int)SolLightingQualityTier.High);
            SolEnvironmentBudget.AddGiRequest();
            SolEnvironmentBudget.AddProbeRequest();
            SolEnvironmentBudget.AddProbeCompletion();
            SolEnvironmentBudget.Counters after = SolEnvironmentBudget.Current;

            Assert.IsTrue(after.HasLightingDirector);
            Assert.AreEqual(SolLightingQualityTier.High,
                (SolLightingQualityTier)after.ActiveLightingTier);
            Assert.AreEqual(12, after.RegisteredLights);
            Assert.AreEqual(8, after.ActiveLights);
            Assert.AreEqual(7, after.ShadowSlices);
            Assert.AreEqual(3, after.VolumetricLights);
            Assert.AreEqual(before.GiRequests + 1, after.GiRequests);
            Assert.AreEqual(before.ProbeRequests + 1, after.ProbeRequests);
            Assert.AreEqual(before.ProbeCompletions + 1, after.ProbeCompletions);
        }

        [Test]
        public void FinalProfilerMarkersAndWindowMetricsRemainDiscoverable()
        {
            string director = File.ReadAllText(
                "Assets/Earth-Sky-Water/Scripts/Lighting/SolLightingDirector.cs");
            string scheduler = File.ReadAllText(
                "Assets/Earth-Sky-Water/Scripts/Management/SolSkyLightingScheduler.cs");
            string window = File.ReadAllText(
                "Assets/Earth-Sky-Water/Scripts/Core/Editor/SolEnvironmentWindow.cs");

            StringAssert.Contains("Sol.Lighting.Selection", director);
            StringAssert.Contains("Sol.Lighting.Apply", director);
            StringAssert.Contains("Sol.Lighting.ProbeScheduling", scheduler);
            StringAssert.Contains("Sol.Lighting.DynamicGI", scheduler);
            StringAssert.Contains("Lights registered / active", window);
            StringAssert.Contains("Shadow slices", window);
            StringAssert.Contains("Probe requests / done", window);
        }
    }
}
