using System.Collections;
using UnityEngine;
using UnityEngine.Events;

// A building under construction in the town, town-builder style: while the task timer runs, the construction site
// animates and kicks up dust; when it ends, a dust poof hides the site, the finished building pops up with a
// bounce, and sparkles and confetti burst out. The effects are particle systems built at runtime under the town,
// so they scale with it.
public class ConstructionProject : MonoBehaviour
{
    [Header("Objects")]
    [Tooltip("The animated construction site (shown while building, hidden when done).")]
    [SerializeField] private GameObject site;
    [Tooltip("Plays the site's looping construction animation; paused until building starts.")]
    [SerializeField] private Animator siteAnimator;
    [Tooltip("The finished building (e.g. Library), hidden until completion.")]
    [SerializeField] private GameObject building;

    [Header("Effect materials (ProductivityTown/Particle Soft)")]
    [SerializeField] private Material dustMaterial;
    [SerializeField] private Material sparkleMaterial;
    [SerializeField] private Material confettiMaterial;

    [Header("Dust while building")]
    [SerializeField] private float dustRate = 14f;
    [SerializeField] private Color dustColorA = new Color(0.78f, 0.68f, 0.55f, 0.75f);
    [SerializeField] private Color dustColorB = new Color(0.9f, 0.82f, 0.7f, 0.65f);

    [Header("Completion")]
    [Tooltip("Seconds into the dust poof when the site is swapped for the building.")]
    [SerializeField] private float swapDelay = 0.35f;
    [SerializeField] private float popDuration = 0.7f;

    public UnityEvent onCompleted;

    [Tooltip("Hide the construction site until building starts (e.g. for a later task's building).")]
    [SerializeField] private bool hideSiteUntilStarted = false;

    public GameObject Site => site;
    public bool IsBuilding { get; private set; }
    public bool IsComplete { get; private set; }

    private ParticleSystem dust, poof, sparkles, confetti;
    private Vector3 buildingScale = Vector3.one;

    private void Awake()
    {
        if (siteAnimator != null)
        {
            siteAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            siteAnimator.speed = 0f; // hold the first pose until building starts
        }
        if (building != null)
        {
            buildingScale = building.transform.localScale;
            building.SetActive(false);
        }
        if (hideSiteUntilStarted && site != null)
            site.SetActive(false);
    }

    /// <summary>Starts the construction animation and dust.</summary>
    public void StartBuilding()
    {
        if (IsComplete || site == null)
            return;

        site.SetActive(true);
        IsBuilding = true;
        if (siteAnimator != null)
            siteAnimator.speed = 1f;

        BuildEffects();
        dust.Play();
    }

    public bool IsPaused { get; private set; }

    /// <summary>Stops the work for now (e.g. a break): the animation holds and the dust settles.</summary>
    public void Pause()
    {
        if (!IsBuilding || IsPaused)
            return;
        IsPaused = true;
        if (siteAnimator != null)
            siteAnimator.speed = 0f;
        if (dust != null)
            dust.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    /// <summary>Continues the work after a pause.</summary>
    public void Resume()
    {
        if (!IsBuilding || !IsPaused)
            return;
        IsPaused = false;
        if (siteAnimator != null)
            siteAnimator.speed = 1f;
        if (dust != null)
            dust.Play();
    }

    /// <summary>Finishes construction: poof, swap the site for the building with a pop, sparkles and confetti.</summary>
    public void Complete()
    {
        if (IsComplete)
            return;
        IsComplete = true;
        IsBuilding = false;
        IsPaused = false;
        StartCoroutine(CompleteSequence());
    }

    private IEnumerator CompleteSequence()
    {
        BuildEffects();
        dust.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        SetGravity(poof, 0f);
        poof.Play();

        yield return new WaitForSeconds(swapDelay);

        if (site != null)
            site.SetActive(false);
        if (building != null)
        {
            building.SetActive(true);
            SetGravity(sparkles, 0.9f);
            SetGravity(confetti, 0.6f);
            sparkles.Play();
            confetti.Play();
            yield return Pop(building.transform);
        }

        Debug.Log("[Construction] Building complete");
        onCompleted?.Invoke();
    }

    // Grows from nothing with an overshoot, squashing a little like a cartoon building settling.
    private IEnumerator Pop(Transform t)
    {
        for (float x = 0f; x < 1f; x += Time.deltaTime / popDuration)
        {
            float u = x - 1f;
            float s = 1f + 2.4f * u * u * u + 1.4f * u * u; // ease-out-back
            float squash = 1f + 0.12f * Mathf.Sin(x * Mathf.PI * 2f) * (1f - x);
            t.localScale = new Vector3(buildingScale.x * s * squash, buildingScale.y * s / squash, buildingScale.z * s * squash);
            yield return null;
        }
        t.localScale = buildingScale;
    }

    // ---------- Effects ----------

    private void BuildEffects()
    {
        if (dust != null)
            return;

        // Footprint of the site, in the town's local units (effects live under the town, not the rotated site).
        Transform parent = site.transform.parent;
        var renderers = site.GetComponentsInChildren<Renderer>(true);
        Bounds world = renderers.Length > 0 ? renderers[0].bounds : new Bounds(site.transform.position, Vector3.one * 0.5f);
        foreach (var r in renderers)
            world.Encapsulate(r.bounds);
        float unit = parent != null ? parent.lossyScale.x : 1f;
        float width = Mathf.Max(world.size.x, world.size.z) / unit;
        Vector3 ground = parent != null
            ? parent.InverseTransformPoint(new Vector3(world.center.x, world.min.y, world.center.z))
            : new Vector3(world.center.x, world.min.y, world.center.z);

        dust = CreateDust(parent, ground, width);
        poof = CreatePoof(parent, ground, width);
        sparkles = CreateSparkles(parent, ground, width);
        confetti = CreateConfetti(parent, ground, width);
    }

    private ParticleSystem NewSystem(string name, Transform parent, Vector3 localPosition, Material material, bool loop)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localRotation = Quaternion.identity;
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = loop;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        // Keep simulating while off-screen, so dust is already there when the user looks back.
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return ps;
    }

    // Gravity isn't scaled by the hierarchy, so convert from town units to world units for the current town scale.
    private static void SetGravity(ParticleSystem ps, float townUnitsPerSecondSquared)
    {
        var main = ps.main;
        main.gravityModifier = townUnitsPerSecondSquared * ps.transform.lossyScale.y / Mathf.Abs(Physics.gravity.y);
    }

    private static ParticleSystem.MinMaxGradient FadeInOut(float peak)
    {
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, peak), new GradientAlphaKey(0f, 1f) });
        return new ParticleSystem.MinMaxGradient(g);
    }

    private ParticleSystem CreateDust(Transform parent, Vector3 ground, float width)
    {
        var ps = NewSystem("ConstructionDust", parent, ground + Vector3.up * 0.02f, dustMaterial, true);
        var main = ps.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(2.2f, 3.2f);
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(width * 0.12f, width * 0.24f);
        main.startColor = new ParticleSystem.MinMaxGradient(dustColorA, dustColorB);
        main.maxParticles = 150;

        var emission = ps.emission;
        emission.rateOverTime = dustRate;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(width * 0.85f, 0.01f, width * 0.85f);

        var velocity = ps.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.x = new ParticleSystem.MinMaxCurve(-width * 0.04f, width * 0.04f);
        velocity.y = new ParticleSystem.MinMaxCurve(width * 0.06f, width * 0.14f);
        velocity.z = new ParticleSystem.MinMaxCurve(-width * 0.04f, width * 0.04f);

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.6f, 1f, 1.6f));

        var color = ps.colorOverLifetime;
        color.enabled = true;
        color.color = FadeInOut(0.2f);

        var rotation = ps.rotationOverLifetime;
        rotation.enabled = true;
        rotation.z = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
        return ps;
    }

    private ParticleSystem CreatePoof(Transform parent, Vector3 ground, float width)
    {
        var ps = NewSystem("CompletionPoof", parent, ground + Vector3.up * width * 0.25f, dustMaterial, false);
        var main = ps.main;
        main.duration = 1f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.0f, 1.7f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(width * 0.5f, width * 1.0f);
        main.startSize = new ParticleSystem.MinMaxCurve(width * 0.25f, width * 0.45f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.95f, 0.92f, 0.85f, 0.95f), new Color(0.85f, 0.78f, 0.66f, 0.85f));
        main.maxParticles = 80;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 55) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = width * 0.3f;

        var limit = ps.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.limit = width * 0.05f;
        limit.dampen = 0.12f;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.6f, 1f, 1.5f));

        var color = ps.colorOverLifetime;
        color.enabled = true;
        color.color = FadeInOut(0.08f);

        var rotation = ps.rotationOverLifetime;
        rotation.enabled = true;
        rotation.z = new ParticleSystem.MinMaxCurve(-1f, 1f);
        return ps;
    }

    private ParticleSystem CreateSparkles(Transform parent, Vector3 ground, float width)
    {
        var ps = NewSystem("CompletionSparkles", parent, ground + Vector3.up * width * 0.2f, sparkleMaterial, false);
        var main = ps.main;
        main.duration = 1f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.6f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(width * 1.0f, width * 1.8f);
        main.startSize = new ParticleSystem.MinMaxCurve(width * 0.05f, width * 0.11f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.35f, 1f), new Color(1f, 1f, 0.9f, 1f));
        main.maxParticles = 80;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 45), new ParticleSystem.Burst(0.25f, 20) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 35f;
        shape.radius = width * 0.3f;
        shape.rotation = new Vector3(-90f, 0f, 0f); // cone opens upward

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        var twinkle = new AnimationCurve(new Keyframe(0f, 0.3f), new Keyframe(0.15f, 1f), new Keyframe(0.5f, 0.7f),
            new Keyframe(0.7f, 1f), new Keyframe(1f, 0f));
        size.size = new ParticleSystem.MinMaxCurve(1f, twinkle);

        var limit = ps.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.limit = width * 2f;
        limit.dampen = 0.05f;
        return ps;
    }

    private ParticleSystem CreateConfetti(Transform parent, Vector3 ground, float width)
    {
        var ps = NewSystem("CompletionConfetti", parent, ground + Vector3.up * width * 0.3f, confettiMaterial, false);
        var main = ps.main;
        main.duration = 1f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 2.6f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(width * 1.1f, width * 1.9f);
        main.startSize = new ParticleSystem.MinMaxCurve(width * 0.035f, width * 0.06f);
        var palette = new Gradient();
        palette.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1f, 0.36f, 0.42f), 0f), new GradientColorKey(new Color(1f, 0.8f, 0.25f), 0.25f),
                new GradientColorKey(new Color(0.35f, 0.85f, 0.5f), 0.5f), new GradientColorKey(new Color(0.3f, 0.65f, 1f), 0.75f),
                new GradientColorKey(new Color(0.75f, 0.5f, 1f), 1f)
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        main.startColor = new ParticleSystem.MinMaxGradient(palette) { mode = ParticleSystemGradientMode.RandomColor };
        main.maxParticles = 100;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 70) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 30f;
        shape.radius = width * 0.25f;
        shape.rotation = new Vector3(-90f, 0f, 0f);

        var limit = ps.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.limit = width * 0.6f;
        limit.dampen = 0.08f;

        var rotation = ps.rotationOverLifetime;
        rotation.enabled = true;
        rotation.z = new ParticleSystem.MinMaxCurve(-8f, 8f);

        // Flutter: a sideways wobble as they fall.
        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = width * 0.3f;
        noise.frequency = 1.5f;

        var color = ps.colorOverLifetime;
        color.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.75f), new GradientAlphaKey(0f, 1f) });
        color.color = new ParticleSystem.MinMaxGradient(fade);
        return ps;
    }
}
