using System.Collections;
using UnityEngine;
using UnityEngine.UI;

// The focus session after a task's timer is set:
//   1. "Time to focus" pops up for a few seconds; the countdown starts, and the town's signs appear over the
//      construction site and the train station.
//   2. After a stretch of focus time, a break: the countdown, train and construction stop and "Break time" shows;
//      when the break is over, "Break time over" shows with a Resume button, which continues the countdown.
//   3. When the countdown ends, the signs go away and "Did you finish your task?" asks (No is disabled).
//      Yes completes the building (the Library pops up) and shows "Building completed" over it for a few seconds.
// Sounds are CC0 (see Assets/Audio/SFX/CREDITS.txt).
public class FocusSession : MonoBehaviour
{
    [SerializeField] private TaskCreationFlow flow;
    [SerializeField] private TownTaskTimer timer;
    [SerializeField] private ConstructionProject construction;

    [Header("Panels (on the task canvas)")]
    [SerializeField] private GameObject timeToFocusPanel;
    [SerializeField] private GameObject breakTimePanel;
    [SerializeField] private GameObject breakOverPanel;
    [SerializeField] private GameObject taskFinishedPanel;
    [SerializeField] private Button resumeButton;
    [SerializeField] private Button yesButton;
    [SerializeField] private Button noButton;

    [Header("Town signs (TownCanvas)")]
    [SerializeField] private GameObject townCanvas;
    [SerializeField] private GameObject constructionSign;
    [SerializeField] private GameObject trainStationSign;
    [SerializeField] private GameObject buildingCompletedSign;
    [Tooltip("Turn the signs to face the user (yaw only).")]
    [SerializeField] private bool signsFaceUser = true;

    [Header("Timing (seconds)")]
    [SerializeField] private float focusMessageTime = 3f;
    [Tooltip("Focus time before the break.")]
    [SerializeField] private float breakAfter = 20f;
    [SerializeField] private float breakDuration = 20f;
    [SerializeField] private float buildingCompletedTime = 5f;

    [Header("Audio")]
    [SerializeField] private AudioClip focusStart;
    [SerializeField] private AudioClip breakStart;
    [SerializeField] private AudioClip breakOver;
    [SerializeField] private AudioClip taskFinished;
    [SerializeField] private AudioClip buildingCompleted;
    [Range(0f, 1f)] [SerializeField] private float volume = 0.8f;

    public bool IsActive { get; private set; }
    public bool IsOnBreak { get; private set; }

    private Transform head;
    private AudioSource source;
    private bool hadBreak;
    private Coroutine routine;

    private void Awake()
    {
        if (flow == null)
            flow = FindFirstObjectByType<TaskCreationFlow>();
        if (timer == null)
            timer = FindFirstObjectByType<TownTaskTimer>();
        var rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;

        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0.6f; // mostly from the panel, but always clearly audible
        source.dopplerLevel = 0f;

        if (flow != null)
            flow.RegisterPanels(timeToFocusPanel, breakTimePanel, breakOverPanel, taskFinishedPanel);
        if (resumeButton != null)
            resumeButton.onClick.AddListener(ResumeAfterBreak);
        if (yesButton != null)
            yesButton.onClick.AddListener(FinishedYes);
        if (noButton != null)
            noButton.interactable = false; // not supported yet: the task has to be finished
        if (townCanvas != null)
            townCanvas.SetActive(false);
    }

    private void OnEnable()
    {
        if (timer != null)
            timer.onFinished.AddListener(OnTimerFinished);
    }

    private void OnDisable()
    {
        if (timer != null)
            timer.onFinished.RemoveListener(OnTimerFinished);
    }

    /// <summary>Starts a focus session of <paramref name="seconds"/>.</summary>
    public void Begin(float seconds)
    {
        IsActive = true;
        IsOnBreak = false;
        hadBreak = false;

        flow.ShowOnly(timeToFocusPanel);
        Play(focusStart);
        timer.StartCountdown(seconds);
        ShowSigns(construction: true, station: true, completed: false);

        Restart(HideAfter(timeToFocusPanel, focusMessageTime));
    }

    private void Update()
    {
        if (!IsActive || hadBreak || timer == null || !timer.IsRunning || timer.IsPaused)
            return;
        // One break, if there's still time left after it.
        if (timer.ElapsedSeconds >= breakAfter && timer.RemainingSeconds > 1f)
            StartBreak();
    }

    private void LateUpdate()
    {
        if (!signsFaceUser || townCanvas == null || !townCanvas.activeInHierarchy)
            return;
        foreach (Transform sign in townCanvas.transform)
        {
            if (!sign.gameObject.activeSelf)
                continue;
            Vector3 away = sign.position - head.position;
            away.y = 0f;
            if (away.sqrMagnitude > 0.0001f)
                sign.rotation = Quaternion.LookRotation(away.normalized, Vector3.up); // UI reads from its -Z side
        }
    }

    private void StartBreak()
    {
        hadBreak = true;
        IsOnBreak = true;
        timer.Pause();
        flow.ShowOnly(breakTimePanel);
        Play(breakStart);
        Restart(BreakCountdown());
    }

    private IEnumerator BreakCountdown()
    {
        yield return new WaitForSeconds(breakDuration);
        flow.ShowOnly(breakOverPanel);
        Play(breakOver);
        routine = null;
    }

    /// <summary>Resume button: back to focus.</summary>
    public void ResumeAfterBreak()
    {
        if (!IsOnBreak)
            return;
        IsOnBreak = false;
        flow.HideAll();
        timer.Resume();
    }

    private void OnTimerFinished()
    {
        if (!IsActive)
            return;
        Stop();
        ShowSigns(construction: false, station: false, completed: false);
        flow.ShowOnly(taskFinishedPanel);
        if (noButton != null)
            noButton.interactable = false;
        Play(taskFinished);
    }

    /// <summary>Yes button: the task is done, so the building is completed.</summary>
    public void FinishedYes()
    {
        flow.HideAll();
        IsActive = false;
        if (construction != null)
            construction.Complete();
        Play(buildingCompleted);
        ShowSigns(construction: false, station: false, completed: true);
        Restart(HideSignsAfter(buildingCompletedTime));
    }

    private IEnumerator HideAfter(GameObject panel, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        // Only if it's still the one showing (a break or the end may have replaced it).
        if (panel != null && panel.activeSelf)
            flow.HideAll();
        routine = null;
    }

    private IEnumerator HideSignsAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        ShowSigns(false, false, false);
        if (townCanvas != null)
            townCanvas.SetActive(false);
        routine = null;
    }

    private void ShowSigns(bool construction, bool station, bool completed)
    {
        if (townCanvas != null && (construction || station || completed))
            townCanvas.SetActive(true);
        SetSign(constructionSign, construction);
        SetSign(trainStationSign, station);
        SetSign(buildingCompletedSign, completed);
    }

    private void SetSign(GameObject sign, bool on)
    {
        if (sign == null || sign.activeSelf == on)
            return;
        sign.SetActive(on);
        if (on)
            StartCoroutine(PopSign(sign.transform));
    }

    private readonly System.Collections.Generic.Dictionary<Transform, Vector3> signScales =
        new System.Collections.Generic.Dictionary<Transform, Vector3>();

    private IEnumerator PopSign(Transform t)
    {
        if (!signScales.TryGetValue(t, out var rest))
            signScales[t] = rest = t.localScale;
        for (float x = 0f; x < 1f; x += Time.deltaTime / 0.4f)
        {
            float u = x - 1f;
            t.localScale = rest * (1f + 2.70158f * u * u * u + 1.70158f * u * u); // ease-out-back
            yield return null;
        }
        t.localScale = rest;
    }

    private void Restart(IEnumerator next)
    {
        Stop();
        routine = StartCoroutine(next);
    }

    private void Stop()
    {
        if (routine != null)
        {
            StopCoroutine(routine);
            routine = null;
        }
    }

    private void Play(AudioClip clip)
    {
        if (clip == null)
            return;
        source.transform.position = head.position + head.forward * 0.6f;
        source.PlayOneShot(clip, volume);
    }
}
