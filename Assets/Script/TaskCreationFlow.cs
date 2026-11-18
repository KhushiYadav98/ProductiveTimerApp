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

    private void Awake()
    {
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();

        var rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;

        if (createTaskButton != null)
            createTaskButton.onClick.AddListener(ShowNamePanel);
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

        SetPanel(createTaskPanel);
        canvas.gameObject.SetActive(true);
        PlaceInFrontOfUser();
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
        if (timerPicker != null)
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
        canvas.gameObject.SetActive(false);
        if (townTimer != null)
            townTimer.StartCountdown(minutes * 60f);
    }

    private void SetPanel(GameObject panel)
    {
        if (createTaskPanel != null)
            createTaskPanel.SetActive(panel == createTaskPanel);
        if (nameTaskPanel != null)
            nameTaskPanel.SetActive(panel == nameTaskPanel);
        if (settingTimerPanel != null)
            settingTimerPanel.SetActive(panel == settingTimerPanel);
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
