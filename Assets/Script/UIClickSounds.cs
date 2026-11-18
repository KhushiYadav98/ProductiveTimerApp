using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Click sound on every UI Button in the scene (a lighter tick for the poke keyboard's keys), played from the button.
// Buttons created later (e.g. keyboard keys built at runtime) are picked up by a periodic re-scan.
// Clips are CC0 (see Assets/Audio/SFX/CREDITS.txt).
public class UIClickSounds : MonoBehaviour
{
    [SerializeField] private AudioClip buttonClick;
    [SerializeField] private AudioClip keyClick;
    [Range(0f, 1f)] [SerializeField] private float buttonVolume = 0.7f;
    [Range(0f, 1f)] [SerializeField] private float keyVolume = 0.5f;
    [Tooltip("Seconds between scans for new buttons.")]
    [SerializeField] private float rescanInterval = 1f;

    private readonly HashSet<Button> hooked = new HashSet<Button>();
    private AudioSource source;
    private float nextScan;

    private void Awake()
    {
        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0.7f;
        source.dopplerLevel = 0f;
        source.minDistance = 1f;
    }

    private void Start() => Scan();

    private void Update()
    {
        if (Time.unscaledTime < nextScan)
            return;
        nextScan = Time.unscaledTime + rescanInterval;
        Scan();
    }

    private void Scan()
    {
        foreach (var button in FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (button == null || hooked.Contains(button))
                continue;
            hooked.Add(button);
            bool isKey = button.GetComponentInParent<PokeKeyboard>(true) != null;
            var b = button;
            b.onClick.AddListener(() => Click(b, isKey));
        }
    }

    private void Click(Button button, bool isKey)
    {
        var clip = isKey && keyClick != null ? keyClick : buttonClick;
        if (clip == null)
            return;
        source.transform.position = button.transform.position;
        source.pitch = isKey ? Random.Range(0.95f, 1.08f) : 1f;
        source.PlayOneShot(clip, isKey ? keyVolume : buttonVolume);
    }
}
