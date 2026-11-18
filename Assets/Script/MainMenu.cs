using System.Collections;
using Oculus.Interaction.Input;
using UnityEngine;
using UnityEngine.UI;

// The town's main menu, opened by touching the finished building (e.g. the Library's big box collider) with the right
// hand. It's unlocked once that building is completed, then pops up close to the user (within poke reach), on the
// side toward the building, facing them.
//   Back: closes the menu.  Exit: quits the app.  End Current Session: ends the running focus session (disabled when
//   none is running).  New Task: not wired yet.
public class MainMenu : MonoBehaviour
{
    [Tooltip("The menu's world-space canvas (e.g. MainMenuCanvas). It's detached from the town at start so the town's " +
             "scale doesn't affect its size.")]
    [SerializeField] private Canvas menuCanvas;
    [Tooltip("Optional: the visible panel inside the canvas, used to size the menu (e.g. MainMenuPanel/bkg).")]
    [SerializeField] private RectTransform sizeReference;
    [Tooltip("Touching this with the right hand opens the menu (e.g. the Library's box collider).")]
    [SerializeField] private Collider trigger;
    [Tooltip("The right tracked hand (e.g. ComprehensiveInteractorsRight).")]
    [SerializeField] private Hand rightHand;
    [Tooltip("The menu is unlocked once this building is completed.")]
    [SerializeField] private ConstructionProject unlockedBy;
    [SerializeField] private FocusSession session;

    [Header("Buttons")]
    [SerializeField] private Button newTaskButton;
    [SerializeField] private Button endSessionButton;
    [SerializeField] private Button exitButton;
    [SerializeField] private Button backButton;

    [Header("Placement")]
    [Tooltip("World width of the menu panel, in meters.")]
    [SerializeField] private float menuWidth = 0.3f;
    [Tooltip("Distance from the head, in meters (keep it within arm's reach for poking).")]
    [SerializeField] private float distance = 0.45f;
    [Tooltip("Height of the menu's center relative to the eyes, in meters.")]
    [SerializeField] private float heightOffset = -0.1f;
    [Tooltip("How far the menu turns from the user's forward toward the building, at most (degrees).")]
    [SerializeField] private float maxSideAngle = 40f;
    [Tooltip("How close (meters) a right-hand joint must get to the trigger to open the menu.")]
    [SerializeField] private float touchDistance = 0.02f;

    [Header("Audio")]
    [SerializeField] private AudioClip openSound;
    [SerializeField] private AudioClip closeSound;
    [Range(0f, 1f)] [SerializeField] private float volume = 0.7f;

    public bool IsUnlocked { get; private set; }
    public bool IsOpen => menuCanvas != null && menuCanvas.gameObject.activeSelf;

    private static readonly HandJointId[] TouchJoints =
    {
        HandJointId.HandIndexTip, HandJointId.HandMiddleTip, HandJointId.HandThumbTip, HandJointId.HandRingTip,
        HandJointId.HandPalm
    };

    private Transform head;
    private AudioSource source;
    private bool handWasInside;
    private Vector3 restScale;
    private Coroutine popRoutine;

    private void Awake()
    {
        var rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;
        if (session == null)
            session = FindFirstObjectByType<FocusSession>();

        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0.7f;
        source.dopplerLevel = 0f;

        if (menuCanvas != null)
        {
            // Out of the town, so its size is ours to set.
            menuCanvas.transform.SetParent(null, true);
            restScale = menuCanvas.transform.localScale;
            menuCanvas.gameObject.SetActive(false);
        }

        if (backButton != null)
            backButton.onClick.AddListener(Close);
        if (exitButton != null)
            exitButton.onClick.AddListener(ExitApp);
        if (endSessionButton != null)
            endSessionButton.onClick.AddListener(EndSession);
    }

    private void OnEnable()
    {
        if (unlockedBy != null)
            unlockedBy.onCompleted.AddListener(Unlock);
    }

    private void OnDisable()
    {
        if (unlockedBy != null)
            unlockedBy.onCompleted.RemoveListener(Unlock);
    }

    /// <summary>Lets the building open the menu from now on.</summary>
    public void Unlock() => IsUnlocked = true;

    private void Update()
    {
        if (IsOpen && endSessionButton != null)
            endSessionButton.interactable = session != null && session.IsActive;

        if (!IsUnlocked || trigger == null || !trigger.gameObject.activeInHierarchy)
            return;

        // Open on a fresh touch (the hand has to leave the building before it can open the menu again).
        bool inside = RightHandTouching();
        if (inside && !handWasInside && !IsOpen)
            Open();
        handWasInside = inside;
    }

    private bool RightHandTouching()
    {
        if (rightHand == null || !rightHand.IsTrackedDataValid)
            return false;
        foreach (var joint in TouchJoints)
        {
            if (!rightHand.GetJointPose(joint, out Pose pose))
                continue;
            Vector3 closest = trigger.ClosestPoint(pose.position);
            if ((closest - pose.position).sqrMagnitude <= touchDistance * touchDistance)
                return true;
        }
        return false;
    }

    /// <summary>Opens the menu in front of the user, turned toward the building, within reach.</summary>
    public void Open()
    {
        if (menuCanvas == null)
            return;

        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
        forward.Normalize();
        Vector3 toBuilding = trigger != null ? Vector3.ProjectOnPlane(trigger.bounds.center - head.position, Vector3.up) : forward;
        float angle = toBuilding.sqrMagnitude > 0.0001f ? Vector3.SignedAngle(forward, toBuilding, Vector3.up) : 0f;
        Vector3 dir = Quaternion.Euler(0f, Mathf.Clamp(angle, -maxSideAngle, maxSideAngle), 0f) * forward;

        var t = menuCanvas.transform;
        t.localScale = restScale;
        t.SetPositionAndRotation(head.position + dir * distance + Vector3.up * heightOffset,
            Quaternion.LookRotation(dir, Vector3.up)); // a world canvas is read from its -Z side
        // Size to menuWidth.
        var reference = sizeReference != null ? sizeReference : (RectTransform)t;
        float width = reference.rect.width * reference.lossyScale.x;
        if (menuWidth > 0f && width > 0.0001f)
            restScaleForOpen = restScale * (menuWidth / width);
        else
            restScaleForOpen = restScale;

        if (endSessionButton != null)
            endSessionButton.interactable = session != null && session.IsActive;
        menuCanvas.gameObject.SetActive(true);
        if (session != null)
            session.HideMenuPointer();
        Play(openSound, t.position);

        if (popRoutine != null)
            StopCoroutine(popRoutine);
        popRoutine = StartCoroutine(Pop(t, restScaleForOpen));
    }

    private Vector3 restScaleForOpen;

    /// <summary>Closes the menu (Back).</summary>
    public void Close()
    {
        if (!IsOpen)
            return;
        Play(closeSound, menuCanvas.transform.position);
        menuCanvas.gameObject.SetActive(false);
    }

    private void EndSession()
    {
        if (session != null)
            session.EndSession();
        Close();
    }

    private void ExitApp()
    {
        Debug.Log("[MainMenu] Exit");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private IEnumerator Pop(Transform t, Vector3 full)
    {
        for (float x = 0f; x < 1f; x += Time.deltaTime / 0.3f)
        {
            float u = x - 1f;
            t.localScale = full * (1f + 2.70158f * u * u * u + 1.70158f * u * u); // ease-out-back
            yield return null;
        }
        t.localScale = full;
        popRoutine = null;
    }

    private void Play(AudioClip clip, Vector3 at)
    {
        if (clip == null)
            return;
        source.transform.position = at;
        source.PlayOneShot(clip, volume);
    }
}
