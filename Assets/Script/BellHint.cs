using UnityEngine;

// "Touch me" hint for the mini town's bell: once started (on the first grab of the town), the bell turns gold and
// rings slowly — a few damped swings from its top every couple of seconds, glowing warmer orange on each ring —
// until the bell is touched and the town is confirmed.
// The bell shares its hologram material with the whole mini town, so colors are overridden on this renderer only.
[RequireComponent(typeof(Renderer))]
public class BellHint : MonoBehaviour
{
    [SerializeField] private OnboardingManager onboarding;

    [Header("Colors")]
    [SerializeField] private Color gold = new Color(1f, 0.86f, 0.22f, 1f);
    [SerializeField] private Color ringGlow = new Color(1f, 0.55f, 0.12f, 1f);
    [SerializeField] private Color fill = new Color(0.6f, 0.4f, 0.03f, 0.4f);

    [Header("Ringing")]
    [Tooltip("Seconds between rings.")]
    [SerializeField] private float ringInterval = 2.4f;
    [Tooltip("Largest swing angle, in degrees.")]
    [SerializeField] private float swingAngle = 18f;
    [Tooltip("Swings per second within a ring.")]
    [SerializeField] private float swingFrequency = 1.6f;
    [Tooltip("How quickly each ring dies down.")]
    [SerializeField] private float damping = 1.8f;

    public bool IsHinting { get; private set; }

    private static readonly int EdgeColorId = Shader.PropertyToID("_EdgeColor");
    private static readonly int RimColorId = Shader.PropertyToID("_RimColor");
    private static readonly int FillColorId = Shader.PropertyToID("_FillColor");
    private static readonly int GridColorId = Shader.PropertyToID("_GridColor");
    private static readonly int RevealEdgeColorId = Shader.PropertyToID("_RevealEdgeColor");

    private Renderer bellRenderer;
    private MaterialPropertyBlock block;
    private Vector3 restPosition;
    private Quaternion restRotation;
    private Vector3 hingeLocal;     // top of the bell, in its own space
    private Vector3 swingAxisLocal; // horizontal axis in the parent's space
    private float startTime;

    private void Awake()
    {
        bellRenderer = GetComponent<Renderer>();
        block = new MaterialPropertyBlock();
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();
    }

    private void OnEnable()
    {
        if (onboarding != null)
            onboarding.Confirmed += StopHint;
    }

    private void OnDisable()
    {
        if (onboarding != null)
            onboarding.Confirmed -= StopHint;
        if (IsHinting)
        {
            Restore();
            // The bell's town is hidden as part of confirming, before Confirmed is raised; the hint is done then.
            if (onboarding != null && onboarding.IsConfirmed)
                IsHinting = false;
        }
    }

    /// <summary>Starts the gold ringing hint (does nothing if it's already running or the bell was rung).</summary>
    public void StartHint()
    {
        if (IsHinting || (onboarding != null && onboarding.IsConfirmed))
            return;

        IsHinting = true;
        startTime = Time.time;
        restPosition = transform.localPosition;
        restRotation = transform.localRotation;

        // Hinge at the top of the bell (whichever mesh axis points up right now), swinging about a horizontal axis.
        var filter = GetComponent<MeshFilter>();
        Bounds mesh = filter != null && filter.sharedMesh != null ? filter.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one * 0.01f);
        Vector3 upLocal = transform.InverseTransformDirection(Vector3.up);
        hingeLocal = mesh.center + Vector3.Scale(mesh.extents, upLocal);
        Vector3 parentUp = transform.parent != null ? transform.parent.InverseTransformDirection(Vector3.up) : Vector3.up;
        Vector3 parentRight = transform.parent != null ? transform.parent.InverseTransformDirection(transform.right) : transform.right;
        swingAxisLocal = Vector3.ProjectOnPlane(parentRight, parentUp).normalized;
        if (swingAxisLocal.sqrMagnitude < 0.01f)
            swingAxisLocal = Vector3.Cross(parentUp, Vector3.forward).normalized;

        Debug.Log("[BellHint] Bell hint started");
    }

    /// <summary>Stops the hint and puts the bell back to its normal hologram look and pose.</summary>
    public void StopHint()
    {
        if (!IsHinting)
            return;
        IsHinting = false;
        Restore();
    }

    private void Update()
    {
        if (!IsHinting)
            return;

        // Each ring: a damped swing that starts at rest, then quiet until the next ring.
        float t = Mathf.Repeat(Time.time - startTime, ringInterval);
        float envelope = Mathf.Exp(-t * damping);
        float angle = swingAngle * Mathf.Sin(t * swingFrequency * Mathf.PI * 2f) * envelope;

        transform.localPosition = restPosition;
        transform.localRotation = restRotation;
        if (transform.parent != null)
        {
            Vector3 hingeWorld = transform.TransformPoint(hingeLocal);
            transform.RotateAround(hingeWorld, transform.parent.TransformDirection(swingAxisLocal), angle);
        }

        // Gold, flaring to warm orange on each ring, plus a slow breathing glow in between.
        float flare = envelope;
        float breathe = 0.85f + 0.15f * Mathf.Sin(Time.time * 3f);
        Color edge = Color.Lerp(gold, ringGlow, flare) * (breathe + flare * 0.5f);
        edge.a = 1f;

        bellRenderer.GetPropertyBlock(block);
        block.SetColor(EdgeColorId, edge);
        block.SetColor(RimColorId, Color.Lerp(gold, ringGlow, flare * 0.7f));
        block.SetColor(FillColorId, Color.Lerp(fill, new Color(ringGlow.r, ringGlow.g, ringGlow.b, fill.a + 0.2f), flare * 0.5f));
        block.SetColor(GridColorId, new Color(gold.r, gold.g, gold.b, 0.6f));
        block.SetColor(RevealEdgeColorId, ringGlow);
        bellRenderer.SetPropertyBlock(block);
    }

    private void Restore()
    {
        transform.localPosition = restPosition;
        transform.localRotation = restRotation;
        if (bellRenderer != null)
            bellRenderer.SetPropertyBlock(null);
    }
}
