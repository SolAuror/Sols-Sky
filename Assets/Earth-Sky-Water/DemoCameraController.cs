using Sol.ToD;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class DemoCameraController : MonoBehaviour
{
    [Header("Fly Camera")]
    [SerializeField, Min(0f)] float moveSpeed = 10f;
    [SerializeField, Min(1f)] float fastMoveMultiplier = 4f;
    [SerializeField, Min(0f)] float lookSensitivity = 0.08f;
    [SerializeField, Range(1f, 89f)] float maxPitch = 89f;

    [Header("Time of Day")]
    [Tooltip("Optional override. If left empty, the scene object named ToDSlider is used.")]
    [SerializeField] Slider timeOfDaySlider;
    [Tooltip("Optional override. If left empty, the active scene TimeOfDay is resolved automatically.")]
    [SerializeField] TimeOfDay timeOfDay;

    float yaw;
    float pitch;
    bool cursorIsLocked;
    bool syncingSlider;
    TimeOfDay subscribedTimeOfDay;
    Slider subscribedSlider;
    float referenceRetryTimer;

    void Awake()
    {
        yaw = transform.eulerAngles.y;
        pitch = NormalizePitch(transform.eulerAngles.x);
    }

    void Start()
    {
        ResolveReferences();
        SubscribeToTimeOfDay();
        SyncSliderToTimeOfDay();
    }

    void OnEnable()
    {
        SubscribeToSlider();
    }

    void OnDisable()
    {
        if (subscribedSlider != null)
            subscribedSlider.onValueChanged.RemoveListener(OnTimeOfDaySliderChanged);
        subscribedSlider = null;

        UnsubscribeFromTimeOfDay();

        UnlockCursor();
    }

    void Update()
    {
        ResolveReferences();
        UpdateCursorLock();
        UpdateLook();
        UpdateMovement();

        // The service may not be available until the manager prefab has initialized.
        // Retry synchronization here so the slider still works with different scene load orders.
        if (timeOfDay != null && timeOfDaySlider != null && !syncingSlider)
            SyncSliderToTimeOfDay();
    }

    void ResolveReferences()
    {
        referenceRetryTimer -= Time.unscaledDeltaTime;
        if (referenceRetryTimer > 0f && (timeOfDay == null || timeOfDaySlider == null))
            return;

        bool missingReference = timeOfDay == null || timeOfDaySlider == null;
        if (timeOfDay == null)
            timeOfDay = TimeOfDay.ResolveInstance();

        if (timeOfDaySlider == null)
        {
            GameObject sliderObject = GameObject.Find("ToDSlider");
            if (sliderObject != null)
                timeOfDaySlider = sliderObject.GetComponent<Slider>();

            if (timeOfDaySlider != null)
                SubscribeToSlider();
        }

        SubscribeToSlider();
        SubscribeToTimeOfDay();

        if (missingReference && (timeOfDay == null || timeOfDaySlider == null))
            referenceRetryTimer = 0.5f;
    }

    void SubscribeToTimeOfDay()
    {
        if (subscribedTimeOfDay == timeOfDay)
            return;

        UnsubscribeFromTimeOfDay();
        if (timeOfDay == null)
            return;

        subscribedTimeOfDay = timeOfDay;
        subscribedTimeOfDay.TimeChanged += OnTimeOfDayChanged;
    }

    void UnsubscribeFromTimeOfDay()
    {
        if (subscribedTimeOfDay != null)
            subscribedTimeOfDay.TimeChanged -= OnTimeOfDayChanged;
        subscribedTimeOfDay = null;
    }

    void SubscribeToSlider()
    {
        if (subscribedSlider == timeOfDaySlider)
            return;

        if (subscribedSlider != null)
            subscribedSlider.onValueChanged.RemoveListener(OnTimeOfDaySliderChanged);

        subscribedSlider = timeOfDaySlider;
        if (subscribedSlider != null)
            subscribedSlider.onValueChanged.AddListener(OnTimeOfDaySliderChanged);
    }

    void OnTimeOfDaySliderChanged(float normalizedTime)
    {
        if (syncingSlider || timeOfDay == null)
            return;

        timeOfDay.SetNormalizedTime(normalizedTime, this, "Demo ToD slider");
    }

    void OnTimeOfDayChanged(TimeChangeResult result)
    {
        SyncSliderToTimeOfDay();
    }

    void SyncSliderToTimeOfDay()
    {
        if (timeOfDay == null || timeOfDaySlider == null)
            return;

        syncingSlider = true;
        timeOfDaySlider.SetValueWithoutNotify(timeOfDay.CurrentTime);
        syncingSlider = false;
    }

    void UpdateCursorLock()
    {
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            UnlockCursor();
            return;
        }

        bool wantsLook = Mouse.current != null && Mouse.current.rightButton.isPressed;
        if (wantsLook && !cursorIsLocked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            cursorIsLocked = true;
        }
        else if (!wantsLook && cursorIsLocked)
        {
            UnlockCursor();
        }
    }

    void UpdateLook()
    {
        if (!cursorIsLocked || Mouse.current == null)
            return;

        Vector2 mouseDelta = Mouse.current.delta.ReadValue();
        yaw += mouseDelta.x * lookSensitivity;
        pitch = Mathf.Clamp(pitch - mouseDelta.y * lookSensitivity, -maxPitch, maxPitch);
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    void UpdateMovement()
    {
        if (Keyboard.current == null)
            return;

        Vector3 input = Vector3.zero;
        if (Keyboard.current.wKey.isPressed) input.z += 1f;
        if (Keyboard.current.sKey.isPressed) input.z -= 1f;
        if (Keyboard.current.dKey.isPressed) input.x += 1f;
        if (Keyboard.current.aKey.isPressed) input.x -= 1f;
        if (Keyboard.current.eKey.isPressed) input.y += 1f;
        if (Keyboard.current.qKey.isPressed) input.y -= 1f;

        if (input.sqrMagnitude < Mathf.Epsilon)
            return;

        float speed = moveSpeed;
        if (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed)
            speed *= fastMoveMultiplier;

        transform.position += transform.TransformDirection(input.normalized) * speed * Time.deltaTime;
    }

    void UnlockCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        cursorIsLocked = false;
    }

    static float NormalizePitch(float angle)
    {
        angle %= 360f;
        if (angle > 180f)
            angle -= 360f;
        return Mathf.Clamp(angle, -89f, 89f);
    }
}
