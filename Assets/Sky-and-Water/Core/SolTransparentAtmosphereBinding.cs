using UnityEngine;

/// <summary>
/// Opt-in marker for transparent and particle materials that call SolAtmosphere.hlsl.
/// It sets only per-renderer data and never modifies shared material assets.
/// </summary>
[ExecuteAlways, DisallowMultipleComponent]
public sealed class SolTransparentAtmosphereBinding : MonoBehaviour
{
    static readonly int EnabledId = Shader.PropertyToID("_SolAtmosphereTransparentFog");
    [SerializeField] Renderer target;
    [SerializeField, Range(0f, 1f)] float strength = 1f;
    readonly MaterialPropertyBlock _properties = new();

    void Reset() => target = GetComponent<Renderer>();
    void OnEnable() => Apply();
    void OnValidate() => Apply();

    public void Apply()
    {
        if (target == null)
            target = GetComponent<Renderer>();
        if (target == null)
            return;
        target.GetPropertyBlock(_properties);
        _properties.SetFloat(EnabledId, strength);
        target.SetPropertyBlock(_properties);
    }

    void OnDisable()
    {
        if (target == null)
            return;
        target.GetPropertyBlock(_properties);
        _properties.SetFloat(EnabledId, 0f);
        target.SetPropertyBlock(_properties);
    }
}
