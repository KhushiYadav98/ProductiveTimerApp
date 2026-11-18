using UnityEngine;

// Sound effects for the onboarding (all clips CC0, see Assets/Audio/SFX/CREDITS.txt):
//   - a pop when the mini town appears,
//   - small rising/falling blips while it's being scaled up/down (pitch follows the size),
//   - the bell ringing when it's touched (and, optionally, a soft ding on each swing of the bell hint),
//   - the sky building: glassy tinks around the user, climbing the sky with the tiles and speeding up,
//   - the town building: hammer knocks around the user at the height of the build line, then a finishing chime.
// Sounds are 3D, played from where things happen. A small pool of sources lets sounds overlap independently.
public class OnboardingSounds : MonoBehaviour
{
    [SerializeField] private OnboardingManager onboarding;
    [SerializeField] private BellHint bellHint;
    [SerializeField] private TownBuildReveal buildReveal;

    [Header("Mini Town Clips")]
    [SerializeField] private AudioClip townAppear;
    [SerializeField] private AudioClip scaleUp;
    [SerializeField] private AudioClip scaleDown;
    [SerializeField] private AudioClip bellRing;
    [Tooltip("Optional soft ding on each swing of the 'touch me' bell hint.")]
    [SerializeField] private AudioClip bellHintDing;

    [Header("Reveal Clips")]
    [Tooltip("Played (randomly) as sky tiles appear.")]
    [SerializeField] private AudioClip[] skyTinks;
    [Tooltip("Played (randomly) while the town builds up.")]
    [SerializeField] private AudioClip[] buildKnocks;
    [SerializeField] private AudioClip buildComplete;

    [Header("Volume")]
    [Range(0f, 1f)] [SerializeField] private float appearVolume = 0.8f;
    [Range(0f, 1f)] [SerializeField] private float scaleVolume = 0.45f;
    [Range(0f, 1f)] [SerializeField] private float bellVolume = 1f;
    [Range(0f, 1f)] [SerializeField] private float hintDingVolume = 0.25f;
    [SerializeField] private bool playHintDing = false;
    [Range(0f, 1f)] [SerializeField] private float skyVolume = 0.35f;
    [Range(0f, 1f)] [SerializeField] private float buildVolume = 0.55f;
    [Range(0f, 1f)] [SerializeField] private float completeVolume = 0.8f;

    [Header("Scaling")]
    [Tooltip("A blip plays every time the town's size changes by this fraction (0.08 = 8%).")]
    [SerializeField] private float scaleStep = 0.08f;
    [Tooltip("Shortest time between two scale blips, in seconds.")]
    [SerializeField] private float scaleCooldown = 0.07f;
    [Tooltip("Pitch at the town's spawn size and at x3 its spawn size.")]
    [SerializeField] private Vector2 scalePitchRange = new Vector2(0.9f, 1.35f);

    [Header("Reveal")]
    [Tooltip("Seconds between sky tinks at the start and at the end of the sky reveal.")]
    [SerializeField] private Vector2 skyInterval = new Vector2(0.2f, 0.06f);
    [Tooltip("Pitch of the sky tinks at the start and the end (rises as the sky fills).")]
    [SerializeField] private Vector2 skyPitch = new Vector2(0.85f, 1.45f);
    [Tooltip("How far from the user the sky tinks come from, in meters.")]
    [SerializeField] private float skyDistance = 6f;
    [Tooltip("Shortest time between two hammer knocks, in seconds.")]
    [SerializeField] private float minKnockGap = 0.06f;
    [Tooltip("Pop for small props popping up (empty = the mini town pop).")]
    [SerializeField] private AudioClip propPop;
    [Range(0f, 1f)] [SerializeField] private float propPopVolume = 0.3f;
    [Tooltip("Shortest time between two prop pops, in seconds.")]
    [SerializeField] private float minPropPopGap = 0.09f;
    [Tooltip("Pitch of the knocks at the start and the end (rises as the town goes up).")]
    [SerializeField] private Vector2 buildPitch = new Vector2(0.85f, 1.2f);

    private const int PoolSize = 8;
    private AudioSource[] pool;
    private int next;
    private Transform head;
    private Transform town;
    private float spawnScale;
    private float lastStepScale;
    private float lastScaleSoundTime = -1f;
    private float nextRevealSound;
    private float lastKnockTime = -1f;
    private float lastPropPopTime = -1f;

    private void Awake()
    {
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();
        if (bellHint == null)
            bellHint = FindFirstObjectByType<BellHint>(FindObjectsInactive.Include);
        if (buildReveal == null)
            buildReveal = FindFirstObjectByType<TownBuildReveal>(FindObjectsInactive.Include);
        var rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig != null ? rig.centerEyeAnchor : (Camera.main != null ? Camera.main.transform : transform);

        pool = new AudioSource[PoolSize];
        for (int i = 0; i < PoolSize; i++)
        {
            var go = new GameObject("Sfx" + i);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 1f;          // 3D, from where things happen
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 1f;
            s.maxDistance = 20f;
            s.dopplerLevel = 0f;
            pool[i] = s;
        }
    }

    private void OnEnable()
    {
        if (onboarding != null)
        {
            onboarding.Spawned += OnSpawned;
            onboarding.BellRung += OnBellRung;
            onboarding.TownRevealed += OnTownRevealed;
        }
        if (bellHint != null)
            bellHint.Rang += OnHintRang;
        if (buildReveal != null)
        {
            buildReveal.Hammer += OnHammer;
            buildReveal.PieceStarted += OnPieceStarted;
            buildReveal.BuildingFinished += OnBuildingFinished;
        }
    }

    private void OnDisable()
    {
        if (onboarding != null)
        {
            onboarding.Spawned -= OnSpawned;
            onboarding.BellRung -= OnBellRung;
            onboarding.TownRevealed -= OnTownRevealed;
        }
        if (bellHint != null)
            bellHint.Rang -= OnHintRang;
        if (buildReveal != null)
        {
            buildReveal.Hammer -= OnHammer;
            buildReveal.PieceStarted -= OnPieceStarted;
            buildReveal.BuildingFinished -= OnBuildingFinished;
        }
    }

    private void OnSpawned()
    {
        town = onboarding.MiniTown;
        spawnScale = lastStepScale = town != null ? town.localScale.x : 1f;
        Play(townAppear, town != null ? town.position : head.position, appearVolume, Random.Range(0.97f, 1.05f));
    }

    private void OnBellRung()
    {
        Vector3 at = bellHint != null ? bellHint.transform.position : (town != null ? town.position : head.position);
        Play(bellRing, at, bellVolume, 1f);
    }

    private void OnHintRang()
    {
        if (playHintDing && bellHint != null)
            Play(bellHintDing, bellHint.transform.position, hintDingVolume, Random.Range(0.97f, 1.03f));
    }

    private void OnTownRevealed()
    {
        Play(buildComplete, head.position + head.forward * 1.5f, completeVolume, 1f);
    }

    private void Update()
    {
        UpdateScaleSounds();
        UpdateRevealSounds();
    }

    private void UpdateScaleSounds()
    {
        // Only while the mini town is out, before the bell.
        if (town == null || !town.gameObject.activeInHierarchy || onboarding.IsConfirmed || lastStepScale <= 0f)
            return;

        float scale = town.localScale.x;
        float change = Mathf.Log(scale / lastStepScale);
        if (Mathf.Abs(change) < Mathf.Log(1f + scaleStep))
            return;

        lastStepScale = scale;
        if (Time.time - lastScaleSoundTime < scaleCooldown)
            return;
        lastScaleSoundTime = Time.time;

        // Higher pitch the bigger the town gets (relative to its spawn size, up to x3).
        float k = Mathf.InverseLerp(0f, Mathf.Log(3f), Mathf.Log(Mathf.Max(scale / spawnScale, 1f)));
        Play(change > 0f ? scaleUp : scaleDown, town.position, scaleVolume, Mathf.Lerp(scalePitchRange.x, scalePitchRange.y, k));
    }

    private void UpdateRevealSounds()
    {
        if (buildReveal == null || buildReveal.Phase != TownBuildReveal.RevealPhase.Sky)
        {
            nextRevealSound = 0f;
            return;
        }
        if (Time.time < nextRevealSound)
            return;

        float p = buildReveal.Progress;
        Vector3 around = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;

        if (buildReveal.Phase == TownBuildReveal.RevealPhase.Sky)
        {
            // Tiles fill bottom to top: the tinks climb from the horizon to overhead and speed up.
            float elevation = Mathf.Lerp(-10f, 75f, p) + Random.Range(-12f, 12f);
            Vector3 dir = Quaternion.AngleAxis(-elevation, Vector3.Cross(Vector3.up, around)) * around;
            Play(Pick(skyTinks), head.position + dir.normalized * skyDistance, skyVolume * Random.Range(0.7f, 1f),
                Mathf.Lerp(skyPitch.x, skyPitch.y, p) * Random.Range(0.94f, 1.06f));
            nextRevealSound = Time.time + Mathf.Lerp(skyInterval.x, skyInterval.y, p) * Random.Range(0.7f, 1.3f);
        }
        // The town's construction sounds come from its pieces (see OnHammer / OnPieceStarted / OnBuildingFinished).
    }

    // A hammer blow on a building under construction, right where it's being built.
    private void OnHammer(Vector3 at, float size)
    {
        if (Time.time - lastKnockTime < minKnockGap)
            return;
        lastKnockTime = Time.time;
        Play(Pick(buildKnocks), at, buildVolume * Random.Range(0.7f, 1f),
            Mathf.Lerp(buildPitch.x, buildPitch.y, buildReveal.Progress) * Random.Range(0.92f, 1.08f));
    }

    // Big buildings start with a knock; small props pop up with a soft pop (rate-limited: there are hundreds).
    private void OnPieceStarted(Vector3 at, float size, bool big)
    {
        if (big)
        {
            OnHammer(at, size);
            return;
        }
        if (Time.time - lastPropPopTime < minPropPopGap)
            return;
        lastPropPopTime = Time.time;
        Play(propPop != null ? propPop : townAppear, at, propPopVolume * Random.Range(0.6f, 1f), Random.Range(1.1f, 1.5f));
    }

    // A building is complete: a heavier, lower knock.
    private void OnBuildingFinished(Vector3 at, float size)
    {
        Play(Pick(buildKnocks), at, Mathf.Min(1f, buildVolume * 1.3f), Random.Range(0.7f, 0.8f));
    }

    private static AudioClip Pick(AudioClip[] clips)
    {
        return clips == null || clips.Length == 0 ? null : clips[Random.Range(0, clips.Length)];
    }

    private void Play(AudioClip clip, Vector3 position, float volume, float pitch)
    {
        if (clip == null)
            return;
        var s = pool[next];
        next = (next + 1) % pool.Length;
        s.transform.position = position;
        s.pitch = pitch;
        s.clip = clip;
        s.volume = volume;
        s.Play();
    }
}
