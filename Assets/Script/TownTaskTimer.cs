using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// The task countdown in the life-size town: a timer display floats above the clock tower (always facing the user)
// and counts down, while the train runs one full lap of the circular track from the station, arriving back at the
// station exactly when the timer reaches 00:00.
public class TownTaskTimer : MonoBehaviour
{
    [Header("Clock tower display")]
    [SerializeField] private Transform clockTower;
    [Tooltip("Background behind the digits (e.g. TimerBkg).")]
    [SerializeField] private Sprite background;
    [Tooltip("Optional text to copy the font, color and style from (e.g. the Set Timer panel's Time text).")]
    [SerializeField] private TMP_Text styleSource;
    [Tooltip("Gap between the tower's top and the display's bottom, in meters.")]
    [SerializeField] private float gapAboveTower = 0.05f;
    [Tooltip("Display width per meter of distance from the user, so it reads the same at any town scale.")]
    [SerializeField] private float widthPerMeter = 0.22f;
    [SerializeField] private float flipDuration = 0.2f;

    [Header("Train")]
    [SerializeField] private Transform train;
    [Tooltip("Front of the train; sets the direction it drives.")]
    [SerializeField] private Transform locomotive;
    [Tooltip("A renderer centered on the circular track (e.g. Rail_In); the train circles its center.")]
    [SerializeField] private Renderer track;
    [Tooltip("Seconds to speed up at the start and slow down at the end of the lap.")]
    [SerializeField] private float trainEaseSeconds = 2f;
    [Tooltip("The train waits here (moved along the track to line up with it) and starts/ends its lap here " +
             "(e.g. the station's Sta_Platform).")]
    [SerializeField] private Transform station;
    [Tooltip("Engine smoke; puffs faster while the train drives.")]
    [SerializeField] private SmokePuffs smoke;
    [Tooltip("Building constructed while the timer runs (paused with the timer; completed by the session's 'Yes').")]
    [SerializeField] private ConstructionProject construction;
    [Tooltip("Complete the building as soon as the countdown ends. Off: something else completes it " +
             "(e.g. FocusSession, after the user confirms they finished the task).")]
    [SerializeField] private bool completeConstructionOnFinish = false;
    [Tooltip("Parks the train at the station when the bell shows the town.")]
    [SerializeField] private OnboardingManager onboarding;

    [Tooltip("Called when the countdown reaches zero.")]
    public UnityEvent onFinished;

    public bool IsRunning { get; private set; }
    /// <summary>True while a running countdown is on hold (e.g. a break): train and construction stop too.</summary>
    public bool IsPaused { get; private set; }
    /// <summary>Seconds of countdown done so far (not counting pauses).</summary>
    public float ElapsedSeconds => elapsed;
    public float RemainingSeconds => IsRunning ? Mathf.Max(0f, duration - elapsed) : 0f;

    private const float CanvasWidth = 952f; // TimerBkg's pixel size, used as the canvas's units
    private const float CanvasHeight = 420f;

    private Transform head;
    private Canvas display;
    private TMP_Text digits;
    private Coroutine flipRoutine;
    private int shownSeconds = -1;
    private float displayAnimationScale = 1f; // pop-in / pulse, on top of the distance-based size

    private float duration;
    private float elapsed;
    private bool trainStored;
    private Vector3 trainStartPosition;
    private Quaternion trainStartRotation;
    private Vector3 lapCenter;
    private Vector3 lapAxis;
    private float lapDirection;

    private void Awake()
    {
        var rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();
    }

    private void OnEnable()
    {
        if (onboarding != null)
            onboarding.Confirmed += ParkAtStation;
    }

    private void OnDisable()
    {
        if (onboarding != null)
            onboarding.Confirmed -= ParkAtStation;
    }

    /// <summary>Shows the timer on the clock tower and starts the countdown and the train's lap.</summary>
    public void StartCountdown(float seconds)
    {
        if (seconds <= 0f)
            return;

        duration = seconds;
        elapsed = 0f;
        IsRunning = true;
        IsPaused = false;

        BuildDisplay();
        display.gameObject.SetActive(true);
        shownSeconds = -1;
        SetDigits(Mathf.CeilToInt(seconds), false);
        StartCoroutine(PopIn(display.transform));

        PrepareTrain();
        if (smoke != null)
            smoke.Moving = true;
        if (construction != null)
            construction.StartBuilding();
        Debug.Log($"[TownTimer] Countdown started: {seconds:F0} s");
    }

    private void Update()
    {
        if (!IsRunning || IsPaused)
            return;

        elapsed += Time.deltaTime;
        float remaining = duration - elapsed;
        if (remaining <= 0f)
        {
            Finish();
            return;
        }

        SetDigits(Mathf.CeilToInt(remaining), true);
        PlaceTrain(LapProgress(elapsed));
    }

    /// <summary>Holds the countdown: the timer, the train and the construction all stop.</summary>
    public void Pause()
    {
        if (!IsRunning || IsPaused)
            return;
        IsPaused = true;
        if (smoke != null)
            smoke.Moving = false;
        if (construction != null)
            construction.Pause();
        Debug.Log("[TownTimer] Paused");
    }

    /// <summary>Continues a paused countdown.</summary>
    public void Resume()
    {
        if (!IsRunning || !IsPaused)
            return;
        IsPaused = false;
        if (smoke != null)
            smoke.Moving = true;
        if (construction != null)
            construction.Resume();
        Debug.Log("[TownTimer] Resumed");
    }

    private void LateUpdate()
    {
        if (display != null && display.gameObject.activeSelf)
            PlaceDisplay();
    }

    private void Finish()
    {
        IsRunning = false;
        IsPaused = false;
        SetDigits(0, true);
        PlaceTrain(1f);
        if (smoke != null)
            smoke.Moving = false;
        if (construction != null)
        {
            if (completeConstructionOnFinish)
                construction.Complete();
            else
                construction.Pause(); // work stops; the building is finished later
        }
        if (display != null)
            StartCoroutine(Pulse(display.transform));
        Debug.Log("[TownTimer] Countdown finished");
        onFinished?.Invoke();
    }

    // ---------- Train ----------

    private void PrepareTrain()
    {
        if (train == null || track == null)
            return;

        // The lap starts at the station (also after an earlier lap).
        if (trainStored)
        {
            train.localPosition = trainStartPosition;
            train.localRotation = trainStartRotation;
        }
        ParkAtStation();
        trainStored = true;
        trainStartPosition = train.localPosition;
        trainStartRotation = train.localRotation;

        lapCenter = track.bounds.center;
        lapAxis = track.transform.up;

        // Drive toward the locomotive: compare it with the rest of the train, and pick the matching turn direction.
        lapDirection = 1f;
        if (locomotive != null)
        {
            Vector3 middle = TrainMiddle();
            Vector3 radius = locomotive.position - lapCenter;
            // Cross(axis, r) is the direction a point moves under a positive RotateAround.
            Vector3 positiveTurn = Vector3.Cross(lapAxis, radius);
            lapDirection = Vector3.Dot(positiveTurn, locomotive.position - middle) >= 0f ? 1f : -1f;
        }
    }

    /// <summary>Slides the train along the track so its middle lines up with the station.</summary>
    [ContextMenu("Park Train At Station")]
    public void ParkAtStation()
    {
        if (train == null || track == null || station == null || IsRunning || !train.gameObject.activeInHierarchy)
            return;

        Vector3 center = track.bounds.center;
        Vector3 axis = track.transform.up;
        Vector3 toTrain = Vector3.ProjectOnPlane(TrainMiddle() - center, axis);
        Vector3 toStation = Vector3.ProjectOnPlane(StationPoint() - center, axis);
        if (toTrain.sqrMagnitude < 1e-6f || toStation.sqrMagnitude < 1e-6f)
            return;

        train.RotateAround(center, axis, Vector3.SignedAngle(toTrain, toStation, axis));
    }

    private Vector3 TrainMiddle()
    {
        Vector3 middle = Vector3.zero;
        int count = 0;
        foreach (Transform car in train)
        {
            middle += car.position;
            count++;
        }
        return count > 0 ? middle / count : train.position;
    }

    private Vector3 StationPoint()
    {
        var renderers = station.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return station.position;
        Bounds b = renderers[0].bounds;
        foreach (var r in renderers)
            b.Encapsulate(r.bounds);
        return b.center;
    }

    private void PlaceTrain(float lap)
    {
        if (train == null || track == null)
            return;

        train.localPosition = trainStartPosition;
        train.localRotation = trainStartRotation;
        if (lap < 1f)
            train.RotateAround(lapCenter, lapAxis, 360f * lap * lapDirection);
    }

    // Fraction of the lap done after `elapsed` seconds: speeds up, cruises, then slows to a stop at exactly one lap.
    private float LapProgress(float elapsed)
    {
        float ease = Mathf.Min(trainEaseSeconds, duration * 0.5f);
        if (ease <= 0f)
            return Mathf.Clamp01(elapsed / duration);

        float cruiseSpeed = 1f / (duration - ease);
        if (elapsed < ease)
            return 0.5f * cruiseSpeed * elapsed * elapsed / ease;
        if (elapsed < duration - ease)
            return cruiseSpeed * (elapsed - ease * 0.5f);
        float left = duration - elapsed;
        return Mathf.Clamp01(1f - 0.5f * cruiseSpeed * left * left / ease);
    }

    // ---------- Display ----------

    private void BuildDisplay()
    {
        if (display != null)
            return;

        var go = new GameObject("ClockTowerTimer", typeof(RectTransform), typeof(Canvas));
        go.layer = LayerMask.NameToLayer("UI") >= 0 ? LayerMask.NameToLayer("UI") : 0;
        display = go.GetComponent<Canvas>();
        display.renderMode = RenderMode.WorldSpace;
        var rt = (RectTransform)go.transform;
        rt.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);
        rt.pivot = new Vector2(0.5f, 0f); // bottom-center sits on the tower

        var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.layer = go.layer;
        var brt = (RectTransform)bg.transform;
        brt.SetParent(rt, false);
        brt.anchorMin = Vector2.zero;
        brt.anchorMax = Vector2.one;
        brt.offsetMin = brt.offsetMax = Vector2.zero;
        var image = bg.GetComponent<Image>();
        image.sprite = background;
        image.raycastTarget = false;

        var text = new GameObject("Digits", typeof(RectTransform), typeof(TextMeshProUGUI));
        text.layer = go.layer;
        var trt = (RectTransform)text.transform;
        trt.SetParent(rt, false);
        // Under the background's "min" / "sec" labels, like the Set Timer panel.
        trt.anchorMin = new Vector2(0f, 0f);
        trt.anchorMax = new Vector2(1f, 0.62f);
        trt.offsetMin = trt.offsetMax = Vector2.zero;
        digits = text.GetComponent<TextMeshProUGUI>();
        if (styleSource != null)
        {
            digits.font = styleSource.font;
            digits.fontSharedMaterial = styleSource.fontSharedMaterial;
            digits.fontStyle = styleSource.fontStyle;
            digits.color = styleSource.color;
        }
        else
        {
            digits.fontStyle = FontStyles.Bold;
            digits.color = new Color(0.1f, 0.12f, 0.15f);
        }
        digits.fontSize = 150f;
        digits.alignment = TextAlignmentOptions.Center;
        digits.raycastTarget = false;

        go.SetActive(false);
    }

    private void PlaceDisplay()
    {
        if (clockTower == null)
            return;

        var renderers = clockTower.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return;
        Bounds b = renderers[0].bounds;
        foreach (var r in renderers)
            b.Encapsulate(r.bounds);

        var t = display.transform;
        t.position = new Vector3(b.center.x, b.max.y + gapAboveTower, b.center.z);

        // Face the user, upright (yaw only). A canvas is read from its -Z side.
        Vector3 away = t.position - head.position;
        away.y = 0f;
        if (away.sqrMagnitude > 0.0001f)
            t.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);

        float width = Mathf.Max(0.05f, away.magnitude * widthPerMeter);
        float scale = width / CanvasWidth;
        // Keep any pop/pulse animation, which multiplies this base scale.
        t.localScale = Vector3.one * scale * displayAnimationScale;
    }

    private void SetDigits(int seconds, bool animate)
    {
        if (seconds == shownSeconds || digits == null)
            return;
        string text = $"{seconds / 60:00}:{seconds % 60:00}";
        shownSeconds = seconds;

        if (animate && isActiveAndEnabled && flipDuration > 0f)
        {
            if (flipRoutine != null)
                StopCoroutine(flipRoutine);
            flipRoutine = StartCoroutine(Flip(text));
        }
        else
        {
            digits.text = text;
        }
    }

    // Same flip-clock flip as the Set Timer panel: out to edge-on, swap, back in with a small overshoot.
    private IEnumerator Flip(string to)
    {
        var rt = digits.rectTransform;
        float half = flipDuration * 0.5f;
        for (float t = 0f; t < 1f; t += Time.deltaTime / half)
        {
            rt.localRotation = Quaternion.Euler(90f * t * t, 0f, 0f);
            yield return null;
        }
        digits.text = to;
        for (float t = 0f; t < 1f; t += Time.deltaTime / half)
        {
            float u = t - 1f;
            float back = 1f + 2.6f * u * u * u + 1.6f * u * u;
            rt.localRotation = Quaternion.Euler(-90f * (1f - back), 0f, 0f);
            yield return null;
        }
        rt.localRotation = Quaternion.identity;
        flipRoutine = null;
    }

    private IEnumerator PopIn(Transform t)
    {
        for (float x = 0f; x < 1f; x += Time.deltaTime / 0.35f)
        {
            float u = x - 1f;
            displayAnimationScale = 1f + 2.70158f * u * u * u + 1.70158f * u * u; // ease-out-back
            yield return null;
        }
        displayAnimationScale = 1f;
    }

    private IEnumerator Pulse(Transform t)
    {
        for (int i = 0; i < 3; i++)
        {
            for (float x = 0f; x < 1f; x += Time.deltaTime / 0.4f)
            {
                displayAnimationScale = 1f + 0.12f * Mathf.Sin(Mathf.PI * x);
                yield return null;
            }
        }
        displayAnimationScale = 1f;
    }
}
