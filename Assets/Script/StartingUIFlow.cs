using System.Collections;
using UnityEngine;

// Drives the onboarding hint panels on StartingUICanvas:
//   1. The mini town + ring appear; after a short delay the canvas shows "Place your town" (PlaceurTownUI).
//   2. First grab: that panel hides a few seconds later (or right away on the first release).
//   3. First release: "Scale your town" (ScaleTown) shows and the ghost hands demo the pinch-and-stretch.
//   4. The town starts scaling: the ghost hands and ScaleTown go away, the bell starts its hint and RingBell shows.
//   5. The mini town goes away (bell rung): the whole canvas hides.
// Each panel is brought in front of the user when it appears, and the canvas lazily follows their view.
public class StartingUIFlow : MonoBehaviour
{
    private enum Step { Hidden, Place, Scale, Bell, Done }

    [SerializeField] private OnboardingManager onboarding;
    [SerializeField] private Canvas canvas;

    [Header("Panels")]
    [SerializeField] private GameObject placeTownPanel;
    [SerializeField] private GameObject scaleTownPanel;
    [SerializeField] private GameObject ringBellPanel;

    [Header("Hints")]
    [SerializeField] private GhostHandsHint ghostHands;
    [SerializeField] private BellHint bellHint;

    [Header("Timing")]
    [Tooltip("Delay after the mini town appears before the first panel shows.")]
    [SerializeField] private float showDelay = 1f;
    [Tooltip("How long the place panel stays up after the first grab.")]
    [SerializeField] private float hidePlaceAfterGrab = 3f;
    [Tooltip("Relative change of the town's scale that counts as 'started scaling' (0.03 = 3%).")]
    [SerializeField] private float scaleThreshold = 0.03f;

    [Header("Placement (in front of the user)")]
    [SerializeField] private float distanceFromHead = 0.8f;
    [Tooltip("Height of the panel relative to the eyes (positive = above), so the mini town below stays visible.")]
    [SerializeField] private float heightOffset = 0.12f;
    [Tooltip("Re-center the canvas when the user looks this far away from it (degrees).")]
    [SerializeField] private float followAngle = 35f;
    [SerializeField] private float followSmoothTime = 0.35f;
    [SerializeField] private float popDuration = 0.25f;

    private Step step = Step.Hidden;
    private Transform head;
    private Transform town;
    private Vector3 canvasScale;
    private float spawnScale;
    private bool grabbedOnce;
    private bool wasGrabbed;
    private bool following;
    private Vector3 followVelocity;
    private Coroutine showRoutine;
    private Coroutine hidePlaceRoutine;
    private Coroutine popRoutine;

    private void Awake()
    {
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();
        if (canvas == null)
            canvas = GetComponent<Canvas>();
        canvasScale = canvas.transform.localScale;

        var rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;

        HideAll();
    }

    private void OnEnable()
    {
        if (onboarding == null)
            return;
        onboarding.Spawned += OnTownSpawned;
        onboarding.Confirmed += Finish;
    }

    private void OnDisable()
    {
        if (onboarding == null)
            return;
        onboarding.Spawned -= OnTownSpawned;
        onboarding.Confirmed -= Finish;
    }

    private void OnTownSpawned()
    {
        StopAll();
        HideAll();
        town = onboarding.MiniTown;
        spawnScale = town != null ? town.localScale.x : 1f;
        grabbedOnce = false;
        wasGrabbed = false;
        step = Step.Hidden;
        showRoutine = StartCoroutine(ShowPlaceAfterDelay());
    }

    private IEnumerator ShowPlaceAfterDelay()
    {
        yield return new WaitForSeconds(showDelay);
        showRoutine = null;
        // If the user already grabbed and released during the delay, skip ahead.
        if (step == Step.Hidden)
        {
            step = Step.Place;
            ShowPanel(placeTownPanel);
            if (grabbedOnce && !wasGrabbed)
                OnFirstRelease();
            else if (grabbedOnce)
                hidePlaceRoutine = StartCoroutine(HidePlaceLater());
        }
    }

    private void Update()
    {
        if (step == Step.Done || town == null)
            return;

        // The mini town is hidden (bell rung, or turned off): the canvas goes with it.
        if (!town.gameObject.activeInHierarchy)
        {
            Finish();
            return;
        }

        bool grabbed = onboarding.IsTownGrabbed;
        if (grabbed && !wasGrabbed && !grabbedOnce)
        {
            grabbedOnce = true;
            if (step == Step.Place)
                hidePlaceRoutine = StartCoroutine(HidePlaceLater());
        }
        if (!grabbed && wasGrabbed && step == Step.Place)
            OnFirstRelease();
        wasGrabbed = grabbed;

        if (step == Step.Scale && HasScaled())
            OnStartedScaling();
    }

    private void LateUpdate()
    {
        if (step == Step.Hidden || step == Step.Done || !canvas.gameObject.activeSelf)
            return;

        // Lazy follow: once the user looks far enough away, glide the canvas back in front of them.
        GetTargetPose(out Vector3 targetPos, out Quaternion targetRot);
        Transform t = canvas.transform;
        Vector3 toCanvas = Vector3.ProjectOnPlane(t.position - head.position, Vector3.up);
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (!following && Vector3.Angle(toCanvas, forward) > followAngle)
            following = true;
        if (following)
        {
            t.position = Vector3.SmoothDamp(t.position, targetPos, ref followVelocity, followSmoothTime);
            t.rotation = Quaternion.Slerp(t.rotation, targetRot, 1f - Mathf.Exp(-8f * Time.deltaTime));
            if ((t.position - targetPos).sqrMagnitude < 0.0001f)
                following = false;
        }
    }

    private IEnumerator HidePlaceLater()
    {
        yield return new WaitForSeconds(hidePlaceAfterGrab);
        hidePlaceRoutine = null;
        if (placeTownPanel != null)
            placeTownPanel.SetActive(false);
    }

    private void OnFirstRelease()
    {
        if (hidePlaceRoutine != null)
        {
            StopCoroutine(hidePlaceRoutine);
            hidePlaceRoutine = null;
        }
        if (placeTownPanel != null)
            placeTownPanel.SetActive(false);

        // Already resized during the first grab: skip straight to the bell.
        if (HasScaled())
        {
            OnStartedScaling();
            return;
        }

        step = Step.Scale;
        ShowPanel(scaleTownPanel);
        if (ghostHands != null)
            ghostHands.Begin();
    }

    private void OnStartedScaling()
    {
        step = Step.Bell;
        if (ghostHands != null)
            ghostHands.Stop();
        if (scaleTownPanel != null)
            scaleTownPanel.SetActive(false);
        if (bellHint != null)
            bellHint.StartHint();
        ShowPanel(ringBellPanel);
    }

    private bool HasScaled()
    {
        return town != null && spawnScale > 0f
               && Mathf.Abs(town.localScale.x / spawnScale - 1f) > scaleThreshold;
    }

    private void Finish()
    {
        step = Step.Done;
        StopAll();
        if (ghostHands != null)
            ghostHands.Stop();
        HideAll();
    }

    // Shows one panel (hiding the others) with the canvas placed in front of the user and a small pop-in.
    private void ShowPanel(GameObject panel)
    {
        foreach (var p in new[] { placeTownPanel, scaleTownPanel, ringBellPanel })
            if (p != null)
                p.SetActive(p == panel);
        if (panel == null)
            return;

        canvas.gameObject.SetActive(true);
        GetTargetPose(out Vector3 pos, out Quaternion rot);
        canvas.transform.SetPositionAndRotation(pos, rot);
        following = false;
        followVelocity = Vector3.zero;

        if (popRoutine != null)
            StopCoroutine(popRoutine);
        popRoutine = StartCoroutine(Pop());
    }

    private IEnumerator Pop()
    {
        Transform t = canvas.transform;
        for (float time = 0f; time < popDuration; time += Time.deltaTime)
        {
            float k = time / popDuration;
            // Ease-out-back: overshoots a little, then settles.
            float s = 1f + 2.2f * Mathf.Pow(k - 1f, 3f) + 1.2f * Mathf.Pow(k - 1f, 2f);
            t.localScale = canvasScale * Mathf.Max(0.01f, s);
            yield return null;
        }
        t.localScale = canvasScale;
        popRoutine = null;
    }

    private void GetTargetPose(out Vector3 position, out Quaternion rotation)
    {
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
        forward.Normalize();
        position = head.position + forward * distanceFromHead + Vector3.up * heightOffset;
        // World-space UI faces the user when its forward points away from them.
        rotation = Quaternion.LookRotation(position - head.position, Vector3.up);
    }

    private void HideAll()
    {
        foreach (var p in new[] { placeTownPanel, scaleTownPanel, ringBellPanel })
            if (p != null)
                p.SetActive(false);
        if (canvas != null)
        {
            canvas.transform.localScale = canvasScale;
            canvas.gameObject.SetActive(false);
        }
    }

    private void StopAll()
    {
        if (showRoutine != null)
            StopCoroutine(showRoutine);
        if (hidePlaceRoutine != null)
            StopCoroutine(hidePlaceRoutine);
        if (popRoutine != null)
            StopCoroutine(popRoutine);
        showRoutine = hidePlaceRoutine = popRoutine = null;
    }
}
