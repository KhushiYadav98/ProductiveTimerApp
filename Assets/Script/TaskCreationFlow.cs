using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// Task creation UI shown after the bell: the canvas appears in front of the user with the "Create task" panel;
// its button switches to the "Name task" panel, where poking the input field opens an in-world poke keyboard.
// OK there opens the "Set timer" panel (starting at 00:00), and its OK creates the task.
public class TaskCreationFlow : MonoBehaviour
{
    [SerializeField] private OnboardingManager onboarding;
    [SerializeField] private Canvas canvas;

    [Header("Panels")]
    [SerializeField] private GameObject createTaskPanel;
    [SerializeField] private GameObject nameTaskPanel;
    [SerializeField] private GameObject settingTimerPanel;

    [Header("New task (from the main menu)")]
    [Tooltip("'Do you want to connect your screen?' panel shown first for a new task.")]
    [SerializeField] private GameObject selectScreenTaskPanel;
    [SerializeField] private Button selectScreenYesButton;
    [SerializeField] private Button selectScreenNoButton;

    [Header("Controls")]
    [SerializeField] private Button createTaskButton;
    [SerializeField] private Button okButton;
    [SerializeField] private TMP_InputField titleInput;
    [Tooltip("In-world poke keyboard shown when the title input is touched.")]
    [SerializeField] private PokeKeyboard keyboard;
    [SerializeField] private TimerPicker timerPicker;
    [SerializeField] private Button timerOkButton;
    [Tooltip("Countdown on the clock tower (and the train's lap), started by the timer's OK.")]
    [SerializeField] private TownTaskTimer townTimer;
    [Tooltip("Optional logo intro (logo pop-up + clouds) played before the 'Create task' panel appears.")]
    [SerializeField] private LogoIntro logoIntro;
    [Tooltip("Runs the focus session (focus message, break, 'did you finish?') once the timer is set. " +
             "Empty: the timer's OK just starts the countdown.")]
    [SerializeField] private FocusSession session;

    [Header("Placement")]
    [Tooltip("Distance in front of the head, in meters. Keep it within arm's reach so the buttons can be poked.")]
    [SerializeField] private float distanceFromHead = 0.5f;
    [Tooltip("Height of the panel's center relative to the head, in meters.")]
    [SerializeField] private float heightOffset = -0.15f;
    [Tooltip("World width of the panels when shown, in meters (the canvas is scaled to fit). 0 keeps the scene scale.")]
    [SerializeField] private float panelWidth = 0.6f;

    [Header("Events")]
    [Tooltip("Called with the task title when OK is pressed with a non-empty title.")]
    public UnityEvent<string> onTaskTitled;
    [Tooltip("Called with the task title and its time in minutes when the timer's OK is pressed.")]
    public UnityEvent<string, int> onTaskCreated;

    public string TaskTitle { get; private set; }

    private Transform head;
    private readonly System.Collections.Generic.List<GameObject> extraPanels = new System.Collections.Generic.List<GameObject>();
    private readonly System.Collections.Generic.Dictionary<Transform, Vector3> restScales =
        new System.Collections.Generic.Dictionary<Transform, Vector3>();
    private Coroutine popRoutine;

    private void Awake()
    {
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();

        var rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;

        if (createTaskButton != null)
            createTaskButton.onClick.AddListener(ShowNamePanel);
        if (selectScreenNoButton != null)
            selectScreenNoButton.onClick.AddListener(ShowNamePanel);
        if (selectScreenYesButton != null)
            selectScreenYesButton.onClick.AddListener(() => Debug.Log("[TaskCreation] Screen connection not set up yet."));
        RegisterPanels(selectScreenTaskPanel);
        if (okButton != null)
            okButton.onClick.AddListener(SubmitTitle);
        if (timerOkButton != null)
            timerOkButton.onClick.AddListener(SubmitTimer);
        if (timerPicker != null && timerOkButton != null)
            // No zero-minute tasks: OK only works once a time is set.
            timerPicker.onMinutesChanged.AddListener(m => timerOkButton.interactable = m > 0);
        if (titleInput != null && keyboard != null)
        {
            // The poke keyboard replaces the system keyboard (which never shows in the Editor over Link).
            titleInput.shouldHideSoftKeyboard = true;
            titleInput.onSelect.AddListener(_ => keyboard.Show(titleInput));
        }
        if (keyboard != null)
            keyboard.Hide();

        if (canvas != null)
            canvas.gameObject.SetActive(false);
    }

    private void OnEnable()
    {
        if (onboarding != null)
            onboarding.TownRevealed += Show;
    }

    private void OnDisable()
    {
        if (onboarding != null)
            onboarding.TownRevealed -= Show;
    }

    /// <summary>Places the canvas in front of the user and shows the "Create task" panel.</summary>
    public void Show()
    {
        if (canvas == null)
            return;

        canvas.gameObject.SetActive(true);
        PlaceInFrontOfUser();

        if (logoIntro != null)
        {
            // Logo first, then the panel pops in.
            SetPanel(null);
            logoIntro.Play(head, createTaskPanel != null ? createTaskPanel.transform : null, () => SetPanel(createTaskPanel));
        }
        else
        {
            SetPanel(createTaskPanel);
        }
    }

    /// <summary>Starts another task: "connect your screen?" first; No goes on to naming the task and setting its time.</summary>
    public void StartNewTask()
    {
        if (selectScreenTaskPanel != null)
            ShowOnly(selectScreenTaskPanel);
        else
        {
            ShowOnly(nameTaskPanel);
            ShowNamePanel();
        }
    }

    public void ShowNamePanel()
    {
        if (titleInput != null)
            titleInput.text = string.Empty;
        if (keyboard != null)
            keyboard.Hide();
        SetPanel(nameTaskPanel);
    }

    private void SubmitTitle()
    {
        string title = titleInput != null ? titleInput.text.Trim() : string.Empty;
        if (title.Length == 0)
        {
            Debug.Log("[TaskCreation] OK pressed with an empty title; ignoring.");
            return;
        }

        if (keyboard != null)
            keyboard.Hide();
        TaskTitle = title;
        Debug.Log($"[TaskCreation] Task titled: {title}");
        onTaskTitled?.Invoke(title);
        ShowTimerPanel();
    }

    public void ShowTimerPanel()
    {
        SetPanel(settingTimerPanel);
        // The session decides which times can be picked for this task (e.g. 25 or 50 minutes).
        if (session != null)
            session.ConfigurePicker(timerPicker);
        else if (timerPicker != null)
            timerPicker.ResetTo(0);
    }

    private void SubmitTimer()
    {
        int minutes = timerPicker != null ? timerPicker.Minutes : 0;
        if (minutes <= 0)
            return;

        Debug.Log($"[TaskCreation] Task created: \"{TaskTitle}\" for {minutes} min");
        onTaskCreated?.Invoke(TaskTitle, minutes);

        // The task is set: put the UI away and start the countdown on the clock tower.
        if (keyboard != null)
            keyboard.Hide();
        if (session != null)
        {
            session.Begin(minutes * 60f);
            return;
        }
        canvas.gameObject.SetActive(false);
        if (townTimer != null)
            townTimer.StartCountdown(minutes * 60f);
    }

    /// <summary>Lets other flows (e.g. FocusSession) show their own panels on this canvas; they're hidden with the rest.</summary>
    public void RegisterPanels(params GameObject[] panels)
    {
        foreach (var p in panels)
            if (p != null && !extraPanels.Contains(p))
            {
                extraPanels.Add(p);
                p.SetActive(false);
            }
    }

    /// <summary>Shows only <paramref name="panel"/>, in front of the user, with a small pop.</summary>
    public void ShowOnly(GameObject panel)
    {
        if (canvas == null)
            return;
        if (keyboard != null)
            keyboard.Hide();
        canvas.gameObject.SetActive(true);
        SetPanel(panel);
        PlaceInFrontOfUser();
        if (panel != null)
        {
            if (popRoutine != null)
                StopCoroutine(popRoutine);
            popRoutine = StartCoroutine(Pop(panel.transform));
        }
    }

    /// <summary>Hides the whole canvas.</summary>
    public void HideAll()
    {
        if (keyboard != null)
            keyboard.Hide();
        SetPanel(null);
        if (canvas != null)
            canvas.gameObject.SetActive(false);
    }

    private System.Collections.IEnumerator Pop(Transform t)
    {
        if (!restScales.TryGetValue(t, out var rest))
            restScales[t] = rest = t.localScale;
        for (float x = 0f; x < 1f; x += Time.deltaTime / 0.3f)
        {
            float u = x - 1f;
            t.localScale = rest * (1f + 2.70158f * u * u * u + 1.70158f * u * u); // ease-out-back
            yield return null;
        }
        t.localScale = rest;
        popRoutine = null;
    }

    private void SetPanel(GameObject panel)
    {
        if (createTaskPanel != null)
            createTaskPanel.SetActive(panel == createTaskPanel);
        if (nameTaskPanel != null)
            nameTaskPanel.SetActive(panel == nameTaskPanel);
        if (settingTimerPanel != null)
            settingTimerPanel.SetActive(panel == settingTimerPanel);
        foreach (var p in extraPanels)
            if (p != null)
                p.SetActive(p == panel);
    }

    private void PlaceInFrontOfUser()
    {
        var t = canvas.transform;

        // Scale so a panel is panelWidth wide (panels share the same rect, so measure whichever is shown).
        var panel = (createTaskPanel != null ? createTaskPanel : nameTaskPanel)?.transform as RectTransform;
        if (panelWidth > 0f && panel != null)
        {
            var corners = new Vector3[4];
            panel.GetWorldCorners(corners);
            float width = Vector3.Distance(corners[1], corners[2]);
            if (width > 0.0001f)
                t.localScale *= panelWidth / width;
        }

        // Yaw-only forward so the panel stays upright if the user is looking up or down.
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
        forward.Normalize();

        // A world-space canvas is read from its -Z side, so +Z points away from the user.
        t.SetPositionAndRotation(head.position + forward * distanceFromHead + Vector3.up * heightOffset,
            Quaternion.LookRotation(forward, Vector3.up));
    }
}
