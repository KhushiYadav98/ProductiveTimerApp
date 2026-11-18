using System.Collections;
using UnityEngine;

// "Ghost" tutorial hands for the mini town: when started (by StartingUIFlow, after the user first lets go of the
// town), two hologram hands fade in at the town's sides, pinch, pull apart (showing a two-hand stretch to resize
// the town), hold, fade out, and loop, until stopped (the user starts scaling) or the bell is rung.
// The hands are authored as children of the mini town (in its unrotated space); at runtime they move to their own
// root that follows the town's position but keeps the town's facing from when it appeared, so they don't orbit
// while the town spins.
public class GhostHandsHint : MonoBehaviour
{
    [SerializeField] private OnboardingManager onboarding;
    [Tooltip("Left hand first, then right, each placed where it pinches the town (e.g. Hand_Pinch_Left / Hand_Pinch_right). " +
             "At runtime the pair is centered on the town and turned so the left hand is on the user's left.")]
    [SerializeField] private Transform[] hands;
    [Tooltip("Hologram material for the hands (fades via its _Fade property).")]
    [SerializeField] private Material hologramMaterial;

    [Header("Timing (seconds)")]
    [Tooltip("Delay after Begin() before the hands appear.")]
    [SerializeField] private float startDelay = 0f;
    [SerializeField] private float fadeIn = 0.45f;
    [SerializeField] private float pinch = 0.35f;
    [SerializeField] private float stretch = 1.2f;
    [SerializeField] private float hold = 0.45f;
    [SerializeField] private float fadeOut = 0.45f;
    [SerializeField] private float pause = 0.6f;

    [Header("Motion")]
    [Tooltip("How far each hand pulls away from the town's center, as a fraction of its starting distance.")]
    [SerializeField] private float stretchAmount = 0.45f;
    [Tooltip("How much the hands shrink while pinching (the squeeze).")]
    [SerializeField] private float pinchSqueeze = 0.12f;

    public bool IsPlaying => routine != null;

    private static readonly int FadeId = Shader.PropertyToID("_Fade");

    private Transform root;
    private Transform town;
    private Vector3[] restPositions;
    private Vector3[] restScales;
    private Renderer[] renderers;
    private MaterialPropertyBlock block;
    private Coroutine routine;
    private bool done;

    private void Awake()
    {
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();
        block = new MaterialPropertyBlock();

        var list = new System.Collections.Generic.List<Renderer>();
        foreach (var hand in hands)
        {
            if (hand == null)
                continue;
            foreach (var r in hand.GetComponentsInChildren<Renderer>(true))
            {
                if (hologramMaterial != null)
                {
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                        mats[i] = hologramMaterial;
                    r.sharedMaterials = mats;
                }
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                list.Add(r);
            }
            hand.gameObject.SetActive(false);
        }
        renderers = list.ToArray();
    }

    private void OnEnable()
    {
        if (onboarding == null)
            return;
        onboarding.Spawned += OnTownSpawned;
        onboarding.Confirmed += Stop;
    }

    private void OnDisable()
    {
        if (onboarding == null)
            return;
        onboarding.Spawned -= OnTownSpawned;
        onboarding.Confirmed -= Stop;
    }

    // A (re)spawn restarts onboarding: hide the hands and allow the hint to be shown again.
    private void OnTownSpawned()
    {
        HideHands();
        done = false;
    }

    /// <summary>Starts the looping pinch-and-stretch hint at the mini town (no-op once stopped, until a respawn).</summary>
    public void Begin()
    {
        if (done || onboarding == null || onboarding.MiniTown == null)
            return;

        town = onboarding.MiniTown;
        BuildRoot();
        if (routine != null)
            StopCoroutine(routine);
        routine = StartCoroutine(Play());
    }

    /// <summary>Ends the hint (e.g. once the user starts scaling the town).</summary>
    public void Stop()
    {
        done = true;
        HideHands();
    }

    private void HideHands()
    {
        if (routine != null)
        {
            StopCoroutine(routine);
            routine = null;
        }
        foreach (var hand in hands)
            if (hand != null)
                hand.gameObject.SetActive(false);
    }

    // A root at the town holding the hands with their authored poses (authored under the unrotated town), turned so
    // the first hand is on the user's left and the second on their right, centered on the town. Re-done on every
    // respawn so it matches the new placement.
    private void BuildRoot()
    {
        if (root == null)
            root = new GameObject("GhostHandsRoot").transform;

        if (restPositions == null)
        {
            restPositions = new Vector3[hands.Length];
            restScales = new Vector3[hands.Length];
            Vector3 middle = Vector3.zero;
            int count = 0;
            for (int i = 0; i < hands.Length; i++)
            {
                if (hands[i] == null)
                    continue;
                restPositions[i] = hands[i].localPosition;
                restScales[i] = hands[i].localScale;
                middle += restPositions[i];
                count++;
            }

            // Center the pair on the town horizontally (keep the authored heights).
            middle = count > 0 ? middle / count : Vector3.zero;
            middle.y = 0f;
            for (int i = 0; i < hands.Length; i++)
                restPositions[i] -= middle;

            // Authored left-to-right direction, used to turn the pair toward the user.
            if (hands.Length >= 2 && hands[0] != null && hands[1] != null)
                authoredRight = Vector3.ProjectOnPlane(restPositions[1] - restPositions[0], Vector3.up).normalized;

            for (int i = 0; i < hands.Length; i++)
                if (hands[i] != null)
                    hands[i].SetParent(root, false);
        }

        // Yaw so the authored left-to-right direction matches the user's right.
        var rig = FindFirstObjectByType<OVRCameraRig>();
        Transform head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;
        Vector3 userRight = Vector3.ProjectOnPlane(head.right, Vector3.up).normalized;
        float yaw = authoredRight.sqrMagnitude > 0.5f && userRight.sqrMagnitude > 0.5f
            ? Vector3.SignedAngle(authoredRight, userRight, Vector3.up)
            : town.eulerAngles.y;
        root.SetPositionAndRotation(town.position, Quaternion.Euler(0f, yaw, 0f));
        root.localScale = town.lossyScale;
    }

    private Vector3 authoredRight;

    private void LateUpdate()
    {
        // Follow the town's position (including its idle bob), but not its spin.
        if (root != null && town != null && routine != null)
            root.position = town.position;
    }

    private IEnumerator Play()
    {
        SetHands(0f, 0f, 0f);
        yield return new WaitForSeconds(startDelay);
        foreach (var hand in hands)
            if (hand != null)
                hand.gameObject.SetActive(true);

        while (true)
        {
            yield return Phase(fadeIn, t => SetHands(Ease(t), 0f, 0f));
            yield return Phase(pinch, t => SetHands(1f, Mathf.Sin(t * Mathf.PI), 0f));
            yield return Phase(stretch, t => SetHands(1f, 0f, Ease(t)));
            yield return Phase(hold, t => SetHands(1f, 0f, 1f));
            yield return Phase(fadeOut, t => SetHands(1f - Ease(t), 0f, 1f + t * 0.15f));
            SetHands(0f, 0f, 0f);
            yield return new WaitForSeconds(pause);
        }
    }

    private static IEnumerator Phase(float duration, System.Action<float> apply)
    {
        for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(duration, 0.01f))
        {
            apply(t);
            yield return null;
        }
        apply(1f);
    }

    // fade: 0..1 visibility; squeeze: 0..1 pinch squeeze; spread: 0 = at the town, 1 = fully stretched apart.
    private void SetHands(float fade, float squeeze, float spread)
    {
        for (int i = 0; i < hands.Length; i++)
        {
            if (hands[i] == null)
                continue;
            Vector3 rest = restPositions[i];
            Vector3 outward = new Vector3(rest.x, 0f, rest.z); // away from the town's center, horizontally
            hands[i].localPosition = rest + outward * stretchAmount * spread;
            hands[i].localScale = restScales[i] * (1f - pinchSqueeze * squeeze);
        }

        block.SetFloat(FadeId, fade);
        foreach (var r in renderers)
            r.SetPropertyBlock(block);
    }

    private static float Ease(float t) => t * t * (3f - 2f * t);
}
