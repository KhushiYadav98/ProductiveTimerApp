using System.Collections;
using UnityEngine;

// Day/night switch with the sun and the moon. In the day the sun floats above the user on the left, within reach;
// at night the moon floats above them on the right. Grab the one in the sky and pull it down: it slides along a
// fixed arc (it doesn't follow the hand freely) and the town dims with it. Let go past the middle and it sets and
// disappears, the other one rises on its own arc on the other side, and the town switches mode; let go earlier and
// it springs back up. Positions are set relative to the user when the town appears.
public class DayNightSwitcher : MonoBehaviour
{
    public enum Body { Sun, Moon }

    [SerializeField] private OnboardingManager onboarding;
    [SerializeField] private DayNightController dayNight;
    [SerializeField] private Transform sun;
    [SerializeField] private Transform moon;

    [Header("Arcs (meters, relative to the user: x = right, y = up, z = forward)")]
    [Tooltip("Where the sun sits in the day (up, on the left, within reach).")]
    [SerializeField] private Vector3 sunUp = new Vector3(-0.3f, 0.22f, 0.32f);
    [Tooltip("Bend of the arc it travels down along.")]
    [SerializeField] private Vector3 sunBend = new Vector3(-0.58f, 0.12f, 0.42f);
    [Tooltip("Where it sets (out and down, where it disappears).")]
    [SerializeField] private Vector3 sunDown = new Vector3(-0.8f, -0.38f, 0.48f);
    [Tooltip("The moon's arc mirrors the sun's on the right side.")]
    [SerializeField] private bool mirrorForMoon = true;

    [Header("Feel")]
    [Tooltip("Let go beyond this far along the arc and it sets; otherwise it springs back.")]
    [Range(0.1f, 0.9f)] [SerializeField] private float switchPoint = 0.4f;
    [SerializeField] private float setTime = 0.7f;
    [SerializeField] private float riseTime = 1.6f;
    [SerializeField] private float springBackTime = 0.4f;
    [Tooltip("From this far along the arc it shrinks and fades away.")]
    [Range(0.3f, 1f)] [SerializeField] private float vanishFrom = 0.7f;
    [SerializeField] private float idleBob = 0.008f;

    [Header("Audio")]
    [SerializeField] private AudioClip grabSound;
    [SerializeField] private AudioClip switchSound;
    [Range(0f, 1f)] [SerializeField] private float volume = 0.6f;

    public bool IsNight { get; private set; }
    public bool IsReady { get; private set; }

    private Transform head;
    private Vector3 origin;
    private Quaternion frame;
    private float sunT = 1f, moonT = 1f; // 0 = up in the sky, 1 = set (hidden)
    private Body? dragging;
    private bool animating;
    private Vector3 sunScale, moonScale;
    private AudioSource source;

    private void Awake()
    {
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();
        if (dayNight == null)
            dayNight = FindFirstObjectByType<DayNightController>();
        var rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;
        if (sun != null) { sunScale = sun.localScale; sun.gameObject.SetActive(false); }
        if (moon != null) { moonScale = moon.localScale; moon.gameObject.SetActive(false); }
        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0.8f;
    }

    private void OnEnable()
    {
        if (onboarding != null)
            onboarding.TownRevealed += Appear;
    }

    private void OnDisable()
    {
        if (onboarding != null)
            onboarding.TownRevealed -= Appear;
    }

    // The town is built: set the arcs around the user and bring up the body of the current mode.
    private void Appear()
    {
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;
        frame = Quaternion.LookRotation(forward.normalized, Vector3.up);
        origin = head.position;
        IsReady = true;
        sun.gameObject.SetActive(true);
        moon.gameObject.SetActive(true);
        sunT = moonT = 1f;
        Place(Body.Sun, 1f);
        Place(Body.Moon, 1f);
        StartCoroutine(Animate(IsNight ? Body.Moon : Body.Sun, 1f, 0f, riseTime));
    }

    private void LateUpdate()
    {
        if (!IsReady || dragging.HasValue || animating)
            return;
        // A gentle float so the one in the sky looks alive (and touchable).
        Body up = IsNight ? Body.Moon : Body.Sun;
        Place(up, 0f, Mathf.Sin(Time.time * 1.6f) * idleBob);
    }

    // ---------- Called by the grab transformer on the sun/moon ----------

    public void BeginDrag(Body body)
    {
        if (!IsReady || animating || body != (IsNight ? Body.Moon : Body.Sun))
            return;
        dragging = body;
        Play(grabSound, BodyTransform(body).position);
    }

    public void Drag(Body body, Vector3 handPosition)
    {
        if (dragging != body)
            return;
        float t = ClosestT(body, handPosition);
        SetT(body, t);
        Place(body, t);
        // The town dims as the sun goes down (or brightens as the moon goes down), up to half way.
        float progress = Mathf.Clamp01(t) * 0.5f;
        dayNight?.SetNightImmediate(IsNight ? 1f - progress : progress);
    }

    public void EndDrag(Body body)
    {
        if (dragging != body)
            return;
        dragging = null;
        float t = body == Body.Sun ? sunT : moonT;
        if (t >= switchPoint)
            StartCoroutine(Switch(body, t));
        else
        {
            StartCoroutine(Animate(body, t, 0f, springBackTime));
            dayNight?.SetNight(IsNight ? 1f : 0f, springBackTime);
        }
    }

    // It sets, the other rises on its side, and the mode flips.
    private IEnumerator Switch(Body setting, float fromT)
    {
        animating = true;
        IsNight = setting == Body.Sun;
        dayNight?.SetNight(IsNight ? 1f : 0f, setTime + riseTime * 0.8f);
        yield return Animate(setting, fromT, 1f, setTime * (1f - fromT) + 0.15f);
        Body rising = setting == Body.Sun ? Body.Moon : Body.Sun;
        Play(switchSound, BodyTransform(rising).position);
        yield return Animate(rising, 1f, 0f, riseTime);
        animating = false;
    }

    private IEnumerator Animate(Body body, float from, float to, float seconds)
    {
        bool wasAnimating = animating;
        animating = true;
        for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(seconds, 0.01f))
        {
            float k = t * t * (3f - 2f * t);
            float v = Mathf.Lerp(from, to, k);
            SetT(body, v);
            Place(body, v);
            yield return null;
        }
        SetT(body, to);
        Place(body, to);
        animating = wasAnimating;
    }

    // ---------- Arc ----------

    private void SetT(Body body, float t)
    {
        if (body == Body.Sun) sunT = t; else moonT = t;
    }

    private Transform BodyTransform(Body body) => body == Body.Sun ? sun : moon;

    private Vector3 Point(Body body, float t)
    {
        Vector3 a = sunUp, c = sunBend, d = sunDown;
        if (body == Body.Moon && mirrorForMoon)
        {
            a.x = -a.x; c.x = -c.x; d.x = -d.x;
        }
        // Quadratic Bezier through the bend.
        float u = 1f - t;
        Vector3 local = u * u * a + 2f * u * t * c + t * t * d;
        return origin + frame * local;
    }

    private float ClosestT(Body body, Vector3 p)
    {
        float best = 0f, bestD = float.MaxValue;
        const int steps = 48;
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            float d = (Point(body, t) - p).sqrMagnitude;
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    private void Place(Body body, float t, float bob = 0f)
    {
        var tr = BodyTransform(body);
        if (tr == null)
            return;
        tr.position = Point(body, t) + Vector3.up * bob;
        // Shrinks and fades out near the end of the arc (fully gone at the end).
        float vanish = Mathf.Clamp01((t - vanishFrom) / Mathf.Max(1f - vanishFrom, 0.01f));
        float s = 1f - vanish * vanish;
        tr.localScale = (body == Body.Sun ? sunScale : moonScale) * Mathf.Max(s, 0.0001f);
        bool visible = s > 0.01f;
        if (tr.gameObject.activeSelf != visible && !(dragging == body))
            tr.gameObject.SetActive(visible);
    }

    private void Play(AudioClip clip, Vector3 at)
    {
        if (clip == null)
            return;
        source.transform.position = at;
        source.PlayOneShot(clip, volume);
    }
}
