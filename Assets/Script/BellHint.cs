using UnityEngine;

// "Touch me" hint for the mini town's bell: once started (when the user starts scaling the town), the bell turns gold
// and rings slowly — a few damped swings from its top every couple of seconds, glowing warmer orange on each ring.
// When the bell is touched it celebrates: a white-gold flash, a big ring, a pop and a burst of sparkles.
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

    [Header("When touched")]
    [Tooltip("Sparkle material (ProductivityTown/Particle Soft, Sparkle shape), e.g. FX_Sparkle.")]
    [SerializeField] private Material sparkleMaterial;
    [SerializeField] private int sparkleCount = 45;
    [SerializeField] private float celebrateSwingAngle = 32f;
    [Tooltip("How much the bell grows in its pop (0.35 = 35%).")]
    [SerializeField] private float celebratePop = 0.35f;
    [ColorUsage(false, true)]
    [SerializeField] private Color flashColor = new Color(3f, 2.6f, 1.6f, 1f);

    public bool IsHinting { get; private set; }
    public bool IsCelebrating { get; private set; }

    private static readonly int EdgeColorId = Shader.PropertyToID("_EdgeColor");
    private static readonly int RimColorId = Shader.PropertyToID("_RimColor");
    private static readonly int FillColorId = Shader.PropertyToID("_FillColor");
    private static readonly int GridColorId = Shader.PropertyToID("_GridColor");
    private static readonly int RevealEdgeColorId = Shader.PropertyToID("_RevealEdgeColor");

    private Renderer bellRenderer;
    private MaterialPropertyBlock block;
    private Vector3 restPosition;
    private Quaternion restRotation;
    private Vector3 restScale;
    private bool restCaptured;
    private Vector3 hingeLocal;     // top of the bell, in its own space
    private Vector3 swingAxisLocal; // horizontal axis in the parent's space
    private float startTime;
    private float celebrateStart;

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
        {
            onboarding.BellRung += Celebrate;
            onboarding.Spawned += ResetBell;
        }
    }

    private void OnDisable()
    {
        if (onboarding != null)
        {
            onboarding.BellRung -= Celebrate;
            onboarding.Spawned -= ResetBell;
        }
        if (IsHinting || IsCelebrating)
        {
            Restore();
            // The bell's town is hidden after the bell is rung; the hint is done then.
            if (onboarding != null && onboarding.IsConfirmed)
                IsHinting = IsCelebrating = false;
        }
    }

    // A (re)spawn restarts onboarding.
    private void ResetBell()
    {
        if (IsHinting || IsCelebrating)
            Restore();
        IsHinting = IsCelebrating = false;
    }

    /// <summary>Starts the gold ringing hint (does nothing if it's already running or the bell was rung).</summary>
    public void StartHint()
    {
        if (IsHinting || IsCelebrating || (onboarding != null && onboarding.IsConfirmed))
            return;

        IsHinting = true;
        startTime = Time.time;
        CaptureRest();
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

    /// <summary>The bell was touched: flash, ring hard, pop and burst into sparkles.</summary>
    public void Celebrate()
    {
        if (IsCelebrating)
            return;
        if (!IsHinting)
            CaptureRest();
        IsHinting = false;
        IsCelebrating = true;
        celebrateStart = Time.time;
        SpawnSparkles();
    }

    private void CaptureRest()
    {
        restPosition = transform.localPosition;
        restRotation = transform.localRotation;
        restScale = transform.localScale;
        restCaptured = true;

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
    }

    private void Update()
    {
        if (IsCelebrating)
        {
            UpdateCelebrate();
            return;
        }
        if (!IsHinting)
            return;

        // Each ring: a damped swing that starts at rest, then quiet until the next ring.
        float t = Mathf.Repeat(Time.time - startTime, ringInterval);
        float envelope = Mathf.Exp(-t * damping);
        float angle = swingAngle * Mathf.Sin(t * swingFrequency * Mathf.PI * 2f) * envelope;
        Swing(angle, 1f);

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

    private void UpdateCelebrate()
    {
        float t = Time.time - celebrateStart;
        float envelope = Mathf.Exp(-t * 2.2f);
        float angle = celebrateSwingAngle * Mathf.Sin(t * 2.6f * Mathf.PI * 2f) * envelope;
        // Pop: quick grow, then settle back.
        float pop = t < 0.12f ? t / 0.12f : Mathf.Exp(-(t - 0.12f) * 5f);
        Swing(angle, 1f + celebratePop * pop);

        // White-hot flash fading to a warm gold glow.
        float flash = Mathf.Exp(-t * 3f);
        Color edge = Color.Lerp(gold * 1.6f, flashColor, flash);
        edge.a = 1f;
        bellRenderer.GetPropertyBlock(block);
        block.SetColor(EdgeColorId, edge);
        block.SetColor(RimColorId, Color.Lerp(gold, Color.white, flash));
        block.SetColor(FillColorId, Color.Lerp(new Color(ringGlow.r, ringGlow.g, ringGlow.b, 0.55f), new Color(1f, 0.95f, 0.8f, 0.9f), flash));
        block.SetColor(GridColorId, new Color(1f, 0.9f, 0.5f, 0.8f));
        block.SetColor(RevealEdgeColorId, ringGlow);
        bellRenderer.SetPropertyBlock(block);
    }

    // Rest pose, then swung about the hinge at the top and scaled about the bell's pivot.
    private void Swing(float angle, float scale)
    {
        transform.localPosition = restPosition;
        transform.localRotation = restRotation;
        transform.localScale = restScale * scale;
        if (transform.parent != null)
        {
            Vector3 hingeWorld = transform.TransformPoint(hingeLocal);
            transform.RotateAround(hingeWorld, transform.parent.TransformDirection(swingAxisLocal), angle);
        }
    }

    // A one-shot burst in world space (it outlives the mini town), sized in meters around the bell.
    private void SpawnSparkles()
    {
        if (sparkleMaterial == null)
            return;

        Bounds b = bellRenderer.bounds;
        float size = Mathf.Max(b.size.x, b.size.y, b.size.z);
        var go = new GameObject("BellSparkles");
        go.transform.position = b.center;
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.duration = 0.5f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Shape;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.4f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.12f, 0.32f);
        main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Max(size * 0.2f, 0.006f), Mathf.Max(size * 0.45f, 0.014f));
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.82f, 0.3f, 1f), new Color(1f, 1f, 0.9f, 1f));
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.gravityModifier = -0.01f; // drift up a little
        main.maxParticles = sparkleCount * 2;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[]
        {
            new ParticleSystem.Burst(0f, (short)sparkleCount),
            new ParticleSystem.Burst(0.18f, (short)(sparkleCount / 2)),
        });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = Mathf.Max(size * 0.5f, 0.005f);

        var limit = ps.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.limit = 0.04f;
        limit.dampen = 0.08f;

        var sizeOverLife = ps.sizeOverLifetime;
        sizeOverLife.enabled = true;
        sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.3f), new Keyframe(0.12f, 1f), new Keyframe(0.45f, 0.7f), new Keyframe(0.65f, 1f), new Keyframe(1f, 0f)));

        var rotation = ps.rotationOverLifetime;
        rotation.enabled = true;
        rotation.z = new ParticleSystem.MinMaxCurve(-2f, 2f);

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = sparkleMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        ps.Play();
        Destroy(go, 3f);
    }

    private void Restore()
    {
        if (!restCaptured)
            return;
        transform.localPosition = restPosition;
        transform.localRotation = restRotation;
        transform.localScale = restScale;
        if (bellRenderer != null)
            bellRenderer.SetPropertyBlock(null);
    }
}
