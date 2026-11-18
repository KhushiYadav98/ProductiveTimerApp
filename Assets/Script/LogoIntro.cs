using System.Collections;
using UnityEngine;

// Intro once the town has been built: the logo pops up in front of the user (a little further out than the task
// panels) while clouds burst out of it and spiral away around the user, with sound; after a few seconds the logo
// pops away and the given panel pops in. Clips are CC0 (see Assets/Audio/SFX/CREDITS.txt).
public class LogoIntro : MonoBehaviour
{
    [SerializeField] private RectTransform logo;

    [Header("Logo")]
    [Tooltip("Distance of the logo in front of the head, in meters.")]
    [SerializeField] private float logoDistance = 1f;
    [Tooltip("Height of the logo's center relative to the eyes, in meters.")]
    [SerializeField] private float logoHeight = 0.15f;
    [Tooltip("World width of the logo, in meters (0 keeps its scene size).")]
    [SerializeField] private float logoWidth = 0.72f;
    [SerializeField] private float popInTime = 0.5f;
    [Tooltip("How long the logo stays up, in seconds.")]
    [SerializeField] private float holdTime = 3f;
    [SerializeField] private float popOutTime = 0.3f;
    [Tooltip("Gentle float of the logo while it's up, in meters.")]
    [SerializeField] private float floatAmount = 0.015f;
    [SerializeField] private float panelPopTime = 0.35f;

    [Header("Clouds")]
    [Tooltip("ProductivityTown/Particle Soft material (Puff shape), e.g. FX_Cloud.")]
    [SerializeField] private Material cloudMaterial;
    [SerializeField] private int cloudCount = 90;
    [Tooltip("Seconds over which the clouds come out of the logo.")]
    [SerializeField] private float cloudEmitTime = 2.2f;
    [SerializeField] private Vector2 cloudLifetime = new Vector2(5f, 7.5f);
    [SerializeField] private Vector2 cloudSize = new Vector2(0.45f, 1.1f);
    [Tooltip("Outward speed away from the user (m/s) and orbit speed around the user (rad/s).")]
    [SerializeField] private Vector2 cloudOutward = new Vector2(0.35f, 0.8f);
    [SerializeField] private Vector2 cloudOrbit = new Vector2(0.2f, 0.55f);
    [SerializeField] private Color cloudColorA = new Color(1f, 1f, 1f, 1f);
    [SerializeField] private Color cloudColorB = new Color(0.9f, 0.95f, 1f, 0.95f);

    [Header("Audio")]
    [SerializeField] private AudioClip logoPop;
    [SerializeField] private AudioClip logoOut;
    [SerializeField] private AudioClip panelAppear;
    [Tooltip("Played as the clouds burst out, and again as some of them fly past.")]
    [SerializeField] private AudioClip cloudWhoosh;
    [Tooltip("Looping wind under the clouds, faded in and out.")]
    [SerializeField] private AudioClip windLoop;
    [Range(0f, 1f)] [SerializeField] private float popVolume = 0.8f;
    [Range(0f, 1f)] [SerializeField] private float whooshVolume = 0.6f;
    [Range(0f, 1f)] [SerializeField] private float windVolume = 0.35f;
    [Tooltip("Extra whooshes from clouds flying past, after the first one.")]
    [SerializeField] private int extraWhooshes = 3;

    public bool IsPlaying => routine != null;

    private Coroutine routine;
    private Vector3 logoRestScale;
    private CanvasGroup logoGroup;
    private ParticleSystem clouds;
    private AudioSource[] oneShots;
    private int nextShot;
    private AudioSource wind;

    private void Awake()
    {
        if (logo != null)
        {
            logoRestScale = logo.localScale;
            logoGroup = logo.GetComponent<CanvasGroup>();
            if (logoGroup == null)
                logoGroup = logo.gameObject.AddComponent<CanvasGroup>();
            logoGroup.blocksRaycasts = false;
            logo.gameObject.SetActive(false);
        }

        oneShots = new AudioSource[4];
        for (int i = 0; i < oneShots.Length; i++)
            oneShots[i] = NewSource("LogoSfx" + i, false);
        wind = NewSource("LogoWind", true);
        wind.spatialBlend = 0f; // wind all around the user
    }

    private AudioSource NewSource(string name, bool loop)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var s = go.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.spatialBlend = 1f;
        s.rolloffMode = AudioRolloffMode.Linear;
        s.minDistance = 1f;
        s.maxDistance = 20f;
        s.dopplerLevel = 0f;
        return s;
    }

    /// <summary>Plays the logo intro in front of <paramref name="head"/>, then pops in <paramref name="panel"/>
    /// (after <paramref name="showPanel"/> has activated it).</summary>
    public void Play(Transform head, Transform panel, System.Action showPanel)
    {
        Stop();
        routine = StartCoroutine(Run(head, panel, showPanel));
    }

    public void Stop()
    {
        if (routine != null)
        {
            StopCoroutine(routine);
            routine = null;
        }
        if (logo != null)
        {
            logo.localScale = logoRestScale;
            logo.gameObject.SetActive(false);
        }
        if (wind != null)
            wind.Stop();
    }

    private IEnumerator Run(Transform head, Transform panel, System.Action showPanel)
    {
        // Place the logo in front of the user, a bit further out than the panels, facing them.
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
        forward.Normalize();
        Vector3 logoPos = head.position + forward * logoDistance + Vector3.up * logoHeight;

        Vector3 fullScale = logoRestScale;
        if (logo != null)
        {
            logo.SetPositionAndRotation(logoPos, Quaternion.LookRotation(forward, Vector3.up));
            logo.localScale = logoRestScale;
            if (logoWidth > 0f)
            {
                float width = logo.rect.width * logo.lossyScale.x;
                if (width > 0.0001f)
                    fullScale = logoRestScale * (logoWidth / width);
            }
            logo.localScale = Vector3.zero;
            logoGroup.alpha = 1f;
            logo.gameObject.SetActive(true);
        }

        // Pop in, with the clouds bursting out and the wind rising.
        PlayAt(logoPop, logoPos, popVolume, 1f);
        PlayAt(cloudWhoosh, logoPos, whooshVolume, 1f);
        StartClouds(head.position, logoPos);
        float cloudsEnd = Time.time + cloudEmitTime + cloudLifetime.y;
        StartCoroutine(Wind(cloudsEnd));
        StartCoroutine(PassingWhooshes());

        for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(popInTime, 0.01f))
        {
            if (logo != null)
                logo.localScale = fullScale * EaseOutBack(t);
            yield return null;
        }

        // Hold, floating gently.
        float holdStart = Time.time;
        while (Time.time - holdStart < holdTime)
        {
            if (logo != null)
            {
                logo.localScale = fullScale;
                logo.position = logoPos + Vector3.up * Mathf.Sin((Time.time - holdStart) * 2f) * floatAmount;
            }
            yield return null;
        }

        // Pop out: a small swell, then shrink and fade.
        PlayAt(logoOut, logoPos, popVolume * 0.8f, 1f);
        for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(popOutTime, 0.01f))
        {
            if (logo != null)
            {
                float s = t < 0.25f ? 1f + 0.08f * (t / 0.25f) : 1.08f * (1f - (t - 0.25f) / 0.75f);
                logo.localScale = fullScale * Mathf.Max(s, 0f);
                logoGroup.alpha = 1f - Mathf.Clamp01((t - 0.3f) / 0.7f);
            }
            yield return null;
        }
        if (logo != null)
        {
            logo.gameObject.SetActive(false);
            logo.localScale = logoRestScale;
        }

        // The panel pops in.
        showPanel?.Invoke();
        if (panel != null)
        {
            PlayAt(panelAppear, panel.position, popVolume, 1f);
            Vector3 panelScale = panel.localScale;
            for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(panelPopTime, 0.01f))
            {
                panel.localScale = panelScale * EaseOutBack(t);
                yield return null;
            }
            panel.localScale = panelScale;
        }
        routine = null;
    }

    // Clouds come out of the logo and spiral away around the user: radial (outward from the user) plus orbit
    // (around the user's vertical axis), drifting up a little, growing and fading.
    private void StartClouds(Vector3 center, Vector3 from)
    {
        if (cloudMaterial == null)
            return;
        if (clouds == null)
            clouds = CreateClouds();

        clouds.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        clouds.transform.SetPositionAndRotation(center, Quaternion.identity);
        var shape = clouds.shape;
        shape.position = from - center;
        clouds.Play();
    }

    private ParticleSystem CreateClouds()
    {
        var go = new GameObject("LogoClouds");
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.duration = cloudEmitTime;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Shape;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        main.startLifetime = new ParticleSystem.MinMaxCurve(cloudLifetime.x, cloudLifetime.y);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(cloudSize.x, cloudSize.y);
        main.startColor = new ParticleSystem.MinMaxGradient(cloudColorA, cloudColorB);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.maxParticles = cloudCount + 10;

        var emission = ps.emission;
        // Front-loaded: a big puff as the logo appears, then a stream.
        emission.rateOverTime = new ParticleSystem.MinMaxCurve(cloudCount * 0.6f / cloudEmitTime);
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)(cloudCount * 0.4f)) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.2f;

        var velocity = ps.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.radial = new ParticleSystem.MinMaxCurve(cloudOutward.x, cloudOutward.y);
        velocity.orbitalY = new ParticleSystem.MinMaxCurve(cloudOrbit.x, cloudOrbit.y);
        velocity.y = new ParticleSystem.MinMaxCurve(0.02f, 0.12f);
        // Radial/orbital need the other axes set in the same mode.
        velocity.x = new ParticleSystem.MinMaxCurve(0f, 0f);
        velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);
        velocity.orbitalX = new ParticleSystem.MinMaxCurve(0f, 0f);
        velocity.orbitalZ = new ParticleSystem.MinMaxCurve(0f, 0f);

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.4f, 1f, 1.6f));

        var color = ps.colorOverLifetime;
        color.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.1f), new GradientAlphaKey(0.85f, 0.7f), new GradientAlphaKey(0f, 1f) });
        color.color = new ParticleSystem.MinMaxGradient(g);

        var rotation = ps.rotationOverLifetime;
        rotation.enabled = true;
        rotation.z = new ParticleSystem.MinMaxCurve(-0.3f, 0.3f);

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = cloudMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sortMode = ParticleSystemSortMode.Distance;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return ps;
    }

    // A few whooshes from clouds as they fly around the user.
    private IEnumerator PassingWhooshes()
    {
        if (cloudWhoosh == null || clouds == null)
            yield break;
        var particles = new ParticleSystem.Particle[64];
        for (int i = 0; i < extraWhooshes; i++)
        {
            yield return new WaitForSeconds(Random.Range(0.9f, 1.6f));
            int n = clouds.GetParticles(particles);
            if (n == 0)
                yield break;
            Vector3 at = clouds.transform.TransformPoint(particles[Random.Range(0, n)].position);
            PlayAt(cloudWhoosh, at, whooshVolume * Random.Range(0.5f, 0.8f), Random.Range(0.85f, 1.15f));
        }
    }

    private IEnumerator Wind(float until)
    {
        if (windLoop == null)
            yield break;
        wind.clip = windLoop;
        wind.volume = 0f;
        wind.Play();
        const float fade = 1.2f;
        float start = Time.time;
        while (Time.time < until)
        {
            float fadeIn = Mathf.Clamp01((Time.time - start) / fade);
            float fadeOut = Mathf.Clamp01((until - Time.time) / (fade * 2f));
            wind.volume = windVolume * Mathf.Min(fadeIn, fadeOut);
            yield return null;
        }
        wind.Stop();
    }

    private void PlayAt(AudioClip clip, Vector3 position, float volume, float pitch)
    {
        if (clip == null)
            return;
        var s = oneShots[nextShot];
        nextShot = (nextShot + 1) % oneShots.Length;
        s.transform.position = position;
        s.clip = clip;
        s.volume = volume;
        s.pitch = pitch;
        s.Play();
    }

    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float x = t - 1f;
        return 1f + c3 * x * x * x + c1 * x * x;
    }
}
