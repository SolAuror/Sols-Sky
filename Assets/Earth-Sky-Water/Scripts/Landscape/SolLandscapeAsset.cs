using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sol.Landscape
{
    /// <summary>Persistent identity and explicit ownership. Source library artwork is never owned.</summary>
    public sealed class SolLandscapeAsset : ScriptableObject
    {
        public int version = 1;
        public string landscapeId;
        public string sceneGuid;
        public string landscapeName;
        public List<UnityEngine.Object> ownedAssets = new();
        public List<SolLandscapeAssetMove> moves = new();
    }
    [Serializable]
    public sealed class SolLandscapeAssetMove
    {
        public string guid, before, after, utc;
    }
}
