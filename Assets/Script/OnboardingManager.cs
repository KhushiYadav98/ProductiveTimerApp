using System.Collections;
using Oculus.Interaction.Input;
using UnityEngine;

// Test setup for the dome boundary flow:
// spawns a mini town model in front of the user and a glowing ring centered on the user, slightly below the head.
// Pinch the town with both hands and pull apart / push together to scale it; the ring scales with it.
public class OnboardingManager : MonoBehaviour
{
    [Header("Mini Town")]
    [Tooltip("Optional. If empty, a placeholder town (disc + tiny houses) is generated.")]
    [SerializeField] private GameObject miniTownPrefab;
    [SerializeField] private float townDistanceFromHead = 0.45f;
    [SerializeField] private float townHeightBelowHead = 0.25f;
    [SerializeField] private float townDiameter = 0.25f;
    [SerializeField] private float townSpinSpeed = 10f;
    [SerializeField] private float townBobAmplitude = 0.01f;

    [Header("Boundary Ring")]
    [SerializeField] private float ringRadius = 0.8f;
    [SerializeField] private float ringHeightBelowHead = 0.3f;
    [SerializeField] private float ringWidth = 0.01f;
    [SerializeField] private int ringSegments = 96;
    [SerializeField] private Color ringColor = new Color(0.37f, 0.9f, 1f, 1f);
    [Tooltip("Optional. If empty, Sprites/Default is used.")]
    [SerializeField] private Material ringMaterial;

    [Header("Two-Hand Stretch")]
    [Tooltip("How far outside the town's edge a pinch still counts as grabbing it (meters).")]
    [SerializeField] private float grabMargin = 0.08f;
    [SerializeField] private float minRingRadius = 0.5f;
    [SerializeField] private float maxRingRadius = 2.5f;
    [Tooltip("Ring line width multiplier while stretching, as feedback.")]
    [SerializeField] private float stretchRingWidthBoost = 2f;

    /// <summary>Current boundary radius in meters (the dome is built from this).</summary>
    public float RingRadius { get; private set; }

    private Transform head;
    private GameObject setupRoot;
    private Transform town;
    private Vector3 townBasePosition;
    private Vector3 townSpawnScale;
    private float townSpawnRadius;
    private LineRenderer ringLine;

    private IHand leftHand;
    private IHand rightHand;
    private float nextHandSearchTime;
    private bool stretching;
    private float stretchStartDistance;
    private float stretchStartRingRadius;

    private IEnumerator Start()
    {
        var rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;

        // Wait for a valid head pose (it reads as zero for the first frames on device).
        float timeout = 3f;
        while (head.localPosition == Vector3.zero && timeout > 0f)
        {
            timeout -= Time.deltaTime;
            yield return null;
        }

        Spawn();

        // Hand-tracking friendly respawn: the system recenter gesture (palm up, pinch-hold the Meta icon).
        if (OVRManager.display != null)
            OVRManager.display.RecenteredPose += Spawn;
    }

    private void OnDestroy()
    {
        if (OVRManager.display != null)
            OVRManager.display.RecenteredPose -= Spawn;
    }

    private void Update()
    {
        UpdateStretch();

        if (town != null)
        {
            town.Rotate(0f, townSpinSpeed * Time.deltaTime, 0f, Space.World);
            town.position = townBasePosition + Vector3.up * Mathf.Sin(Time.time * 1.5f) * townBobAmplitude;
        }
    }

    [ContextMenu("Respawn")]
    public void Spawn()
    {
        if (setupRoot != null)
            Destroy(setupRoot);

        setupRoot = new GameObject("DomeSetupRoot");

        // Yaw-only forward so the town doesn't end up in the floor/ceiling if the user is looking up or down.
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
        forward.Normalize();

        SpawnRing();
        SpawnTown(forward);
    }

    private void SpawnRing()
    {
        var ring = new GameObject("BoundaryRing");
        ring.transform.SetParent(setupRoot.transform, false);
        ring.transform.position = new Vector3(head.position.x, head.position.y - ringHeightBelowHead, head.position.z);

        ringLine = ring.AddComponent<LineRenderer>();
        ringLine.useWorldSpace = false;
        ringLine.loop = true;
        ringLine.positionCount = ringSegments;
        ringLine.widthMultiplier = ringWidth;
        ringLine.material = ringMaterial != null ? ringMaterial : new Material(Shader.Find("Sprites/Default"));
        ringLine.startColor = ringLine.endColor = ringColor;
        ringLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ringLine.receiveShadows = false;

        SetRingRadius(ringRadius);
    }

    // Resizes the ring and scales the town by the same factor relative to their spawn size.
    private void SetRingRadius(float radius)
    {
        RingRadius = radius;
        for (int i = 0; i < ringSegments; i++)
        {
            float a = i * Mathf.PI * 2f / ringSegments;
            ringLine.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
        }

        if (town != null)
            town.localScale = townSpawnScale * (radius / ringRadius);
    }

    // ---------- Two-hand stretch ----------

    private void UpdateStretch()
    {
        if (town == null || ringLine == null)
            return;

        if ((leftHand == null || rightHand == null) && Time.time >= nextHandSearchTime)
        {
            FindHands();
            nextHandSearchTime = Time.time + 1f;
        }

        bool leftPinch = TryGetPinchPoint(leftHand, out var leftPoint);
        bool rightPinch = TryGetPinchPoint(rightHand, out var rightPoint);

        if (!stretching)
        {
            if (leftPinch && rightPinch && IsNearTown(leftPoint) && IsNearTown(rightPoint))
            {
                stretching = true;
                stretchStartDistance = Mathf.Max(Vector3.Distance(leftPoint, rightPoint), 0.01f);
                stretchStartRingRadius = RingRadius;
                ringLine.widthMultiplier = ringWidth * stretchRingWidthBoost;
            }
            return;
        }

        if (!leftPinch || !rightPinch)
        {
            stretching = false;
            ringLine.widthMultiplier = ringWidth;
            return;
        }

        float factor = Vector3.Distance(leftPoint, rightPoint) / stretchStartDistance;
        SetRingRadius(Mathf.Clamp(stretchStartRingRadius * factor, minRingRadius, maxRingRadius));
    }

    // Picks the raw tracked hands from the interaction rig ("OVRHands"), avoiding the SyntheticHandData
    // copies, whose fingers can be modified by other interactions. Only connected hands are taken.
    private void FindHands()
    {
        foreach (var hand in FindObjectsByType<Hand>(FindObjectsSortMode.None))
        {
            if (!hand.IsConnected || hand.name.Contains("Synthetic"))
                continue;
            bool preferred = hand.name.StartsWith("OVRHands");
            string side = hand.Handedness.ToString();
            if (side == "Left" && (leftHand == null || preferred))
                leftHand = hand;
            else if (side == "Right" && (rightHand == null || preferred))
                rightHand = hand;
        }
    }

    // Midpoint between thumb and index tips while the index finger is pinching.
    private static bool TryGetPinchPoint(IHand hand, out Vector3 point)
    {
        point = default;
        if (hand == null || !hand.IsTrackedDataValid || !hand.GetIndexFingerIsPinching())
            return false;
        if (!hand.GetJointPose(HandJointId.HandIndexTip, out var index) ||
            !hand.GetJointPose(HandJointId.HandThumbTip, out var thumb))
            return false;
        point = (index.position + thumb.position) * 0.5f;
        return true;
    }

    private bool IsNearTown(Vector3 point)
    {
        float townRadius = townSpawnRadius * (RingRadius / ringRadius);
        return Vector3.Distance(point, town.position) <= townRadius + grabMargin;
    }

    private void SpawnTown(Vector3 forward)
    {
        townBasePosition = head.position + forward * townDistanceFromHead + Vector3.down * townHeightBelowHead;

        town = miniTownPrefab != null
            ? Instantiate(miniTownPrefab).transform
            : BuildPlaceholderTown();

        town.name = "MiniTown";
        town.SetParent(setupRoot.transform, true);
        town.SetPositionAndRotation(townBasePosition, Quaternion.LookRotation(-forward, Vector3.up));
        townSpawnScale = town.localScale;
        townSpawnRadius = CalculateHorizontalRadius(town);
        stretching = false;
    }

    // Approximate radius of the town footprint around its pivot.
    private static float CalculateHorizontalRadius(Transform root)
    {
        var center = new Vector2(root.position.x, root.position.z);
        float radius = 0f;
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            var b = r.bounds;
            radius = Mathf.Max(radius,
                Vector2.Distance(new Vector2(b.min.x, b.min.z), center),
                Vector2.Distance(new Vector2(b.max.x, b.max.z), center));
        }
        // Bounds corners overestimate a round town.
        return radius > 0f ? radius * 0.75f : 0.125f;
    }

    // Disc base with a ring of tiny houses and trees, sized to townDiameter.
    private Transform BuildPlaceholderTown()
    {
        var root = new GameObject("PlaceholderTown").transform;
        float r = townDiameter * 0.5f;

        var disc = CreatePart(PrimitiveType.Cylinder, root, new Color(0.2f, 0.5f, 0.6f));
        disc.localScale = new Vector3(townDiameter, 0.004f, townDiameter);

        Color[] houseColors =
        {
            new Color(0.96f, 0.84f, 0.55f), new Color(0.62f, 0.85f, 0.8f),
            new Color(0.95f, 0.64f, 0.55f), new Color(0.9f, 0.78f, 1f)
        };

        const int count = 10;
        for (int i = 0; i < count; i++)
        {
            float a = i * Mathf.PI * 2f / count;
            var pos = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r * 0.75f;

            if (i % 3 == 2)
            {
                var tree = CreatePart(PrimitiveType.Sphere, root, new Color(0.3f, 0.65f, 0.35f));
                tree.localScale = Vector3.one * townDiameter * 0.08f;
                tree.localPosition = pos + Vector3.up * townDiameter * 0.06f;
            }
            else
            {
                float h = townDiameter * Random.Range(0.07f, 0.14f);
                var house = CreatePart(PrimitiveType.Cube, root, houseColors[i % houseColors.Length]);
                house.localScale = new Vector3(townDiameter * 0.08f, h, townDiameter * 0.08f);
                house.localPosition = pos + Vector3.up * h * 0.5f;
                house.localRotation = Quaternion.Euler(0f, -a * Mathf.Rad2Deg, 0f);
            }
        }

        return root;
    }

    private static Transform CreatePart(PrimitiveType type, Transform parent, Color color)
    {
        var go = GameObject.CreatePrimitive(type);
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.GetComponent<Renderer>().material.color = color;
        return go.transform;
    }
}
