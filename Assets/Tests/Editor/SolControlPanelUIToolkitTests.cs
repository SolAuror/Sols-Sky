using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Sol.Environment.EditorTools;
using Sol.ToD;
using Sol.Water;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Sol.Tests.Editor
{
    public sealed class SolControlPanelUIToolkitTests
    {
        ElementaControlPanel _window;
        SceneSetup[] _scenes;
        readonly List<Object> _temporary = new();

        [SetUp]
        public void SetUp()
        {
            _scenes = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _window = ScriptableObject.CreateInstance<ElementaControlPanel>();
        }
        [TearDown]
        public void TearDown()
        {
            if (_window != null) Object.DestroyImmediate(_window);
            foreach (var target in _temporary) if (target != null) Object.DestroyImmediate(target);
            _temporary.Clear();
            if (_scenes.Any(s => s.isLoaded && s.isActive)) EditorSceneManager.RestoreSceneManagerSetup(_scenes);
            else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        T Asset<T>() where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>(); asset.name = "Elementa test " + typeof(T).Name + _temporary.Count; _temporary.Add(asset); return asset;
        }
        TimeOfDay Clock()
        {
            var clock = new GameObject("Elementa test clock").AddComponent<TimeOfDay>();
            _temporary.Add(clock.gameObject); _window.Context.State.timeOfDay = clock;
            return clock;
        }
        SolWeatherManager Weather()
        {
            var weather = new GameObject("Elementa test weather").AddComponent<SolWeatherManager>();
            _temporary.Add(weather.gameObject);
            weather.profiles = new[] { new SolWeatherSelection { profile = Asset<SolWeatherProfileAsset>(), weight = .7f },
                new SolWeatherSelection { profile = Asset<SolWeatherProfileAsset>(), weight = .3f } };
            _window.Context.State.weatherManager = weather;
            return weather;
        }

        [Test]
        public void EveryPage_BuildsNativeControls_AndDoesNotStartPreview()
        {
            Clock().SetSkyProfile(Asset<SolSkyProfile>()); var weather = Weather();
            _window.Context.Resolve(true);
            Assert.That(_window.Pages.Select(p => p.Id), Is.EqualTo(new[] { "overview", "time", "sky", "weather", "clouds", "water", "landscape", "lighting", "quality", "diagnostics" }));
            foreach (var page in _window.Pages)
            {
                var root = page.BuildContent(_window.Context);
                Assert.That(root.Query<IMGUIContainer>().ToList(), Is.Empty, page.Title);
                Assert.That(root.Query<HelpBox>().ToList().Where(h => h.text.StartsWith("Unavailable field:")), Is.Empty, page.Title);
                Assert.False(weather.HasPreview, page.Title);
                var before = root.Query<VisualElement>().ToList();
                page.Refresh(); page.Refresh();
                Assert.That(root.Query<VisualElement>().ToList(), Is.EqualTo(before), page.Title + " rebuilt its tree on refresh");
                page.Dispose();
            }
        }

        [Test]
        public void DemoCatalogue_RegistersRealControlsAndValidRoutes_ForEveryVisibleProperty()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Elementa_Demo.unity", OpenSceneMode.Single);
            _window.Context.Resolve(true); _window.Context.RefreshIssues(true);
            _window.Context.State.cloudsWeather = false;
            var entries = new List<ElementaFieldCatalogue.Entry>();
            foreach (var page in _window.Pages)
            {
                var root = page.BuildContent(_window.Context);
                Assert.That(root.Query<HelpBox>().ToList().Where(h => h.text.StartsWith("Unavailable field:")).Select(h => h.text), Is.Empty, page.Title);
                foreach (var entry in page.Catalogue.Entries)
                {
                    Assert.True(entry.Control == root || root.Contains(entry.Control), page.Title + "/" + entry.Path + " is detached");
                    Assert.NotNull(_window.Context.Serialized(entry.Target).FindProperty(entry.Path), page.Title + "/" + entry.Path);
                    if (entry.Role == ElementaFieldRole.ReadOnly) Assert.False(entry.Control.enabledSelf, entry.Path);
                    entries.Add(entry);
                }
            }
            foreach (var entry in entries.Where(e => e.Role == ElementaFieldRole.Routed))
            {
                var destination = _window.Pages.Single(p => p.Title == entry.Route || p.Id == entry.Route);
                Assert.True(destination.Catalogue.Entries.Any(e => e.Target == entry.Target && e.Path == entry.Path && e.Role != ElementaFieldRole.Routed),
                    entry.Target.name + "/" + entry.Path + " has no control on " + entry.Route);
            }
            var missing = new List<string>();
            foreach (var target in entries.Select(e => e.Target).Distinct())
            {
                // URP itself and per-body geometry have their own inspectors. Elementa owns
                // its required pipeline flags and the selected body's profile/level controls.
                if (target is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset || target is SolWaterBody) continue;
                var iterator = _window.Context.Serialized(target).GetIterator(); bool children = true;
                while (iterator.NextVisible(children))
                {
                    children = false; string path = iterator.propertyPath;
                    if (path == "m_Script") continue;
                    if (!entries.Any(e => e.Target == target && (e.Path == path || e.Path.StartsWith(path + "."))))
                        missing.Add(target.GetType().Name + "/" + path);
                }
            }
            Assert.That(missing, Is.Empty, string.Join("\n", missing));
        }

        [Test]
        public void WeatherShape_IsBoundOnlyToTheNamedProfile_AndFormationFieldsAreDisabled()
        {
            var time = Clock(); var sky = Asset<SolSkyProfile>(); time.SetSkyProfile(sky);
            var weather = Weather(); var profile = weather.profiles[0].profile; profile.overrideAdvancedCloudShape = false;
            _window.Context.State.inspectedWeather = profile; _window.Context.State.cloudsWeather = true;
            var page = _window.Pages.Single(p => p.Id == "clouds"); page.BuildContent(_window.Context);
            var density = page.Catalogue.Entries.Single(e => e.Target == profile && e.Path == "cloudDensity");
            Assert.False(density.Control.enabledInHierarchy);
            Assert.That(page.Catalogue.Entries.Where(e => e.Target == sky), Is.Empty);
            profile.overrideAdvancedCloudShape = true; page.Refresh();
            Assert.True(density.Control.enabledInHierarchy);
            Assert.That(((Slider)page.Catalogue.Entries.Single(e => e.Target == profile && e.Path == "cloudiness").Control).label, Is.EqualTo("Weather influence"));
            Assert.False(weather.HasPreview);
        }

        [Test]
        public void Preview_EndAndCloseRestoreWeather_WithoutRevertingAuthoredEditsOrClock()
        {
            var time = Clock(); var manager = Weather();
            _window.Context.State.weatherA = manager.profiles[0].profile;
            _window.Context.State.weatherB = manager.profiles[1].profile;
            _window.Context.State.weatherBlend = .7f;
            var before = manager.CurrentState;
            _window.StartWeatherPreview(); Assert.True(manager.HasPreview);
            manager.profiles[1].profile.cloudDensity = 1.23f;
            ElementaPanelActions.SetClock(_window.Context, 13.25f);
            _window.ClearWeatherPreview(); Assert.False(manager.HasPreview);
            Assert.That(manager.CurrentState.RainIntensity, Is.EqualTo(before.RainIntensity));
            Assert.That(manager.profiles[1].profile.cloudDensity, Is.EqualTo(1.23f));
            Assert.That(time.ClockHour, Is.EqualTo(13.25f).Within(.001f));
            _window.StartWeatherPreview(); Object.DestroyImmediate(_window);
            Assert.False(manager.HasPreview);
        }

        [Test]
        public void CopyWeatherProfile_PreservesWeights_AndUndoRestoresOnlyAssignment()
        {
            var manager = Weather(); var source = manager.profiles[0].profile;
            string path = "Assets/ElementaTestCopy-" + Guid.NewGuid().ToString("N") + ".asset";
            try
            {
                Undo.IncrementCurrentGroup();
                ElementaPanelActions.CopyProfileTo(_window.Context, manager, "profiles.Array.data[0].profile", path);
                Assert.AreNotSame(source, manager.profiles[0].profile);
                Assert.That(manager.profiles[0].weight, Is.EqualTo(.7f));
                Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
                Assert.AreSame(source, manager.profiles[0].profile);
                Assert.NotNull(AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(path));
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        [UnityTest]
        public IEnumerator NavigationAndLiveUpdates_KeepNativeBindingsAndFocus()
        {
            var time = Clock(); time.SetSkyProfile(Asset<SolSkyProfile>());
            _window.position = new Rect(100, 100, 900, 900);
            _window.Show(); _window.ShowPage("Sky");
            yield return null; yield return null;
            var page = _window.ActivePage;
            var field = page.Root.Q<Slider>("twilightIntensity");
            Assert.NotNull(field);
            var input = field.Q<TextField>(); Assert.NotNull(input);
            _window.rootVisualElement.Q<ScrollView>("page-scroll").ScrollTo(field);
            _window.Focus(); input.Focus();
            yield return null;
            // FocusController retargets focus from the slider's input to its composite root.
            var originalFocus = _window.rootVisualElement.focusController.focusedElement;
            Assert.AreSame(field, originalFocus);
            var tree = page.Root;
            var serialized = _window.Context.Serialized(time.SkyProfile);
            serialized.FindProperty("dayZenith").colorValue = Color.magenta; serialized.ApplyModifiedProperties();
            _window.Context.CheckEdits(true); _window.RequestRefresh();
            yield return null; yield return null;
            Assert.AreSame(tree, _window.ActivePage.Root);
            Assert.AreSame(originalFocus, _window.rootVisualElement.focusController.focusedElement);
            _window.ShowPage("Weather"); yield return null; _window.ShowPage("Sky"); yield return null;
            Assert.False(_window.HasWeatherPreview);
        }

        [Test]
        public void LiveClockDoesNotPublishAnAuthoredEdit_AndOneProfileChangePublishesOnce()
        {
            var time = Clock(); var sky = Asset<SolSkyProfile>(); time.SetSkyProfile(sky);
            var page = _window.Pages.Single(p => p.Id == "sky"); page.BuildContent(_window.Context);
            _window.Context.CheckEdits(true); int before = _window.Context.EditPublicationCount;
            ElementaPanelActions.SetClock(_window.Context, 10.5f);
            _window.Context.CheckEdits(true);
            Assert.That(_window.Context.EditPublicationCount, Is.EqualTo(before));
            var serialized = _window.Context.Serialized(sky); serialized.FindProperty("cloudDensity").floatValue = .8f;
            serialized.ApplyModifiedProperties(); _window.Context.CheckEdits(true); _window.Context.CheckEdits(true);
            Assert.That(_window.Context.EditPublicationCount, Is.EqualTo(before + 1));
            Assert.AreSame(sky, time.SkyProfile);
        }

        [Test]
        public void WaterBodySelectionDoesNotCreateOrEditAnInheritedDefault()
        {
            var world = new GameObject("Elementa test water world").AddComponent<SolWaterWorld>(); _temporary.Add(world.gameObject);
            var profile = Asset<SolWaterProfile>();
            var serialized = new SerializedObject(world); serialized.FindProperty("defaultProfile").objectReferenceValue = profile; serialized.ApplyModifiedProperties();
            var body = new GameObject("Elementa test water body").AddComponent<SolWaterBody>(); _temporary.Add(body.gameObject);
            _window.Context.Resolve(true); _window.Context.State.selectedWaterBody = body;
            var page = _window.Pages.Single(p => p.Id == "water"); page.BuildContent(_window.Context);
            Assert.IsNull(new SerializedObject(body).FindProperty("profile").objectReferenceValue);
            Assert.False(page.Catalogue.Entries.Any(e => e.Target == profile));
            Assert.NotNull(page.Root.Q<Button>("Edit shared default"));
        }

        [Test]
        public void Preview_TargetReplacementAndReloadReleaseOwnership()
        {
            var first = Weather();
            _window.Context.State.weatherA = first.profiles[0].profile;
            _window.Context.State.weatherB = first.profiles[1].profile;
            _window.StartWeatherPreview(); Assert.True(first.HasPreview);
            _window.ClearWeatherPreview();
            var next = Weather();
            _window.Context.State.weatherA = next.profiles[0].profile; _window.Context.State.weatherB = next.profiles[1].profile;
            Assert.False(first.HasPreview); Assert.False(next.HasPreview);
            _window.StartWeatherPreview(); Assert.True(next.HasPreview);
            // Same event callback used for both domain-reload configurations.
            typeof(ElementaControlPanel).GetMethod("OnPlayMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(_window, new object[] { PlayModeStateChange.ExitingEditMode });
            Assert.False(next.HasPreview);
            Assert.False(_window.Context.State.previewWeather);
        }
    }
}
