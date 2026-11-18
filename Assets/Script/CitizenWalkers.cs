using System.Collections.Generic;
using UnityEngine;

// Makes the town's citizens stroll around the plaza promenade (by default its inner lane, clear of the benches and
// lamps; anything solid on the ring is avoided): each one walks a stretch, pauses, picks a new spot, and so on, with a little
// hop and waddle as it goes. They freeze where they are while the town is in focus mode (focus timer running), and
// carry on during breaks and outside a session. Everything is computed in the citizens' parent's space, so it works
// at any town scale and rotation. The models face +Z.
public class CitizenWalkers : MonoBehaviour
{
    [Tooltip("Parent of the citizens (each direct child is one citizen), e.g. Citizens_Lineup_Unity.")]
    [SerializeField] private Transform citizensRoot;
    [Tooltip("The plaza's outer curb; the citizens walk in a ring just outside it.")]
    [SerializeField] private Renderer plazaOuterCurb;
    [SerializeField] private OnboardingManager onboarding;
    [SerializeField] private TownTaskTimer timer;

    [Header("Where")]
    [Tooltip("Walking ring, as fractions of the plaza's outer curb radius (inner, outer). 0.83 is the inner curb.")]
    [SerializeField] private Vector2 ringBand = new Vector2(0.845f, 0.87f);
    [Tooltip("How far along the ring a citizen walks before pausing (degrees, min/max).")]
    [SerializeField] private Vector2 walkArc = new Vector2(12f, 40f);
    [Tooltip("Parent of the town's objects, used to keep citizens out of solid things on the ring " +
             "(e.g. 'Town'). Empty: the citizens' parent's parent.")]
    [SerializeField] private Transform obstaclesRoot;
    [Tooltip("Objects whose names start with these are walked over/through (small or flat things).")]
    [SerializeField] private string[] ignoreObstacles = { "Flower", "Path", "Plaza", "Grass", "Base", "Rail", "Sleeper", "Track", "Field", "River", "Train", "PT_", "TownCanvas" };
    [Tooltip("Extra clearance around obstacles, in citizen heights.")]
    [SerializeField] private float clearance = 0.3f;

    [Header("How")]
    [Tooltip("Walking speed in citizen heights per second.")]
    [SerializeField] private Vector2 speed = new Vector2(0.8f, 1.3f);
    [Tooltip("Pause between walks, in seconds (min/max).")]
    [SerializeField] private Vector2 idleTime = new Vector2(0.8f, 3f);
    [SerializeField] private float stepsPerSecond = 3.2f;
    [Tooltip("Hop height per step, in citizen heights.")]
    [SerializeField] private float hopHeight = 0.08f;
    [SerializeField] private float waddleDegrees = 7f;
    [SerializeField] private float turnSpeed = 360f;

    private class Citizen
    {
        public Transform t;
        public Quaternion restRotation; // local, facing +Z
        public float groundY;           // local
        public float height;            // local units
        public float angle, radius;     // current polar position (degrees, local units)
        public float fromAngle, toAngle, fromRadius, toRadius;
        public float walkTime, walkLength, idleUntil, speed, stepPhase, yaw;
        public bool walking;
        public float segStart, segEnd; // free stretch of the ring this citizen strolls in (degrees, end > start)
    }

    private readonly List<Vector2> freeSegments = new List<Vector2>(); // (start, end) degrees, end > start

    private readonly List<Citizen> citizens = new List<Citizen>();
    private Vector3 center;   // local
    private float plazaRadius; // local
    private bool active;

    public bool IsFrozen => timer != null && timer.IsRunning && !timer.IsPaused;

    private void Awake()
    {
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();
        if (timer == null)
            timer = FindFirstObjectByType<TownTaskTimer>();
    }

    private void OnEnable()
    {
        if (onboarding != null)
            onboarding.TownRevealed += Begin;
    }

    private void OnDisable()
    {
        if (onboarding != null)
            onboarding.TownRevealed -= Begin;
    }

    private void Start() => Setup();

    // Measures the ring and spreads the citizens around it (so they appear spread out when the town is built).
    private void Setup()
    {
        citizens.Clear();
        if (citizensRoot == null || plazaOuterCurb == null)
            return;

        // Measured from the meshes in the citizens' parent space (renderer bounds aren't valid while the town is
        // still hidden).
        if (!LocalBounds(plazaOuterCurb.transform, out Bounds b))
            return;
        center = new Vector3(b.center.x, 0f, b.center.z);
        plazaRadius = Mathf.Max(b.extents.x, b.extents.z);

        int count = citizensRoot.childCount;
        float typicalHeight = 0.05f;
        if (count > 0 && LocalBounds(citizensRoot.GetChild(0), out Bounds first))
            typicalHeight = first.size.y;
        FindFreeSegments(typicalHeight);

        float offset = Random.Range(0f, 360f);
        for (int i = 0; i < count; i++)
        {
            Transform t = citizensRoot.GetChild(i);
            float height = LocalBounds(t, out Bounds rb) ? rb.size.y : 0.05f;
            float wanted = offset + 360f * i / count + Random.Range(-8f, 8f);
            Vector2 seg = SegmentNear(wanted);
            float pad = Mathf.Min(PadDegrees(height), (seg.y - seg.x) * 0.4f);

            var c = new Citizen
            {
                t = t,
                restRotation = t.localRotation,
                groundY = t.localPosition.y,
                height = height,
                angle = Random.Range(seg.x + pad, seg.y - pad),
                radius = plazaRadius * Random.Range(ringBand.x, ringBand.y),
                segStart = seg.x + pad,
                segEnd = seg.y - pad,
            };
            c.yaw = c.angle + 90f * (Random.value < 0.5f ? 1f : -1f); // facing along the ring
            c.idleUntil = Random.Range(0f, 1.5f);
            Place(c, 0f, 0f);
            citizens.Add(c);
        }
    }

    private void Begin() => active = true;

    // The ring minus the angles blocked by solid things overlapping it.
    private void FindFreeSegments(float citizenHeight)
    {
        freeSegments.Clear();
        Transform root = obstaclesRoot != null ? obstaclesRoot : (citizensRoot.parent != null ? citizensRoot.parent : citizensRoot);
        float inner = plazaRadius * ringBand.x, outer = plazaRadius * ringBand.y;
        float pad = citizenHeight * clearance;
        var blocked = new List<Vector2>();
        foreach (Transform child in root)
        {
            if (child == citizensRoot || IsIgnored(child.name) || !LocalBounds(child, out Bounds ob))
                continue;
            float footprint = Mathf.Max(ob.size.x, ob.size.z);
            if (ob.size.y < footprint * 0.2f)
                continue; // flat
            Vector3 offset = new Vector3(ob.center.x, 0f, ob.center.z) - center;
            float r = Mathf.Min(ob.extents.x, ob.extents.z) * 0.9f + pad; // a bit generous on round objects
            float dist = offset.magnitude;
            if (dist + r < inner || dist - r > outer || dist < 1e-4f)
                continue;
            float a = Mathf.Atan2(offset.z, offset.x) * Mathf.Rad2Deg;
            float half = Mathf.Asin(Mathf.Clamp01(r / dist)) * Mathf.Rad2Deg;
            blocked.Add(new Vector2(a - half, a + half));
        }

        if (blocked.Count == 0)
        {
            freeSegments.Add(new Vector2(0f, 360f));
            return;
        }

        // Normalize to [0, 360), sort, merge; the gaps are free (the last gap wraps around).
        for (int i = 0; i < blocked.Count; i++)
        {
            float s = Mathf.Repeat(blocked[i].x, 360f);
            blocked[i] = new Vector2(s, s + (blocked[i].y - blocked[i].x));
        }
        blocked.Sort((x, y) => x.x.CompareTo(y.x));
        var merged = new List<Vector2> { blocked[0] };
        foreach (var bl in blocked)
        {
            var last = merged[merged.Count - 1];
            if (bl.x <= last.y)
                merged[merged.Count - 1] = new Vector2(last.x, Mathf.Max(last.y, bl.y));
            else
                merged.Add(bl);
        }
        for (int i = 0; i < merged.Count; i++)
        {
            float start = merged[i].y;
            float end = i + 1 < merged.Count ? merged[i + 1].x : merged[0].x + 360f;
            if (end - start > 4f)
                freeSegments.Add(new Vector2(start, end));
        }
        if (freeSegments.Count == 0)
            freeSegments.Add(new Vector2(0f, 360f));
    }

    private bool IsIgnored(string objectName)
    {
        foreach (var prefix in ignoreObstacles)
            if (!string.IsNullOrEmpty(prefix) && objectName.StartsWith(prefix))
                return true;
        return false;
    }

    // The free stretch containing (or nearest to) an angle, with the wanted angle mapped into its range.
    private Vector2 SegmentNear(float angle)
    {
        Vector2 best = freeSegments[0];
        float bestScore = float.MaxValue;
        foreach (var seg in freeSegments)
        {
            float a = Mathf.Repeat(angle - seg.x, 360f) + seg.x; // angle expressed from the segment's start
            float score = a <= seg.y ? -(seg.y - seg.x) : Mathf.Min(a - seg.y, seg.x + 360f - a);
            if (score < bestScore)
            {
                bestScore = score;
                best = seg;
            }
        }
        return best;
    }

    private float PadDegrees(float citizenHeight)
    {
        float r = plazaRadius * (ringBand.x + ringBand.y) * 0.5f;
        return Mathf.Asin(Mathf.Clamp01(citizenHeight * 0.5f / Mathf.Max(r, 1e-4f))) * Mathf.Rad2Deg;
    }

    // Bounds of everything under `obj`, from its meshes, in the citizens' parent space.
    private bool LocalBounds(Transform obj, out Bounds bounds)
    {
        bounds = default;
        bool any = false;
        foreach (var mf in obj.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null)
                continue;
            Bounds m = mf.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? m.min.x : m.max.x, (i & 2) == 0 ? m.min.y : m.max.y, (i & 4) == 0 ? m.min.z : m.max.z);
                Vector3 p = citizensRoot.InverseTransformPoint(mf.transform.TransformPoint(corner));
                if (!any)
                {
                    bounds = new Bounds(p, Vector3.zero);
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(p);
                }
            }
        }
        return any;
    }

    private void Update()
    {
        if (!active || citizens.Count == 0)
            return;

        bool frozen = IsFrozen;
        float now = Time.time;
        foreach (var c in citizens)
        {
            if (c.t == null)
                continue;
            if (frozen)
            {
                // Stand still (settle any hop/waddle).
                c.stepPhase = 0f;
                Place(c, 0f, 0f);
                continue;
            }

            if (!c.walking)
            {
                if (now < c.idleUntil)
                {
                    Place(c, 0f, 0f);
                    continue;
                }
                StartWalk(c);
            }

            // Walk along the ring (polar interpolation keeps them on the belt).
            c.walkTime += Time.deltaTime;
            float k = Mathf.Clamp01(c.walkTime / c.walkLength);
            float eased = k * k * (3f - 2f * k);
            float prevAngle = c.angle, prevRadius = c.radius;
            c.angle = Mathf.Lerp(c.fromAngle, c.toAngle, eased);
            c.radius = Mathf.Lerp(c.fromRadius, c.toRadius, eased);

            Vector3 move = PolarToLocal(c.angle, c.radius) - PolarToLocal(prevAngle, prevRadius);
            if (move.sqrMagnitude > 1e-10f)
            {
                float targetYaw = Mathf.Atan2(move.x, move.z) * Mathf.Rad2Deg;
                c.yaw = Mathf.MoveTowardsAngle(c.yaw, targetYaw, turnSpeed * Time.deltaTime);
            }

            c.stepPhase += Time.deltaTime * stepsPerSecond;
            float hop = Mathf.Abs(Mathf.Sin(c.stepPhase * Mathf.PI)) * hopHeight * c.height;
            float waddle = Mathf.Sin(c.stepPhase * Mathf.PI) * waddleDegrees;
            Place(c, hop, waddle);

            if (k >= 1f)
            {
                c.walking = false;
                c.stepPhase = 0f;
                c.idleUntil = now + Random.Range(idleTime.x, idleTime.y);
            }
        }
    }

    private void StartWalk(Citizen c)
    {
        c.walking = true;
        c.walkTime = 0f;
        c.fromAngle = c.angle;
        c.fromRadius = c.radius;
        // Stay within its free stretch: turn around rather than walk into something.
        float arc = Random.Range(walkArc.x, walkArc.y);
        float dir = Random.value < 0.5f ? -1f : 1f;
        if (c.angle + arc * dir > c.segEnd || c.angle + arc * dir < c.segStart)
            dir = -dir;
        c.toAngle = Mathf.Clamp(c.angle + arc * dir, c.segStart, c.segEnd);
        c.toRadius = plazaRadius * Random.Range(ringBand.x, ringBand.y);
        c.speed = Random.Range(speed.x, speed.y) * c.height;
        float distance = Mathf.Deg2Rad * Mathf.Abs(c.toAngle - c.fromAngle) * (c.fromRadius + c.toRadius) * 0.5f
                         + Mathf.Abs(c.toRadius - c.fromRadius);
        c.walkLength = Mathf.Max(0.3f, distance / Mathf.Max(c.speed, 1e-4f));
    }

    private Vector3 PolarToLocal(float angle, float radius)
    {
        float a = angle * Mathf.Deg2Rad;
        return center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
    }

    private void Place(Citizen c, float hop, float waddle)
    {
        Vector3 p = PolarToLocal(c.angle, c.radius);
        p.y = c.groundY + hop;
        c.t.localPosition = p;
        // Face the walking direction (models face +Z), with a side-to-side waddle.
        c.t.localRotation = Quaternion.Euler(0f, c.yaw, 0f) * Quaternion.Euler(0f, 0f, waddle) * c.restRotation;
    }
}
