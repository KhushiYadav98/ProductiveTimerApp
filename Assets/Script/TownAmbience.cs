using UnityEngine;

// Background audio for the life-size town, cross-faded by mode:
//   - the town ambience loop once the town has been built, and during breaks / outside a focus session,
//   - the focus music loop while the focus timer is running (not paused), plus a very quiet train-on-the-tracks loop
//     coming from the train as it does its lap.
// Both loops keep playing underneath (only their volumes fade), so each picks up where it was.
public class TownAmbience : MonoBehaviour
{
    [SerializeField] private OnboardingManager onboarding;
    [SerializeField] private TownTaskTimer timer;
    [SerializeField] private AudioClip townLoop;
    [SerializeField] private AudioClip focusLoop;
    [Range(0f, 1f)] [SerializeField] private float townVolume = 0.5f;
    [Range(0f, 1f)] [SerializeField] private float focusVolume = 0.45f;
    [Tooltip("Soft clickety-clack loop, played from the train while focusing.")]
    [SerializeField] private AudioClip trainLoop;
    [Tooltip("Where the train sound comes from (e.g. the locomotive).")]
    [SerializeField] private Transform trainEmitter;
    [Range(0f, 1f)] [SerializeField] private float trainVolume = 0.12f;
    [Tooltip("Cross-fade time between the town and focus audio, in seconds.")]
    [SerializeField] private float fadeTime = 1.5f;

    public bool IsFocusMode => timer != null && timer.IsRunning && !timer.IsPaused;

    private AudioSource town;
    private AudioSource focus;
    private AudioSource train;
    private bool townShown;

    private void Awake()
    {
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();
        if (timer == null)
            timer = FindFirstObjectByType<TownTaskTimer>();
        town = NewLoop("TownLoop", townLoop);
        focus = NewLoop("FocusLoop", focusLoop);
        train = NewLoop("TrainLoop", trainLoop);
        // 3D, but heard gently from anywhere in the town.
        train.spatialBlend = 0.7f;
        train.rolloffMode = AudioRolloffMode.Linear;
        train.minDistance = 2f;
        train.maxDistance = 30f;
        train.dopplerLevel = 0f;
    }

    private AudioSource NewLoop(string name, AudioClip clip)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var s = go.AddComponent<AudioSource>();
        s.clip = clip;
        s.loop = true;
        s.playOnAwake = false;
        s.spatialBlend = 0f; // all around the user
        s.volume = 0f;
        return s;
    }

    private void OnEnable()
    {
        if (onboarding != null)
        {
            onboarding.TownRevealed += OnTownRevealed;
            onboarding.Spawned += OnSpawned;
        }
    }

    private void OnDisable()
    {
        if (onboarding != null)
        {
            onboarding.TownRevealed -= OnTownRevealed;
            onboarding.Spawned -= OnSpawned;
        }
    }

    private void OnTownRevealed()
    {
        townShown = true;
        if (town.clip != null && !town.isPlaying)
            town.Play();
        if (focus.clip != null && !focus.isPlaying)
            focus.Play();
        if (train.clip != null && !train.isPlaying)
            train.Play();
    }

    // Respawning goes back to the mini town: fade everything out.
    private void OnSpawned() => townShown = false;

    private void Update()
    {
        float townTarget = townShown && !IsFocusMode ? townVolume : 0f;
        float focusTarget = townShown && IsFocusMode ? focusVolume : 0f;
        float step = Time.deltaTime / Mathf.Max(fadeTime, 0.01f);
        town.volume = Mathf.MoveTowards(town.volume, townTarget, step * Mathf.Max(townVolume, 0.01f));
        focus.volume = Mathf.MoveTowards(focus.volume, focusTarget, step * Mathf.Max(focusVolume, 0.01f));
        float trainTarget = townShown && IsFocusMode ? trainVolume : 0f;
        train.volume = Mathf.MoveTowards(train.volume, trainTarget, step * Mathf.Max(trainVolume, 0.01f));
        if (trainEmitter != null)
            train.transform.position = trainEmitter.position;

        // Don't keep decoding silent loops forever before the town exists.
        if (!townShown && town.volume <= 0f && focus.volume <= 0f && train.volume <= 0f)
        {
            if (town.isPlaying) town.Stop();
            if (focus.isPlaying) focus.Stop();
            if (train.isPlaying) train.Stop();
        }
    }
}
