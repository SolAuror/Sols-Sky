using System;
using System.Collections.Generic;
using Sol.Environment;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Sol.Streaming
{
    [Serializable]
    public struct SolAdditiveSceneCell
    {
        public SolEnvironmentCellId id;
        [Tooltip("Scene name or build-settings path loaded additively.")]
        public string scene;
    }

    /// <summary>Default additive-scene implementation of the streaming contract.</summary>
    [DisallowMultipleComponent]
    public sealed class SolAdditiveSceneCellProvider : MonoBehaviour, IEnvironmentCellProvider,
        ISolOriginShiftParticipant
    {
        [SerializeField] SolAdditiveSceneCell[] cells = Array.Empty<SolAdditiveSceneCell>();

        readonly Dictionary<SolEnvironmentCellId, SolAdditiveSceneCell> _definitions = new();
        readonly Dictionary<SolEnvironmentCellId, SolEnvironmentCellState> _states = new();
        readonly HashSet<SolEnvironmentCellId> _loaded = new();

        public event Action<SolEnvironmentCellId> CellLoaded;
        public event Action<SolEnvironmentCellId> CellUnloaded;
        public event Action<SolEnvironmentCellId, string> CellFailed;
        public event Action<Vector3, SolDouble3> OriginShifted;

        public IReadOnlyCollection<SolEnvironmentCellId> LoadedCells => _loaded;

        void Awake() => RebuildDefinitions();

        void OnEnable()
        {
            RebuildDefinitions();
            SolWorldOriginService.Active?.Register(this);
        }

        void OnDisable() => SolWorldOriginService.Active?.Unregister(this);

        public SolEnvironmentCellState GetState(SolEnvironmentCellId id)
            => _states.TryGetValue(id, out SolEnvironmentCellState state)
                ? state
                : SolEnvironmentCellState.Unloaded;

        public bool RequestLoad(SolEnvironmentCellId id)
        {
            if (!_definitions.TryGetValue(id, out SolAdditiveSceneCell definition)
                || string.IsNullOrWhiteSpace(definition.scene))
                return Fail(id, "Unknown cell or scene.");
            SolEnvironmentCellState state = GetState(id);
            if (state is SolEnvironmentCellState.Loaded or SolEnvironmentCellState.Loading)
                return false;

            AsyncOperation operation = SceneManager.LoadSceneAsync(definition.scene, LoadSceneMode.Additive);
            if (operation == null)
                return Fail(id, $"Unity could not start loading '{definition.scene}'.");
            _states[id] = SolEnvironmentCellState.Loading;
            operation.completed += _ =>
            {
                Scene scene = SceneManager.GetSceneByPath(definition.scene);
                if (!scene.IsValid())
                    scene = SceneManager.GetSceneByName(definition.scene);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    Fail(id, $"Scene '{definition.scene}' did not become loaded.");
                    return;
                }
                _states[id] = SolEnvironmentCellState.Loaded;
                _loaded.Add(id);
                CellLoaded?.Invoke(id);
            };
            return true;
        }

        public bool RequestUnload(SolEnvironmentCellId id)
        {
            if (!_definitions.TryGetValue(id, out SolAdditiveSceneCell definition))
                return Fail(id, "Unknown cell.");
            if (GetState(id) != SolEnvironmentCellState.Loaded)
                return false;
            Scene scene = SceneManager.GetSceneByPath(definition.scene);
            if (!scene.IsValid())
                scene = SceneManager.GetSceneByName(definition.scene);
            if (!scene.IsValid() || !scene.isLoaded)
                return Fail(id, $"Scene '{definition.scene}' is not loaded.");

            AsyncOperation operation = SceneManager.UnloadSceneAsync(scene);
            if (operation == null)
                return Fail(id, $"Unity could not start unloading '{definition.scene}'.");
            _states[id] = SolEnvironmentCellState.Unloading;
            operation.completed += _ =>
            {
                _states[id] = SolEnvironmentCellState.Unloaded;
                _loaded.Remove(id);
                CellUnloaded?.Invoke(id);
            };
            return true;
        }

        public void NotifyOriginShift(Vector3 localShift, SolDouble3 logicalOrigin)
            => OriginShifted?.Invoke(localShift, logicalOrigin);

        public void OnSolOriginShift(Vector3 localShift, SolDouble3 logicalOrigin)
            => NotifyOriginShift(localShift, logicalOrigin);

        void RebuildDefinitions()
        {
            _definitions.Clear();
            if (cells == null)
                return;
            for (int i = 0; i < cells.Length; i++)
            {
                SolAdditiveSceneCell cell = cells[i];
                if (!cell.id.IsValid || string.IsNullOrWhiteSpace(cell.scene))
                    continue;
                if (_definitions.ContainsKey(cell.id))
                    Debug.LogError($"[SolStreaming] Duplicate cell ID {cell.id}.", this);
                else
                    _definitions.Add(cell.id, cell);
            }
        }

        bool Fail(SolEnvironmentCellId id, string message)
        {
            _states[id] = SolEnvironmentCellState.Failed;
            CellFailed?.Invoke(id, message);
            Debug.LogError($"[SolStreaming] Cell {id}: {message}", this);
            return false;
        }
    }
}
