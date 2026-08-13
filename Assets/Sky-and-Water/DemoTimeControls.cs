using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using Sol.ToD;

/// <summary>
/// Binds the serialized demo time-controls panel and routes all mutations through TimeOfDay.
/// Speed buttons are persistent rate toggles; rewind is a continuous direction toggle.
/// </summary>
public class DemoTimeControls : MonoBehaviour
{
    enum JumpUnit
    {
        Day,
        Month,
        Year
    }

    [SerializeField] TimeOfDay timeOfDay;
    [SerializeField] SolWeatherManager weatherManager;
    [SerializeField] SolRainVfxController rainController;
    [SerializeField] SolAtmosphereController atmosphereController;
    [SerializeField, Min(1f)] float normalSpeedFallback = 1f;

    static readonly Color ButtonColor = new(0.18f, 0.22f, 0.32f, 0.94f);
    static readonly Color SpeedSelectedColor = new(0.18f, 0.56f, 0.4f, 1f);
    static readonly Color RewindColor = new(0.42f, 0.18f, 0.2f, 0.96f);
    static readonly Color RewindOnColor = new(0.76f, 0.26f, 0.2f, 1f);

    RectTransform panel;
    Button rewindButton;
    TMP_Text rewindLabel;
    Image rewindImage;
    readonly List<Button> speedButtons = new();
    readonly List<Button> amountButtons = new();
    readonly List<Button> directionButtons = new();
    readonly List<Button> unitButtons = new();
    readonly Dictionary<Button, Color> defaultButtonColors = new();
    RectTransform speedRow;
    RectTransform jumpRow;
    RectTransform environmentRow;
    TMP_Text environmentStatus;
    float normalTimeScale;
    float selectedSpeed = 1f;
    Button selectedSpeedButton;
    int selectedAmount = 1;
    bool jumpForward = true;
    JumpUnit selectedJumpUnit = JumpUnit.Day;
    Button selectedAmountButton;
    Button selectedDirectionButton;
    Button selectedUnitButton;
    Color rewindDefaultColor;
    bool rewindMode;
    float environmentReferenceRetryTimer;
    float environmentStatusTimer;
    string lastEnvironmentStatus;

    void Start()
    {
        timeOfDay = timeOfDay != null ? timeOfDay : TimeOfDay.ResolveInstance();
        ResolveEnvironmentReferences();
        normalTimeScale = timeOfDay != null && timeOfDay.TimeScale > 0f
            ? timeOfDay.TimeScale
            : normalSpeedFallback;

        BindExistingControls();
    }

    void Update()
    {
        if (timeOfDay == null)
            timeOfDay = TimeOfDay.ResolveInstance();

        ResolveEnvironmentReferences();
        environmentStatusTimer -= Time.unscaledDeltaTime;
        if (environmentStatusTimer <= 0f)
        {
            environmentStatusTimer = 0.1f;
            RefreshEnvironmentStatus();
        }

        if (timeOfDay == null || !rewindMode || timeOfDay.Paused)
            return;

        float cycleSeconds = Mathf.Max(0.1f, timeOfDay.CycleDuration * 60f);
        float hoursThisFrame = 24f / cycleSeconds * normalTimeScale * selectedSpeed * Time.deltaTime;
        timeOfDay.RewindHours(hoursThisFrame, this, "Demo rewind toggle");
    }

    void BindExistingControls()
    {
        GameObject panelObject = GameObject.Find("TimeControlsPanel");
        if (panelObject == null)
        {
            Debug.LogWarning("[DemoTimeControls] Could not find the serialized TimeControlsPanel.", this);
            return;
        }

        panel = panelObject.GetComponent<RectTransform>();
        speedRow = FindRow("SpeedControlsRow");
        jumpRow = FindRow("CalendarStepRow");
        environmentRow = FindRow("EnvironmentControlsRow");

        BindSpeedButton("1X", 1f);
        BindSpeedButton("10X", 10f);
        BindSpeedButton("100X", 100f);
        BindButton(speedRow, "Pause", PauseWorld);
        BindButton(speedRow, "Play", PlayWorld);

        rewindButton = BindButton(speedRow, "Rewind", ToggleRewind);
        if (rewindButton != null)
        {
            rewindImage = rewindButton.GetComponent<Image>();
            rewindLabel = rewindButton.GetComponentInChildren<TMP_Text>();
            rewindDefaultColor = rewindImage != null ? rewindImage.color : RewindColor;
        }

        BindAmountButton("1", 1);
        BindAmountButton("5", 5);
        BindAmountButton("10", 10);
        BindDirectionButton("+", true);
        BindDirectionButton("-", false);
        BindUnitButton("Day", JumpUnit.Day);
        BindUnitButton("Month", JumpUnit.Month);
        BindUnitButton("Year", JumpUnit.Year);
        BindButton(jumpRow, "Step", ApplyCalendarStep);

        BindButton(environmentRow, "Weather", NextWeather);
        BindButton(environmentRow, "Rain", CycleRainExposure);
        BindButton(environmentRow, "Cloud", CycleCloudQuality);
        BindButton(environmentRow, "Fog", CycleFogQuality);
        Button statusButton = FindButton(environmentRow, "EnvironmentStatus");
        environmentStatus = statusButton != null
            ? statusButton.GetComponentInChildren<TMP_Text>()
            : FindText(environmentRow, "EnvironmentStatus");

        UpdateSpeedVisuals();
        UpdateJumpVisuals();
        UpdateRewindVisuals();
        RefreshEnvironmentStatus();
    }

    RectTransform FindRow(string rowName)
    {
        return panel != null ? panel.Find(rowName) as RectTransform : null;
    }

    Button FindButton(Transform row, string buttonName)
    {
        return row != null ? row.Find(buttonName)?.GetComponent<Button>() : null;
    }

    TMP_Text FindText(Transform row, string objectName)
    {
        return row != null ? row.Find(objectName)?.GetComponentInChildren<TMP_Text>() : null;
    }

    Button BindButton(Transform row, string buttonName, UnityEngine.Events.UnityAction action)
    {
        Button button = FindButton(row, buttonName);
        if (button == null)
            return null;

        RememberButtonColor(button);
        button.onClick.AddListener(action);
        return button;
    }

    void BindSpeedButton(string buttonName, float speed)
    {
        Button button = FindButton(speedRow, buttonName);
        if (button == null)
            return;

        RememberButtonColor(button);
        speedButtons.Add(button);
        button.onClick.AddListener(() => SelectSpeed(speed, button));

        if (Mathf.Approximately(speed, selectedSpeed))
            selectedSpeedButton = button;
    }

    void BindAmountButton(string buttonName, int amount)
    {
        Button button = FindButton(jumpRow, buttonName);
        if (button == null)
            return;

        RememberButtonColor(button);
        amountButtons.Add(button);
        button.onClick.AddListener(() =>
        {
            selectedAmount = amount;
            selectedAmountButton = button;
            UpdateJumpVisuals();
        });

        if (amount == selectedAmount)
            selectedAmountButton = button;
    }

    void BindDirectionButton(string buttonName, bool forward)
    {
        Button button = FindButton(jumpRow, buttonName);
        if (button == null)
            return;

        RememberButtonColor(button);
        directionButtons.Add(button);
        button.onClick.AddListener(() =>
        {
            jumpForward = forward;
            selectedDirectionButton = button;
            UpdateJumpVisuals();
        });

        if (forward == jumpForward)
            selectedDirectionButton = button;
    }

    void BindUnitButton(string buttonName, JumpUnit unit)
    {
        Button button = FindButton(jumpRow, buttonName);
        if (button == null)
            return;

        RememberButtonColor(button);
        unitButtons.Add(button);
        button.onClick.AddListener(() =>
        {
            selectedJumpUnit = unit;
            selectedUnitButton = button;
            UpdateJumpVisuals();
        });

        if (unit == selectedJumpUnit)
            selectedUnitButton = button;
    }

    void RememberButtonColor(Button button)
    {
        if (button != null && !defaultButtonColors.ContainsKey(button))
        {
            Image image = button.GetComponent<Image>();
            defaultButtonColors[button] = image != null ? image.color : ButtonColor;
        }
    }

    void ResolveEnvironmentReferences()
    {
        environmentReferenceRetryTimer -= Time.unscaledDeltaTime;
        if (environmentReferenceRetryTimer > 0f &&
            (weatherManager == null || rainController == null || atmosphereController == null))
            return;

        bool missingReference = weatherManager == null || rainController == null || atmosphereController == null;
        weatherManager ??= SolWeatherManager.Instance;
        rainController ??= FindFirstObjectByType<SolRainVfxController>();
        atmosphereController ??= SolAtmosphereController.Active;

        if (missingReference &&
            (weatherManager == null || rainController == null || atmosphereController == null))
            environmentReferenceRetryTimer = 0.5f;
    }

    void NextWeather()
    {
        ResolveEnvironmentReferences();
        weatherManager?.NextWeather();
    }

    void CycleRainExposure()
    {
        ResolveEnvironmentReferences();
        if (rainController == null) return;
        float next = rainController.RainExposure > 0.75f ? 0.5f
            : rainController.RainExposure > 0.25f ? 0f : 1f;
        rainController.SetRainExposure(next);
    }

    void CycleCloudQuality()
    {
        if (timeOfDay == null) return;
        timeOfDay.CloudQuality = (SolCloudQuality)(((int)timeOfDay.CloudQuality + 1) % 3);
    }

    void CycleFogQuality()
    {
        ResolveEnvironmentReferences();
        if (atmosphereController == null) return;
        atmosphereController.SetQuality((SolAtmosphereQuality)(((int)atmosphereController.Quality + 1) % 3));
    }

    void RefreshEnvironmentStatus()
    {
        if (environmentStatus == null) return;
        string weather = weatherManager != null && weatherManager.TargetProfile != null
            ? weatherManager.TargetProfile.name
            : "None";
        float rain = rainController != null ? rainController.RainExposure : 0f;
        string cloud = timeOfDay != null ? timeOfDay.CloudQuality.ToString()[0].ToString() : "-";
        string fog = atmosphereController != null ? atmosphereController.Quality.ToString()[0].ToString() : "-";
        string season = timeOfDay != null && timeOfDay.Calendar != null
            ? timeOfDay.Calendar.CurrentSeason switch
            {
                SolSeason.Spring => "Spr",
                SolSeason.Summer => "Sum",
                SolSeason.Autumn => "Aut",
                _ => "Win",
            }
            : "---";
        float dailyFog = weatherManager != null ? weatherManager.CurrentDailyFog : 0f;
        float mist = weatherManager != null ? weatherManager.CurrentState.Mistiness : 0f;
        float turbulence = weatherManager != null ? weatherManager.CurrentState.WaterTurbulence : 0f;
        float lunarTide = timeOfDay != null ? timeOfDay.LunarTideFactor : 0f;
        string status = $"{weather} {season} R:{rain:0.0} F:{dailyFog:0.00} M:{mist:0.00} T:{turbulence:0.00} L:{lunarTide:0.00} C:{cloud}/{fog}";
        if (status == lastEnvironmentStatus)
            return;

        lastEnvironmentStatus = status;
        environmentStatus.text = status;
    }

    void SelectSpeed(float speed, Button button)
    {
        selectedSpeed = speed;
        selectedSpeedButton = button;
        UpdateSpeedVisuals();

        if (timeOfDay == null)
        {
            timeOfDay = TimeOfDay.ResolveInstance();
            if (timeOfDay == null)
                return;
        }

        if (!rewindMode)
            timeOfDay.SetTimeScale(normalTimeScale * selectedSpeed);
        else
            timeOfDay.SetTimeScale(0f);
    }

    void UpdateSpeedVisuals()
    {
        foreach (Button button in speedButtons)
        {
            Image image = button.GetComponent<Image>();
            if (image != null)
                image.color = button == selectedSpeedButton ? SpeedSelectedColor : GetDefaultButtonColor(button);
        }
    }

    void UpdateJumpVisuals()
    {
        SetSelectedButtonColors(amountButtons, selectedAmountButton);
        SetSelectedButtonColors(directionButtons, selectedDirectionButton);
        SetSelectedButtonColors(unitButtons, selectedUnitButton);
    }

    void SetSelectedButtonColors(List<Button> buttons, Button selectedButton)
    {
        foreach (Button button in buttons)
        {
            Image image = button.GetComponent<Image>();
            if (image != null)
                image.color = button == selectedButton ? SpeedSelectedColor : GetDefaultButtonColor(button);
        }
    }

    Color GetDefaultButtonColor(Button button)
    {
        return defaultButtonColors.TryGetValue(button, out Color color) ? color : ButtonColor;
    }

    void ApplyCalendarStep()
    {
        if (timeOfDay == null)
            timeOfDay = TimeOfDay.ResolveInstance();

        if (timeOfDay == null)
            return;

        Calendar calendar = timeOfDay.Calendar;
        if (calendar == null)
            return;

        int days = selectedJumpUnit switch
        {
            JumpUnit.Day => selectedAmount,
            JumpUnit.Month => GetMonthDayCount(calendar, selectedAmount, jumpForward),
            JumpUnit.Year => calendar.DaysPerYear * selectedAmount,
            _ => selectedAmount
        };

        float hours = days * 24f;
        if (jumpForward)
            timeOfDay.AdvanceHours(hours, this, "Demo calendar step forward");
        else
            timeOfDay.RewindHours(hours, this, "Demo calendar step backward");
    }

    static int GetMonthDayCount(Calendar calendar, int monthCount, bool forward)
    {
        int days = 0;
        int month = calendar.Month;

        for (int i = 0; i < monthCount; i++)
        {
            if (forward)
            {
                days += calendar.GetDaysInMonth(month);
                month++;
                if (month > calendar.MonthsPerYear)
                    month = 1;
            }
            else
            {
                month--;
                if (month < 1)
                    month = calendar.MonthsPerYear;
                days += calendar.GetDaysInMonth(month);
            }
        }

        return days;
    }

    void PauseWorld()
    {
        if (timeOfDay != null)
            timeOfDay.SetPaused(true);
    }

    void PlayWorld()
    {
        if (timeOfDay != null)
            timeOfDay.SetPaused(false);
    }

    void ToggleRewind()
    {
        rewindMode = !rewindMode;
        if (timeOfDay != null)
            timeOfDay.SetTimeScale(rewindMode ? 0f : normalTimeScale * selectedSpeed);

        UpdateRewindVisuals();
    }

    void UpdateRewindVisuals()
    {
        if (rewindButton == null)
            return;

        if (rewindImage != null)
            rewindImage.color = rewindMode ? RewindOnColor : rewindDefaultColor;
        if (rewindLabel != null)
            rewindLabel.text = rewindMode ? "Rewind ON" : "Rewind OFF";
    }
}
