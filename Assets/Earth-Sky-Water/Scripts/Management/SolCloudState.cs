using System;
using Sol.Environment;
using Sol.ToD;
using UnityEngine;

public enum SolCloudFormation
{
    Cumulus = 0,
    Stratus = 1,
    Nimbostratus = 2,
    Cumulonimbus = 3,
}

/// <summary>Immutable resolved cloud parameters shared by weather and rendering.</summary>
[Serializable]
public readonly struct SolCloudState : IEquatable<SolCloudState>
{
    public readonly SolCloudFormation DominantFormation;
    public readonly float Coverage;
    public readonly float Erosion;
    public readonly float Density;
    public readonly float BaseHeight;
    public readonly float Thickness;
    public readonly float VerticalDevelopment;
    public readonly float AnvilAmount;
    public readonly float CirrusAmount;
    /// <summary>
    /// Width of the density boundary, as a multiplier on the formation's own width.
    /// Low values give hard cauliflower edges, high values give a soft stratus haze.
    /// </summary>
    public readonly float EdgeSoftness;
    /// <summary>Diffuseness of the cloud underside. 0 is a crisp flat cumulus base.</summary>
    public readonly float BaseSoftness;
    /// <summary>
    /// Additive nudge to coverage, independent of the overcast influence. It lets a profile
    /// thicken or thin the deck without also changing how far it overrides the authored sky.
    /// </summary>
    public readonly float CoverageBias;
    public readonly Vector2 WeatherOffset;
    public readonly Vector2 ShapeOffset;
    public readonly Vector2 DetailOffset;
    public readonly float ShadowStrength;
    /// <summary>
    /// Normalised per-formation weights this state represents. Carried rather than rebuilt
    /// from <see cref="DominantFormation"/>, which Lerp resolves with a hard switch at the
    /// midpoint: deriving a one-hot vector from that label put most of a formation change
    /// into a single frame of _SolCloudFormationWeights. The shader has always expected a
    /// continuous blend here.
    /// </summary>
    public readonly Vector4 FormationBlend;

    public SolCloudState(
        SolCloudFormation dominantFormation,
        float coverage,
        float erosion,
        float density,
        float baseHeight,
        float thickness,
        float verticalDevelopment,
        float anvilAmount,
        float cirrusAmount,
        Vector2 weatherOffset,
        Vector2 shapeOffset,
        Vector2 detailOffset,
        float shadowStrength)
        : this(dominantFormation, coverage, erosion, density, baseHeight, thickness,
            verticalDevelopment, anvilAmount, cirrusAmount, 0.5f, 0.5f, 0f,
            weatherOffset, shapeOffset, detailOffset, shadowStrength) { }

    public SolCloudState(
        SolCloudFormation dominantFormation,
        float coverage,
        float erosion,
        float density,
        float baseHeight,
        float thickness,
        float verticalDevelopment,
        float anvilAmount,
        float cirrusAmount,
        float edgeSoftness,
        float baseSoftness,
        float coverageBias,
        Vector2 weatherOffset,
        Vector2 shapeOffset,
        Vector2 detailOffset,
        float shadowStrength)
        : this(dominantFormation, coverage, erosion, density, baseHeight, thickness,
            verticalDevelopment, anvilAmount, cirrusAmount, edgeSoftness, baseSoftness,
            coverageBias, weatherOffset, shapeOffset, detailOffset, shadowStrength,
            SolCloudMath.FormationWeights(dominantFormation)) { }

    /// <summary>
    /// Full constructor carrying an explicit formation blend. Used by Lerp and ApplyWeather
    /// so a transition between two formations stays continuous instead of snapping.
    /// </summary>
    public SolCloudState(
        SolCloudFormation dominantFormation,
        float coverage,
        float erosion,
        float density,
        float baseHeight,
        float thickness,
        float verticalDevelopment,
        float anvilAmount,
        float cirrusAmount,
        float edgeSoftness,
        float baseSoftness,
        float coverageBias,
        Vector2 weatherOffset,
        Vector2 shapeOffset,
        Vector2 detailOffset,
        float shadowStrength,
        Vector4 formationBlend)
    {
        DominantFormation = dominantFormation;
        Coverage = Mathf.Clamp01(coverage);
        Erosion = Mathf.Clamp01(erosion);
        Density = Mathf.Max(0f, density);
        BaseHeight = Mathf.Max(1f, baseHeight);
        Thickness = Mathf.Max(10f, thickness);
        VerticalDevelopment = Mathf.Clamp01(verticalDevelopment);
        AnvilAmount = Mathf.Clamp01(anvilAmount);
        CirrusAmount = Mathf.Clamp01(cirrusAmount);
        EdgeSoftness = Mathf.Clamp01(edgeSoftness);
        BaseSoftness = Mathf.Clamp01(baseSoftness);
        CoverageBias = Mathf.Clamp(coverageBias, -1f, 1f);
        WeatherOffset = weatherOffset;
        ShapeOffset = shapeOffset;
        DetailOffset = detailOffset;
        ShadowStrength = Mathf.Clamp01(shadowStrength);
        FormationBlend = NormaliseFormation(formationBlend, dominantFormation);
    }

    /// <summary>
    /// Keeps the weight vector a partition of unity. A degenerate vector -- which is what
    /// default(SolCloudState) holds -- falls back to the label's own one-hot rather than
    /// dividing by zero.
    /// </summary>
    static Vector4 NormaliseFormation(Vector4 blend, SolCloudFormation fallback)
    {
        float sum = blend.x + blend.y + blend.z + blend.w;
        return sum > 0.0001f ? blend / sum : SolCloudMath.FormationWeights(fallback);
    }

    public SolCloudState WithOffsets(Vector2 weather, Vector2 shape, Vector2 detail)
        => new(DominantFormation, Coverage, Erosion, Density, BaseHeight, Thickness,
            VerticalDevelopment, AnvilAmount, CirrusAmount, EdgeSoftness, BaseSoftness,
            CoverageBias, weather, shape, detail, ShadowStrength, FormationBlend);

    /// <summary>Replaces the additive coverage nudge, keeping every other value.</summary>
    public SolCloudState WithCoverageBias(float coverageBias)
        => new(DominantFormation, Coverage, Erosion, Density, BaseHeight, Thickness,
            VerticalDevelopment, AnvilAmount, CirrusAmount, EdgeSoftness, BaseSoftness,
            coverageBias, WeatherOffset, ShapeOffset, DetailOffset, ShadowStrength,
            FormationBlend);

    public static SolCloudState FromCompatibility(float cloudiness, float erosion)
        => new(SolCloudFormation.Cumulus, cloudiness, erosion, 0.72f, 1500f, 3200f,
            0.45f, 0f, 0.18f, Vector2.zero, Vector2.zero, Vector2.zero, 0.65f);

    public static SolCloudState Lerp(in SolCloudState a, in SolCloudState b, float t)
    {
        t = Mathf.Clamp01(t);
        return new SolCloudState(
            t < 0.5f ? a.DominantFormation : b.DominantFormation,
            Mathf.Lerp(a.Coverage, b.Coverage, t),
            Mathf.Lerp(a.Erosion, b.Erosion, t),
            Mathf.Lerp(a.Density, b.Density, t),
            Mathf.Lerp(a.BaseHeight, b.BaseHeight, t),
            Mathf.Lerp(a.Thickness, b.Thickness, t),
            Mathf.Lerp(a.VerticalDevelopment, b.VerticalDevelopment, t),
            Mathf.Lerp(a.AnvilAmount, b.AnvilAmount, t),
            Mathf.Lerp(a.CirrusAmount, b.CirrusAmount, t),
            Mathf.Lerp(a.EdgeSoftness, b.EdgeSoftness, t),
            Mathf.Lerp(a.BaseSoftness, b.BaseSoftness, t),
            Mathf.Lerp(a.CoverageBias, b.CoverageBias, t),
            Vector2.LerpUnclamped(a.WeatherOffset, b.WeatherOffset, t),
            Vector2.LerpUnclamped(a.ShapeOffset, b.ShapeOffset, t),
            Vector2.LerpUnclamped(a.DetailOffset, b.DetailOffset, t),
            Mathf.Lerp(a.ShadowStrength, b.ShadowStrength, t),
            // The label above still switches at the midpoint, but the weights the shader
            // actually shapes density from move continuously. A convex combination of two
            // partitions of unity is itself one, so this needs no renormalisation.
            Vector4.Lerp(a.FormationBlend, b.FormationBlend, t));
    }

    /// <summary>
    /// Applies the weather profile as an overcast influence over the cloud cover authored
    /// on TimeOfDay. Weather cloudiness has always meant "0 = keep the authored sky" and
    /// "1 = fully overcast"; treating it as absolute coverage makes Clear weather erase
    /// the authored clouds entirely.
    /// </summary>
    public static SolCloudState ApplyWeather(in SolCloudState authored, in SolCloudState weather)
    {
        float influence = weather.Coverage;
        SolCloudState overcastTarget = new(weather.DominantFormation, 1f, weather.Erosion,
            weather.Density, weather.BaseHeight, weather.Thickness,
            weather.VerticalDevelopment, weather.AnvilAmount, weather.CirrusAmount,
            weather.EdgeSoftness, weather.BaseSoftness, weather.CoverageBias,
            weather.WeatherOffset, weather.ShapeOffset, weather.DetailOffset,
            weather.ShadowStrength, weather.FormationBlend);
        SolCloudState blended = Lerp(authored, overcastTarget, influence);
        // CoverageBias is documented as independent of how far cloudiness overrides the
        // authored sky, and the shader adds it straight into coverage. Lerping it by the
        // influence made it vanish exactly where it was most useful: at cloudiness 0 a
        // profile could not thin the authored deck at all, so no weather could produce a
        // sky clearer than whatever TimeOfDay happened to author.
        return blended.WithCoverageBias(authored.CoverageBias + weather.CoverageBias);
    }

    public bool Equals(SolCloudState other)
        => DominantFormation == other.DominantFormation
        && Approximately(Coverage, other.Coverage)
        && Approximately(Erosion, other.Erosion)
        && Approximately(Density, other.Density)
        && Approximately(BaseHeight, other.BaseHeight)
        && Approximately(Thickness, other.Thickness)
        && Approximately(VerticalDevelopment, other.VerticalDevelopment)
        && Approximately(AnvilAmount, other.AnvilAmount)
        && Approximately(CirrusAmount, other.CirrusAmount)
        && Approximately(EdgeSoftness, other.EdgeSoftness)
        && Approximately(BaseSoftness, other.BaseSoftness)
        && Approximately(CoverageBias, other.CoverageBias)
        && (WeatherOffset - other.WeatherOffset).sqrMagnitude < 0.000001f
        && (ShapeOffset - other.ShapeOffset).sqrMagnitude < 0.000001f
        && (DetailOffset - other.DetailOffset).sqrMagnitude < 0.000001f
        && Approximately(ShadowStrength, other.ShadowStrength)
        && (FormationBlend - other.FormationBlend).sqrMagnitude < 0.000001f;

    public override bool Equals(object obj) => obj is SolCloudState other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(
        HashCode.Combine(DominantFormation, Coverage, Erosion, Density),
        HashCode.Combine(BaseHeight, Thickness, VerticalDevelopment, AnvilAmount),
        HashCode.Combine(CirrusAmount, WeatherOffset, ShapeOffset, DetailOffset),
        HashCode.Combine(EdgeSoftness, BaseSoftness, CoverageBias),
        HashCode.Combine(ShadowStrength, FormationBlend));
    public static bool operator ==(SolCloudState left, SolCloudState right) => left.Equals(right);
    public static bool operator !=(SolCloudState left, SolCloudState right) => !left.Equals(right);
    static bool Approximately(float a, float b) => Mathf.Abs(a - b) <= 0.0001f;
}

/// <summary>
/// Runtime cloud authority. It deliberately owns no scene state: the renderer feature can
/// use it in Game, Scene, and reflection cameras without adding hidden components to scenes.
/// </summary>
public sealed class SolCloudController
{
    static SolCloudController _active;
    public static SolCloudController Active => _active ??= new SolCloudController();

    SolCloudQuality? _qualityOverride;
    int _lastEvaluatedFrame = -1;
    int _historyRevision;
    Vector2 _weatherOffset;
    Vector2 _shapeOffset;
    Vector2 _detailOffset;
    SolCloudState _previousState;
    bool _hasState;

    public SolCloudQuality Quality
        => _qualityOverride ?? (TimeOfDay.ResolveInstance() != null
            ? TimeOfDay.ResolveInstance().CloudQuality
            : SolCloudQuality.Medium);
    public SolCloudState CurrentState { get; private set; }
    public SolCloudState PreviousState { get; private set; }
    /// <summary>
    /// Normalised per-formation weights for the blend the current state represents. The
    /// renderer shapes density from these rather than from DominantFormation, which
    /// SolCloudState.Lerp resolves with a hard switch at the transition midpoint.
    /// </summary>
    public Vector4 FormationWeights { get; private set; } = new(1f, 0f, 0f, 0f);
    public int HistoryRevision => _historyRevision;

    public void SetQualityOverride(SolCloudQuality quality)
    {
        if (_qualityOverride == quality)
            return;
        _qualityOverride = quality;
        _historyRevision++;
    }

    public void ClearQualityOverride()
    {
        if (!_qualityOverride.HasValue)
            return;
        _qualityOverride = null;
        _historyRevision++;
    }

    /// <summary>
    /// Discard the temporal reconstruction. Public rather than internal because editor
    /// tooling has to be able to call it: step counts, scales and history weights all
    /// describe the accumulation itself, so an edit that kept the old history would blend
    /// two different reconstructions of the deck together.
    /// </summary>
    public void InvalidateHistory() => _historyRevision++;

    internal SolCloudState Evaluate(SolCloudRenderingProfile profile)
    {
        if (_lastEvaluatedFrame == Time.frameCount)
            return CurrentState;
        _lastEvaluatedFrame = Time.frameCount;

        SolWeatherState weather = SolWeatherManager.Instance != null
            ? SolWeatherManager.Instance.CurrentState
            : default;
        TimeOfDay timeOfDay = TimeOfDay.ResolveInstance();
        SolCloudState authored = timeOfDay != null
            ? timeOfDay.LegacyCloudState
            : SolCloudState.FromCompatibility(0f, weather.CloudErosion);
        if (SolWeatherManager.Instance != null && weather.Clouds.Thickness > 0f)
        {
            // ApplyWeather blends the two states by the weather's coverage, and the blend it
            // produces already carries the formation weights. Rebuilding them here from
            // DominantFormation instead is what used to snap the sky: that label resolves
            // with a hard switch at Lerp's midpoint, and the weather's own coverage is high
            // at both ends of most pairs, so nearly all of a one-hot jump landed in one frame.
            authored = SolCloudState.ApplyWeather(authored, weather.Clouds);
        }
        FormationWeights = authored.FormationBlend;

        SolEnvironmentState environment = SolEnvironmentWorld.ResolveState();
        float deltaSeconds = (float)Math.Max(0d, SolEnvironmentWorld.Active != null
            ? SolEnvironmentWorld.Active.WorldDeltaSeconds
            : Time.deltaTime);
        Vector3 velocity = environment.Wind.CloudDirection * environment.Wind.CloudSpeed;
        float advectionMultiplier = profile != null
            ? profile.cloudAdvectionMultiplier : 4f;
        Vector2 planarVelocity = new(velocity.x, velocity.z);

        long worldDay = timeOfDay != null ? timeOfDay.WorldDayIndex : 0L;
        float clockHour = timeOfDay != null ? timeOfDay.ClockHour : 0f;
        float weatherScale = profile != null ? profile.weatherScaleMetres : 85000f;
        float shapeScale = profile != null ? profile.shapeScaleMetres : 12000f;
        float detailScale = profile != null ? profile.detailScaleMetres : 1800f;
        _weatherOffset = SolCloudMath.AdvancePeriodicOffset(_weatherOffset,
            planarVelocity * (deltaSeconds * advectionMultiplier * 0.35f), weatherScale);
        _shapeOffset = SolCloudMath.AdvanceRotatedPeriodicOffset(_shapeOffset,
            planarVelocity * (deltaSeconds * advectionMultiplier), shapeScale);
        _detailOffset = SolCloudMath.AdvanceRotatedPeriodicOffset(_detailOffset,
            planarVelocity * (deltaSeconds * advectionMultiplier * 1.8f), detailScale);
        Vector2 dailyWeather = SolCloudMath.DailyCloudOffset(
            worldDay, clockHour, 401, weatherScale);
        Vector2 dailyShape = SolCloudMath.DailyCloudOffset(
            worldDay, clockHour, 811, shapeScale);
        Vector2 dailyDetail = SolCloudMath.DailyCloudOffset(
            worldDay, clockHour, 1871, detailScale);

        SolCloudState resolved = authored.WithOffsets(
            SolCloudMath.WrapOffset(_weatherOffset + dailyWeather, weatherScale),
            SolCloudMath.WrapRotatedOffset(_shapeOffset + dailyShape, shapeScale),
            SolCloudMath.WrapRotatedOffset(_detailOffset + dailyDetail, detailScale));
        if (_hasState)
        {
            // FormationBlend is deliberately absent here. This detector exists to throw away
            // temporal history across a genuine jump; the blend now moves continuously over
            // the whole transition, so watching it would only discard history during the
            // smooth motion it was made continuous for.
            bool discontinuity = Mathf.Abs(resolved.Coverage - _previousState.Coverage) > 0.2f
                || Mathf.Abs(resolved.BaseHeight - _previousState.BaseHeight) > 500f
                || Mathf.Abs(resolved.Thickness - _previousState.Thickness) > 1000f
                || SolCloudMath.RotatedPeriodicOffsetDelta(
                    resolved.ShapeOffset, _previousState.ShapeOffset, shapeScale).sqrMagnitude
                    > shapeScale * shapeScale * 0.0625f;
            if (discontinuity)
                _historyRevision++;
        }

        PreviousState = _hasState ? _previousState : resolved;
        CurrentState = resolved;
        _previousState = resolved;
        _hasState = true;
        return resolved;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() => _active = null;
}

/// <summary>CPU mirrors of cloud optical and shell math for deterministic validation.</summary>
public static class SolCloudMath
{
    const float DomainCosine = 0.819152f;
    const float DomainSine = 0.573576f;

    /// <summary>
    /// Keeps repeating texture coordinates close to the origin. This avoids float precision
    /// loss after long time-lapses while preserving exactly the same sampled field.
    /// </summary>
    public static Vector2 WrapOffset(Vector2 offset, float period)
    {
        double safePeriod = Math.Max(1d, period);
        return new Vector2(Wrap(offset.x, safePeriod), Wrap(offset.y, safePeriod));
    }

    public static Vector2 AdvancePeriodicOffset(Vector2 current, Vector2 delta, float period)
    {
        double safePeriod = Math.Max(1d, period);
        return new Vector2(
            Wrap((double)current.x + delta.x, safePeriod),
            Wrap((double)current.y + delta.y, safePeriod));
    }

    /// <summary>
    /// Rotated cloud domains need to wrap in texture space rather than world X/Z. Wrapping
    /// in world axes would make the texture jump because one world period is not one
    /// integer period after the domain rotation.
    /// </summary>
    public static Vector2 WrapRotatedOffset(Vector2 offset, float period)
        => InverseRotateDomain(WrapOffset(RotateDomain(offset), period));

    public static Vector2 AdvanceRotatedPeriodicOffset(
        Vector2 current, Vector2 delta, float period)
        => InverseRotateDomain(AdvancePeriodicOffset(
            RotateDomain(current), RotateDomain(delta), period));

    /// <summary>Shortest displacement between two offsets on a repeating texture.</summary>
    public static Vector2 PeriodicOffsetDelta(Vector2 current, Vector2 previous, float period)
    {
        float safePeriod = Mathf.Max(1f, period);
        float half = safePeriod * 0.5f;
        return new Vector2(
            Mathf.Repeat(current.x - previous.x + half, safePeriod) - half,
            Mathf.Repeat(current.y - previous.y + half, safePeriod) - half);
    }

    public static Vector2 RotatedPeriodicOffsetDelta(
        Vector2 current, Vector2 previous, float period)
        => InverseRotateDomain(PeriodicOffsetDelta(
            RotateDomain(current), RotateDomain(previous), period));

    /// <summary>
    /// Smooth deterministic phase for a date. Wind provides short-term advection while
    /// this phase prevents a calm weather preset from presenting the same cloud map on
    /// every day. The interpolation is continuous across midnight and deterministic when
    /// the calendar is rewound.
    /// </summary>
    public static Vector2 DailyCloudOffset(long worldDay, float clockHour, int salt, float scale)
    {
        float dayFraction = Mathf.Clamp01(Mathf.Repeat(clockHour, 24f) / 24f);
        float blend = dayFraction * dayFraction * (3f - 2f * dayFraction);
        Vector2 from = HashVector2(worldDay, salt);
        Vector2 to = HashVector2(worldDay + 1L, salt);
        return Vector2.LerpUnclamped(from, to, blend) * Mathf.Max(1f, scale);
    }

    /// <summary>
    /// One-hot weights for a single formation. Blending two of these tracks a weather
    /// transition continuously, where the enum itself can only switch.
    /// </summary>
    public static Vector4 FormationWeights(SolCloudFormation formation) => formation switch
    {
        SolCloudFormation.Stratus => new Vector4(0f, 1f, 0f, 0f),
        SolCloudFormation.Nimbostratus => new Vector4(0f, 0f, 1f, 0f),
        SolCloudFormation.Cumulonimbus => new Vector4(0f, 0f, 0f, 1f),
        _ => new Vector4(1f, 0f, 0f, 0f),
    };

    /// <summary>Weights four baked density fields without adding another volume fetch.</summary>
    public static Vector4 DailyVariationWeights(long worldDay, float clockHour)
    {
        float dayFraction = Mathf.Clamp01(Mathf.Repeat(clockHour, 24f) / 24f);
        float blend = dayFraction * dayFraction * (3f - 2f * dayFraction);
        Vector4 weights = Vector4.LerpUnclamped(
            VariationKey(worldDay), VariationKey(worldDay + 1L), blend);
        float sum = weights.x + weights.y + weights.z + weights.w;
        return sum > 0.0001f ? weights / sum : new Vector4(1f, 0f, 0f, 0f);
    }

    /// <summary>Reduces flat sky fill while retaining direct sun/moon highlights.</summary>
    public static float CloudAmbientMultiplier(float weatherDim, float rainIntensity,
        float shadowStrength)
    {
        // These three used to compound to roughly a third of the authored ambient in a
        // storm, and the shader's interior occlusion then took another three quarters off
        // that, which is most of why a storm deck resolved to one near-black value. Dimming
        // is redistributed rather than compounded: the overall level falls gently here and
        // the crown-to-underside spread widens in CloudAmbientBottomMultiplier, so a darker
        // sky loses light without losing contrast.
        float weather = Mathf.Lerp(1f, 0.62f, Mathf.Clamp01(weatherDim));
        float rain = Mathf.Lerp(1f, 0.88f, Mathf.Clamp01(rainIntensity));
        float formation = Mathf.Lerp(1f, 0.94f, Mathf.Clamp01(shadowStrength));
        return weather * rain * formation;
    }

    /// <summary>
    /// How dark the underside of the deck sits relative to its crown. Widening this as the
    /// sky dims is the other half of <see cref="CloudAmbientMultiplier"/>: the deck loses
    /// light overall but gains top-to-bottom separation, which is what keeps a storm reading
    /// as cloud rather than as a single flat value.
    /// </summary>
    public static float CloudAmbientBottomMultiplier(float authored, float weatherDim,
        float rainIntensity)
    {
        float darkening = Mathf.Clamp01(Mathf.Max(
            Mathf.Clamp01(weatherDim), Mathf.Clamp01(rainIntensity) * 0.8f));
        return Mathf.Clamp01(authored) * Mathf.Lerp(1f, 0.35f, darkening);
    }

    public static float DensityToTransmittance(float density, float distanceMetres,
        float extinctionPerKilometre)
        => Mathf.Exp(-Mathf.Max(0f, density) * Mathf.Max(0f, distanceMetres) * 0.001f
            * Mathf.Max(0f, extinctionPerKilometre));

    public static bool TryIntersectShell(Vector3 cameraPosition, Vector3 direction,
        Vector3 planetCenter, float planetRadius, float baseHeight, float thickness,
        float maximumDistance, out float startDistance, out float endDistance)
    {
        direction = direction.sqrMagnitude > 0.000001f ? direction.normalized : Vector3.up;
        Vector3 origin = cameraPosition - planetCenter;
        float innerRadius = Mathf.Max(1f, planetRadius + baseHeight);
        float outerRadius = innerRadius + Mathf.Max(10f, thickness);
        if (!RaySphere(origin, direction, outerRadius, out float outerNear, out float outerFar))
        {
            startDistance = endDistance = 0f;
            return false;
        }

        float cameraRadius = origin.magnitude;
        if (cameraRadius < innerRadius)
        {
            if (!RaySphere(origin, direction, innerRadius, out _, out float innerFar))
            {
                startDistance = endDistance = 0f;
                return false;
            }
            startDistance = Mathf.Max(0f, innerFar);
            endDistance = outerFar;
        }
        else if (cameraRadius < outerRadius)
        {
            startDistance = 0f;
            endDistance = outerFar;
        }
        else
        {
            startDistance = Mathf.Max(0f, outerNear);
            endDistance = outerFar;
        }

        endDistance = Mathf.Min(endDistance, Mathf.Max(0f, maximumDistance));
        return endDistance > startDistance;
    }

    static Vector4 VariationKey(long worldDay)
    {
        ulong bits = HashDay(worldDay, 0x51f15e);
        int primary = (int)(bits & 3UL);
        int secondary = (primary + 1 + (int)((bits >> 2) % 3UL)) & 3;
        float secondaryWeight = 0.12f
            + ((bits >> 12) & 0xffffUL) * (0.2f / 65535f);
        Vector4 result = Vector4.zero;
        result[primary] = 1f - secondaryWeight;
        result[secondary] = secondaryWeight;
        return result;
    }

    static Vector2 HashVector2(long worldDay, int salt)
    {
        ulong bits = HashDay(worldDay, salt);
        float x = (bits & 0xffffffUL) / 16777215f;
        float y = ((bits >> 24) & 0xffffffUL) / 16777215f;
        return new Vector2(x, y);
    }

    static ulong HashDay(long worldDay, int salt)
    {
        unchecked
        {
            ulong value = (ulong)worldDay + (ulong)(uint)salt * 0x9E3779B97F4A7C15UL;
            value ^= value >> 30;
            value *= 0xBF58476D1CE4E5B9UL;
            value ^= value >> 27;
            value *= 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return value;
        }
    }

    static float Wrap(double value, double period)
    {
        double wrapped = value % period;
        if (wrapped < 0d)
            wrapped += period;
        return (float)wrapped;
    }

    static Vector2 RotateDomain(Vector2 value) => new(
        value.x * DomainCosine - value.y * DomainSine,
        value.x * DomainSine + value.y * DomainCosine);

    static Vector2 InverseRotateDomain(Vector2 value) => new(
        value.x * DomainCosine + value.y * DomainSine,
        -value.x * DomainSine + value.y * DomainCosine);

    static bool RaySphere(Vector3 origin, Vector3 direction, float radius,
        out float nearDistance, out float farDistance)
    {
        float b = Vector3.Dot(origin, direction);
        float c = Vector3.Dot(origin, origin) - radius * radius;
        float discriminant = b * b - c;
        if (discriminant < 0f)
        {
            nearDistance = farDistance = 0f;
            return false;
        }
        float root = Mathf.Sqrt(discriminant);
        nearDistance = -b - root;
        farDistance = -b + root;
        return farDistance > 0f;
    }
}
