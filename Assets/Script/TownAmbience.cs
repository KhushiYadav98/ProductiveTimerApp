using UnityEngine;

// Background audio for the life-size town, cross-faded by mode:
//   - the town ambience loop once the town has been built, and during breaks / outside a focus session,
//   - the focus music loop while the focus timer is running (not paused).
// Both loops keep playing underneath (only their volumes fade), so each picks up where it was.
public class TownAmbience : MonoBehaviour
{
    [SerializeField] private OnboardingManager onboarding;
    [SerializeField] private TownTaskTimer timer;
    [SerializeField] private AudioClip townLoop;
    [SerializeField] private AudioClip focusLoop;
    [Range(0f, 1f)] [SerializeField] private float townVolume = 0.5f;
    [Range(0f, 1f)] [SerializeField] private float focusVolume = 0.45f;
    [Tooltip("Cross-fade time between the town and focus audio, in seconds.")]
    [SerializeField] private float fadeTime = 1.5f;

    public bool IsFocusMode => timer != null && timer.IsRunning && !timer.IsPaused;

    private AudioSource town;
    private AudioSource focus;
    private bool townShown;

    private void Awake()
    {
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();
        if (timer == null)
            timer = FindFirstObjectByType<TownTaskTimer>();
        town = NewLoop("TownLoop", townLoop);
        focus = NewLoop("FocusLoop", focusLoop);
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

        // Don't keep decoding silent loops forever before the town exists.
        if (!townShown && town.volume <= 0f && focus.volume <= 0f)
        {
            if (town.isPlaying) town.Stop();
            if (focus.isPlaying) focus.Stop();
        }
    }
}
