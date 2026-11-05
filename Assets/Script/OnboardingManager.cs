using System.Collections;
using Oculus.Interaction;
using UnityEngine;

// Test setup for the dome boundary flow:
// places the mini town in front of the user and a glowing ring centered on the user, slightly below the head.
// The town is grabbed and two-hand scaled by its Interaction SDK Grabbable; the ring follows the town's scale.
public class OnboardingManager : MonoBehaviour
{
    [Header("Mini Town")]
    [Tooltip("A town already in the scene (e.g. with Grabbable + transformer). Takes priority over the prefab.")]
    [SerializeField] private Transform existingTown;
    [Tooltip("Spawned if no existing town is set. If both are empty, a placeholder town (disc + tiny houses) is generated.")]
    [SerializeField] private GameObject miniTownPrefab;
    [SerializeField] private bool placeTownInFrontOfUser = true;
    [SerializeField] private float townDistanceFromHead = 0.45f;
    [SerializeField] private float townHeightBelowHead = 0.25f;
    [SerializeField] private float townDiameter = 0.25f;
    [SerializeField] private float townSpinSpeed = 10f;
    [SerializeField] private float townBobAmplitude = 0.01f;

    [Header("Boundary Ring")]
    [Tooltip("Ring radius at the town's starting scale; it grows and shrinks in proportion to the town.")]
    [SerializeField] private float ringRadius = 0.8f;
    [SerializeField] private float ringHeightBelowHead = 0.3f;
    [SerializeField] private float ringWidth = 0.01f;
    [SerializeField] private int ringSegments = 96;
    [SerializeField] private Color ringColor = new Color(0.37f, 0.9f, 1f, 1f);
    [Tooltip("Optional. If empty, Sprites/Default is used.")]
    [SerializeField] private Material ringMaterial;
    [Tooltip("Ring line width multiplier while the town is held, as feedback.")]
    [SerializeField] private float grabbedRingWidthBoost = 2f;

    [Header("Dome")]
    [Tooltip("Dome model, left inactive in the scene; shown when the bell is touched.")]
    [SerializeField] private Transform dome;
    [Tooltip("How far the dome's wall sits outside the ring, in meters.")]
    [SerializeField] private float domePadding = 1f;

    [Header("Passthrough Hole")]
    [Tooltip("Show the real room inside the ring by punching through the virtual floor/sky there.")]
    [SerializeField] private bool passthroughHole = true;
    [Tooltip("Material using ProductivityTown/Passthrough Hole (a referenced material keeps the shader in builds).")]
    [SerializeField] private Material passthroughHoleMaterial;
    [Tooltip("Height above the floor of the punch disc; about desk height keeps the whole real desk visible. " +
             "Virtual things below it, inside the ring, are hidden.")]
    [SerializeField] private float passthroughHoleHeight = 0.7f;
    [Tooltip("Width of the soft edge between the real room and the virtual floor, in meters.")]
    [SerializeField] private float passthroughHoleFeather = 0.3f;

    /// <summary>Current boundary radius in meters (the dome is built from this).</summary>
    public float RingRadius { get; private set; }

    public bool IsTownGrabbed => wasGrabbed;
    public bool IsDomeActive => dome != null && dome.gameObject.activeSelf;

    private static readonly int PassthroughHoleCenterId = Shader.PropertyToID("_PassthroughHoleCenter");
    private static readonly int PassthroughHoleRadiusId = Shader.PropertyToID("_PassthroughHoleRadius");
    private static readonly int HoleFeatherId = Shader.PropertyToID("_HoleFeather");

    private Transform head;
    private OVRCameraRig rig;
    // Dome model measurements per unit of localScale, taken when it's first shown.
    private float domeRadiusPerScale;
    private Vector3 domeCenterOffsetPerScale;
    private float domeBottomOffsetPerScale;
    private GameObject setupRoot;
    private Transform town;
    private Grabbable townGrabbable;
    private Vector3 townBasePosition;
    private float townSpawnScale;
    private LineRenderer ringLine;
    private Transform holeDisc;
    private bool wasGrabbed;

    private IEnumerator Start()
    {
        rig = FindFirstObjectByType<OVRCameraRig>();
        head =rig != null ? rig.centerEyeAnchor : Camera.main.transform;

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
        if (town == null)
            return;

        // SelectingPoints is created in the Grabbable's Awake, so it's null if that hasn't run (e.g. inactive or failed).
        bool grabbed = townGrabbable != null && townGrabbable.isActiveAndEnabled
                       && townGrabbable.SelectingPoints != null && townGrabbable.SelectingPoints.Count > 0;

        // While held, the Grabbable's transformer owns the town's transform; idle motion resumes from where it was let go.
        if (!grabbed)
        {
            if (wasGrabbed)
                townBasePosition = town.position;
            town.Rotate(0f, townSpinSpeed * Time.deltaTime, 0f, Space.World);
            town.position = townBasePosition + Vector3.up * Mathf.Sin(Time.time * 1.5f) * townBobAmplitude;
        }

        if (grabbed != wasGrabbed && ringLine != null)
            ringLine.widthMultiplier = ringWidth * (grabbed ? grabbedRingWidthBoost : 1f);
        wasGrabbed = grabbed;
    }

    // After the transformer has updated the town this frame.
    private void LateUpdate()
    {
        if (town == null || ringLine == null || townSpawnScale <= 0f)
            return;

        float radius = ringRadius * town.localScale.x / townSpawnScale;
        if (!Mathf.Approximately(radius, RingRadius))
            SetRingRadius(radius);

        if (holeDisc != null)
            PlaceHoleDisc();

        if (dome != null && dome.gameObject.activeSelf && domeRadiusPerScale > 0f)
            PlaceDome();
    }

    /// <summary>Shows the dome on the floor around the user, sized from the ring. Called by the bell.</summary>
    public void ActivateDome()
    {
        if (dome == null || ringLine == null || dome.gameObject.activeSelf)
            return;

        dome.gameObject.SetActive(true);

        // Renderer bounds are only valid once active. Measure the model so any pivot/size works.
        var renderers = dome.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return;
        Bounds b = renderers[0].bounds;
        foreach (var r in renderers)
            b.Encapsulate(r.bounds);

        float scale = dome.localScale.x;
        domeRadiusPerScale = Mathf.Max(b.extents.x, b.extents.z) / scale;
        domeCenterOffsetPerScale = (b.center - dome.position) / scale;
        domeBottomOffsetPerScale = (dome.position.y - b.min.y) / scale;

        PlaceDome();
    }

    private void PlaceDome()
    {
        float scale = (RingRadius + domePadding) / domeRadiusPerScale;
        dome.localScale = Vector3.one * scale;

        // Floor-level tracking origin: the tracking space's y is the real floor.
        float floorY = rig != null ? rig.trackingSpace.position.y : 0f;
        Vector3 center = ringLine.transform.position;
        dome.position = new Vector3(
            center.x - domeCenterOffsetPerScale.x * scale,
            floorY + domeBottomOffsetPerScale * scale,
            center.z - domeCenterOffsetPerScale.z * scale);
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
        SpawnHoleDisc();
        SpawnTown(forward);
    }

    // Flat quad on the floor around the user with the "Passthrough Hole" shader; the shader cuts the soft circle.
    private void SpawnHoleDisc()
    {
        if (!passthroughHole)
            return;
        if (passthroughHoleMaterial == null)
        {
            Debug.LogWarning("[Onboarding] Passthrough Hole Material is not assigned.");
            return;
        }

        var disc = GameObject.CreatePrimitive(PrimitiveType.Quad);
        disc.name = "PassthroughHole";
        Destroy(disc.GetComponent<Collider>());
        disc.transform.SetParent(setupRoot.transform, false);
        disc.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        var renderer = disc.GetComponent<MeshRenderer>();
        renderer.material = passthroughHoleMaterial;
        renderer.material.SetFloat(HoleFeatherId, passthroughHoleFeather);
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        holeDisc = disc.transform;
        PlaceHoleDisc();
    }

    private void PlaceHoleDisc()
    {
        Vector3 center = ringLine.transform.position;
        // Floor-level tracking origin: the tracking space's y is the real floor.
        float floorY = rig != null ? rig.trackingSpace.position.y : 0f;
        holeDisc.position = new Vector3(center.x, floorY + passthroughHoleHeight, center.z);
        holeDisc.localScale = Vector3.one * 2f * (RingRadius + passthroughHoleFeather + 0.05f);

        Shader.SetGlobalVector(PassthroughHoleCenterId, center);
        Shader.SetGlobalFloat(PassthroughHoleRadiusId, RingRadius);
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
        // Sprites/Default is unlit and uses the line's vertex color. URP materials ignore vertex color,
        // so the assigned material is also tinted (instanced via .material) to ringColor.
        ringLine.material = ringMaterial != null ? ringMaterial : new Material(Shader.Find("Sprites/Default"));
        ringLine.material.color = ringColor;
        // Both ring and hologram are transparent without depth writes, so Unity orders them by object center,
        // and the ring's center (the user's head) always wins. The town sits inside the ring, so draw the ring first.
        ringLine.material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 10;
        ringLine.startColor = ringLine.endColor = ringColor;
        ringLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ringLine.receiveShadows = false;

        SetRingRadius(ringRadius);
    }

    private void SetRingRadius(float radius)
    {
        RingRadius = radius;
        for (int i = 0; i < ringSegments; i++)
        {
            float a = i * Mathf.PI * 2f / ringSegments;
            ringLine.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
        }
    }

    private void SpawnTown(Vector3 forward)
    {
        if (existingTown != null)
        {
            // Reuse the scene town (keeps its Grabbable setup); don't parent it, so respawning never destroys it.
            // It may be left inactive in the scene so it only appears now; activating it also runs the Grabbable's Awake.
            town = existingTown;
            town.gameObject.SetActive(true);
        }
        else
        {
            if (town != null && town.IsChildOf(setupRoot.transform))
                Destroy(town.gameObject);
            town = miniTownPrefab != null
                ? Instantiate(miniTownPrefab).transform
                : BuildPlaceholderTown();
            town.name = "MiniTown";
            town.SetParent(setupRoot.transform, true);
        }

        if (placeTownInFrontOfUser)
        {
            townBasePosition = head.position + forward * townDistanceFromHead + Vector3.down * townHeightBelowHead;
            town.SetPositionAndRotation(townBasePosition, Quaternion.LookRotation(-forward, Vector3.up));
        }
        else
        {
            townBasePosition = town.position;
        }

        townGrabbable = town.GetComponentInChildren<Grabbable>();
        // The ring's starting radius corresponds to the town's scale right now.
        townSpawnScale = town.localScale.x;
        wasGrabbed = false;
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
