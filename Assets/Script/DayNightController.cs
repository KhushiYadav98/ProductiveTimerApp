using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Day/night look of the life-size town, driven by one value: Night (0 = day, 1 = night).
//   - Sun light: warm daylight -> dim cool moonlight.        - Ambient: cozy day gradient -> deep blue night.
//   - Sky sphere: day texture -> night texture (swapped at the darkest point of a dip, so it's seamless).
//   - Stars twinkle in the sky.                               - Street lamps glow, with soft light pools below.
//   - Windows (emissive materials) shine brighter.
// Cheap on Quest: no extra realtime lights; glows and pools are particles; windows use runtime copies of the few
// emissive materials (so draw batching is kept).
public class DayNightController : MonoBehaviour
{
    [SerializeField] private OnboardingManager onboarding;
    [SerializeField] private Light sun;
    [SerializeField] private Renderer skySphere;
    [Tooltip("Parent of the town's objects (lamps, houses...), e.g. Town.")]
    [SerializeField] private Transform townRoot;

    [Header("Night look")]
    [SerializeField] private Texture nightSkyTexture;
    [SerializeField] private float nightSunIntensity = 0.35f;
    [SerializeField] private Color nightSunColor = new Color(0.55f, 0.66f, 1f);
    [SerializeField] private Color nightAmbientSky = new Color(0.16f, 0.2f, 0.38f);
    [SerializeField] private Color nightAmbientEquator = new Color(0.14f, 0.15f, 0.28f);
    [SerializeField] private Color nightAmbientGround = new Color(0.06f, 0.06f, 0.1f);
    [Tooltip("How much brighter emissive windows/signs get at night.")]
    [SerializeField] private float nightWindowBoost = 2.2f;

    [Header("Lamps")]
    [Tooltip("Town objects whose names start with this are street lamps.")]
    [SerializeField] private string lampPrefix = "Lamp_";
    [SerializeField] private Material glowMaterial;
    [SerializeField] private Color lampColor = new Color(1f, 0.78f, 0.42f, 1f);
    [Tooltip("Glow size and light-pool size, as multiples of the lamp's height.")]
    [SerializeField] private float glowSize = 0.35f;
    [SerializeField] private float poolSize = 1.6f;

    [Header("Stars")]
    [SerializeField] private Material starMaterial;
    [SerializeField] private int starCount = 260;

    public float Night { get; private set; }
    public bool IsNight => Night > 0.5f;

    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int IntensityId = Shader.PropertyToID("_Intensity");

    private bool ready;
    private float daySunIntensity;
    private Color daySunColor;
    private Color dayAmbientSky, dayAmbientEquator, dayAmbientGround;
    private Material skyMaterial;
    private Texture dayBaseMap, dayEmissionMap;
    private Color daySkyBase, daySkyEmission;
    private ParticleSystem glows, pools, stars;
    private readonly Dictionary<Material, Material> emissiveCopies = new Dictionary<Material, Material>();
    private readonly List<(Material copy, Color emission)> windows = new List<(Material, Color)>();
    private MaterialPropertyBlock block;
    private Coroutine fade;

    private void Awake()
    {
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();
        if (sun == null)
            sun = RenderSettings.sun;
        block = new MaterialPropertyBlock();
    }

    private void OnEnable()
    {
        if (onboarding != null)
            onboarding.TownRevealed += Setup;
    }

    private void OnDisable()
    {
        if (onboarding != null)
            onboarding.TownRevealed -= Setup;
    }

    private void OnDestroy()
    {
        foreach (var copy in emissiveCopies.Values)
            if (copy != null)
                Destroy(copy);
    }

    /// <summary>Fades to night (1) or day (0) over <paramref name="seconds"/>.</summary>
    public void SetNight(float target, float seconds)
    {
        if (fade != null)
            StopCoroutine(fade);
        fade = StartCoroutine(FadeTo(Mathf.Clamp01(target), seconds));
    }

    /// <summary>Sets the blend right away (e.g. while the user drags the sun or moon).</summary>
    public void SetNightImmediate(float value)
    {
        if (fade != null)
        {
            StopCoroutine(fade);
            fade = null;
        }
        Apply(Mathf.Clamp01(value));
    }

    private IEnumerator FadeTo(float target, float seconds)
    {
        float from = Night;
        for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(seconds, 0.01f))
        {
            Apply(Mathf.Lerp(from, target, t * t * (3f - 2f * t)));
            yield return null;
        }
        Apply(target);
        fade = null;
    }

    // Captures the day look and builds the night extras, once the town is in place.
    private void Setup()
    {
        if (ready)
            return;
        ready = true;

        if (sun != null)
        {
            daySunIntensity = sun.intensity;
            daySunColor = sun.color;
        }
        dayAmbientSky = RenderSettings.ambientSkyColor;
        dayAmbientEquator = RenderSettings.ambientEquatorColor;
        dayAmbientGround = RenderSettings.ambientGroundColor;

        if (skySphere != null)
        {
            skyMaterial = skySphere.material; // the sphere's own instance
            dayBaseMap = skyMaterial.GetTexture(BaseMapId);
            dayEmissionMap = skyMaterial.HasProperty(EmissionMapId) ? skyMaterial.GetTexture(EmissionMapId) : null;
            daySkyBase = skyMaterial.GetColor(BaseColorId);
            daySkyEmission = skyMaterial.HasProperty(EmissionColorId) ? skyMaterial.GetColor(EmissionColorId) : Color.black;
        }

        if (townRoot != null)
        {
            BuildLamps();
            // Emissive materials (windows, signs...) get one runtime copy each, shared by all their renderers.
            foreach (var r in townRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (r == skySphere || r is ParticleSystemRenderer)
                    continue;
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || !m.IsKeywordEnabled("_EMISSION") || !m.HasProperty(EmissionColorId))
                        continue;
                    if (!emissiveCopies.TryGetValue(m, out var copy))
                    {
                        Color e = m.GetColor(EmissionColorId);
                        if (e.maxColorComponent <= 0.01f)
                            continue;
                        copy = new Material(m) { name = m.name + " (DayNight)" };
                        emissiveCopies[m] = copy;
                        windows.Add((copy, e));
                    }
                    mats[i] = copy;
                    changed = true;
                }
                if (changed)
                    r.sharedMaterials = mats;
            }
        }
        BuildStars();
        Apply(Night);
    }

    private void Apply(float n)
    {
        Night = n;
        if (!ready)
            return;

        if (sun != null)
        {
            sun.intensity = Mathf.Lerp(daySunIntensity, nightSunIntensity, n);
            sun.color = Color.Lerp(daySunColor, nightSunColor, n);
        }
        RenderSettings.ambientSkyColor = Color.Lerp(dayAmbientSky, nightAmbientSky, n);
        RenderSettings.ambientEquatorColor = Color.Lerp(dayAmbientEquator, nightAmbientEquator, n);
        RenderSettings.ambientGroundColor = Color.Lerp(dayAmbientGround, nightAmbientGround, n);

        // Sky: darken toward the middle, swap textures there, brighten into the night texture.
        if (skyMaterial != null)
        {
            bool nightTex = n >= 0.5f && nightSkyTexture != null;
            float dip = 1f - (n < 0.5f ? n * 2f : (1f - n) * 2f) * 0.85f;   // 1 at the ends, 0.15 in the middle
            if (nightSkyTexture == null)
                dip = Mathf.Lerp(1f, 0.25f, n);                              // no night texture: just darken
            skyMaterial.SetTexture(BaseMapId, nightTex ? nightSkyTexture : dayBaseMap);
            if (dayEmissionMap != null)
                skyMaterial.SetTexture(EmissionMapId, nightTex ? nightSkyTexture : dayEmissionMap);
            skyMaterial.SetColor(BaseColorId, daySkyBase * dip);
            skyMaterial.SetColor(EmissionColorId, daySkyEmission * dip);
        }

        float lights = Mathf.Clamp01((n - 0.35f) / 0.5f); // lamps come on as it gets dark
        SetParticles(glows, lights);
        SetParticles(pools, lights * 0.8f);
        SetParticles(stars, Mathf.Clamp01((n - 0.5f) / 0.4f));

        foreach (var (copy, e) in windows)
            copy.SetColor(EmissionColorId, e * (1f + nightWindowBoost * lights));
    }

    private void SetParticles(ParticleSystem ps, float amount)
    {
        if (ps == null)
            return;
        var r = ps.GetComponent<ParticleSystemRenderer>();
        r.enabled = amount > 0.001f;
        r.GetPropertyBlock(block);
        block.SetFloat(IntensityId, amount);
        r.SetPropertyBlock(block);
    }

    // A warm glow on each street lamp's head and a soft light pool on the ground under it.
    private void BuildLamps()
    {
        if (glowMaterial == null)
            return;
        var heads = new List<(Vector3 pos, float h)>();
        foreach (Transform child in townRoot)
        {
            if (!child.name.StartsWith(lampPrefix) || !child.gameObject.activeInHierarchy)
                continue;
            var rs = child.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0)
                continue;
            Bounds b = rs[0].bounds;
            foreach (var r in rs)
                b.Encapsulate(r.bounds);
            heads.Add((new Vector3(b.center.x, b.max.y - b.size.y * 0.06f, b.center.z), b.size.y));
        }
        if (heads.Count == 0)
            return;

        glows = NewSystem("LampGlows", ParticleSystemRenderMode.Billboard, heads.Count);
        pools = NewSystem("LampPools", ParticleSystemRenderMode.HorizontalBillboard, heads.Count);
        var p = new ParticleSystem.EmitParams { startLifetime = 1e6f, velocity = Vector3.zero };
        foreach (var (pos, h) in heads)
        {
            p.position = pos;
            p.startSize = h * glowSize;
            p.startColor = lampColor;
            glows.Emit(p, 1);
            p.position = new Vector3(pos.x, pos.y - h * 0.94f + h * 0.02f, pos.z);
            p.startSize = h * poolSize;
            p.startColor = new Color(lampColor.r, lampColor.g * 0.95f, lampColor.b * 0.8f, 0.55f);
            pools.Emit(p, 1);
        }
    }

    private ParticleSystem NewSystem(string name, ParticleSystemRenderMode mode, int count)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = count;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        shape.enabled = false;
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = mode;
        r.sharedMaterial = glowMaterial;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        ps.Play();
        return ps;
    }

    // Twinkling stars across the upper sky, just inside the sky sphere.
    private void BuildStars()
    {
        if (starMaterial == null || skySphere == null)
            return;
        Bounds b = skySphere.bounds;
        float radius = Mathf.Min(b.extents.x, b.extents.z) * 0.9f;

        var go = new GameObject("NightStars");
        go.transform.SetParent(transform, false);
        go.transform.SetPositionAndRotation(b.center, Quaternion.Euler(-90f, 0f, 0f)); // hemisphere opens upward
        stars = go.AddComponent<ParticleSystem>();
        stars.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = stars.main;
        main.loop = true;
        main.duration = 5f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 7f);
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(radius * 0.006f, radius * 0.016f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 1f, 1f, 1f), new Color(1f, 0.93f, 0.8f, 1f));
        main.maxParticles = starCount;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        main.prewarm = true;
        var emission = stars.emission;
        emission.rateOverTime = starCount / 5f;
        var shape = stars.shape;
        shape.shapeType = ParticleSystemShapeType.Hemisphere;
        shape.radius = radius;
        shape.radiusThickness = 0f;
        var col = stars.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.3f), new GradientAlphaKey(0.6f, 0.6f), new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(g);
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = starMaterial;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        stars.Play();
    }
}
