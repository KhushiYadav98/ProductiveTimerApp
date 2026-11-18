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
    [SerializeField] private float ringWidth = 0.01f;
    [SerializeField] private int ringSegments = 96;
    [SerializeField] private Color ringColor = new Color(0.37f, 0.9f, 1f, 1f);
    [Tooltip("Optional. If empty, Sprites/Default is used.")]
    [SerializeField] private Material ringMaterial;
    [Tooltip("Ring line width multiplier while the town is held, as feedback.")]
    [SerializeField] private float grabbedRingWidthBoost = 2f;

    [Header("Productivity Town (life-size)")]
    [Tooltip("Life-size town (e.g. ProductivityTown_Unity), shown around the user when the bell is touched. " +
             "Its pivot should be at the town's center, on its ground.")]
    [SerializeField] private Transform bigTown;
    [Tooltip("Part of the big town whose edge is matched to the ring (e.g. Plaza_OuterCurb, so the user stands in the plaza). " +
             "If empty, the whole town's edge is matched to the ring.")]
    [SerializeField] private Transform bigTownFitTo;
    [Tooltip("Optional part of the big town (e.g. ClockTower) turned to a fixed direction from the user when it's shown.")]
    [SerializeField] private Transform bigTownFaceTarget;
    [Tooltip("Where the face target ends up, in degrees from the user's forward (positive = to the right).")]
    [SerializeField] private float bigTownFaceTargetYaw = 45f;
    [Tooltip("Makes the big town bigger (>1) or smaller (<1) than the ring. 1 = the fit target's edge sits on the ring. " +
             "Larger values push buildings out past the passthrough hole, so less of the town is cut away.")]
    [Min(0.1f)]
    [SerializeField] private float bigTownSizeMultiplier = 1f;
    [Tooltip("Hide the mini town and the ring once the big town is shown.")]
    [SerializeField] private bool hideMiniTownOnConfirm = true;
    [Tooltip("Sky sphere around the town (e.g. Env_Sphere). Drawn double-sided so it's visible from inside.")]
    [SerializeField] private Renderer skySphere;

    [Header("Dome")]
    [Tooltip("Optional dome model, left inactive in the scene; shown when the bell is touched.")]
    [SerializeField] private Transform dome;
    [Tooltip("How far the dome's wall sits outside the ring, in meters.")]
    [SerializeField] private float domePadding = 1f;

    [Header("Passthrough Hole")]
    [Tooltip("After the bell: show the real room inside the ring (floor to ceiling) by punching through the town/sky there.")]
    [SerializeField] private bool passthroughHole = true;
    [Tooltip("Material using ProductivityTown/Passthrough Hole (a referenced material keeps the shader in builds).")]
    [SerializeField] private Material passthroughHoleMaterial;
    [Tooltip("Height above the ring (where the town's ground is placed) of the floor punch disc. " +
             "Keep it just above the town's ground so the cut ends at the ring.")]
    [SerializeField] private float passthroughHoleHeight = 0.05f;
    [Tooltip("Also punch through the sky above the ring, showing the real ceiling. Off: only the floor shows the real " +
             "room and the virtual sky stays overhead.")]
    [SerializeField] private bool passthroughCeiling = false;
    [Tooltip("Height above the floor of the ceiling punch disc; the sky above it, inside the ring, shows the real room.")]
    [SerializeField] private float passthroughCeilingHeight = 2.5f;
    [Tooltip("Width of the soft edge between the real room and the town, in meters (centered on the ring).")]
    [SerializeField] private float passthroughHoleFeather = 0.3f;

    /// <summary>Current boundary radius in meters (the dome is built from this).</summary>
    public float RingRadius { get; private set; }

    public bool IsTownGrabbed => wasGrabbed;
    public bool IsDomeActive => dome != null && dome.gameObject.activeSelf;
    /// <summary>True once the bell has confirmed the size (the big town and/or dome is shown).</summary>
    public bool IsConfirmed { get; private set; }

    /// <summary>Raised once the bell has confirmed the size and the big town is shown.</summary>
    public event System.Action Confirmed;

    /// <summary>Raised after the mini town (and ring) is placed in front of the user, including on respawn.</summary>
    public event System.Action Spawned;

    /// <summary>The mini town being sized (null until spawned).</summary>
    public Transform MiniTown => town;

    private static readonly int PassthroughHoleCenterId = Shader.PropertyToID("_PassthroughHoleCenter");
    private static readonly int PassthroughHoleRadiusId = Shader.PropertyToID("_PassthroughHoleRadius");
    private static readonly int HoleFeatherId = Shader.PropertyToID("_HoleFeather");
    private static readonly int HoleExtraId = Shader.PropertyToID("_HoleExtra");
    private static readonly int CullId = Shader.PropertyToID("_Cull");

    private Transform head;
    private OVRCameraRig rig;
    private Vector3 bigTownStartScale;
    private Quaternion bigTownStartRotation;
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
    private Transform holeCeiling;
    private bool wasGrabbed;

    private IEnumerator Start()
    {
        rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;

        // The life-size town stays hidden until the bell confirms its size.
        if (bigTown != null)
        {
            bigTownStartScale = bigTown.localScale;
            bigTownStartRotation = bigTown.rotation;
            bigTown.gameObject.SetActive(false);
        }

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

        // Until the bell, the ring stays centered on the user but follows the mini town's height (its base),
        // so the big town is later placed at the height the user chose. The idle bob is ignored.
        if (!IsConfirmed)
        {
            float townY = wasGrabbed ? town.position.y : townBasePosition.y;
            Vector3 p = ringLine.transform.position;
            if (!Mathf.Approximately(p.y, townY))
                ringLine.transform.position = new Vector3(p.x, townY, p.z);
        }

        if (holeDisc != null)
            PlaceHoleDisc();

        if (dome != null && dome.gameObject.activeSelf && domeRadiusPerScale > 0f)
            PlaceDome();
    }

    /// <summary>
    /// Called by the bell: locks in the ring size, then shows the life-size town (and the dome, if set)
    /// on the floor, centered on the ring (where the user stands).
    /// </summary>
    public void ConfirmBoundary()
    {
        if (IsConfirmed || ringLine == null)
            return;
        IsConfirmed = true;

        ShowBigTown();
        ActivateDome();
        // Before the bell everything is passthrough anyway; now cut the real room back in, inside the ring.
        SpawnHoleDiscs();

        if (hideMiniTownOnConfirm)
        {
            if (town != null)
                town.gameObject.SetActive(false);
            ringLine.enabled = false;
        }

        Confirmed?.Invoke();
    }

    private void ShowBigTown()
    {
        if (bigTown == null)
            return;

        bigTown.localScale = bigTownStartScale;
        bigTown.rotation = bigTownStartRotation;
        bigTown.gameObject.SetActive(true);

        // The town's own preview camera would render over the headset view.
        foreach (var cam in bigTown.GetComponentsInChildren<Camera>(true))
            cam.enabled = false;

        // The sky sphere's faces point outward; draw both sides so it's visible from inside.
        if (skySphere != null && skySphere.material.HasProperty(CullId))
            skySphere.material.SetFloat(CullId, (float)UnityEngine.Rendering.CullMode.Off);

        // Measure the part that should line up with the ring (renderer bounds are only valid once active).
        Transform fit = bigTownFitTo != null ? bigTownFitTo : bigTown;
        var renderers = fit.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            Debug.LogWarning("[Onboarding] Big town fit target has no renderers; can't size it to the ring.");
            return;
        }
        Bounds b = renderers[0].bounds;
        foreach (var r in renderers)
            b.Encapsulate(r.bounds);

        float fitRadius = Mathf.Max(b.extents.x, b.extents.z);
        Vector3 centerOffset = b.center - bigTown.position;

        float k = RingRadius * bigTownSizeMultiplier / fitRadius;
        bigTown.localScale = bigTownStartScale * k;

        // Center of the fit target goes to the ring center (the user); the town's ground (its pivot) goes at the ring's height.
        Vector3 center = ringLine.transform.position;
        bigTown.position = new Vector3(center.x - centerOffset.x * k, center.y, center.z - centerOffset.z * k);

        // Spin the town about the user (the ring center) so the face target lands at a fixed angle from where
        // they're looking, e.g. the clock tower 45 degrees to the right.
        if (bigTownFaceTarget != null)
        {
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            Vector3 toTarget = Vector3.ProjectOnPlane(bigTownFaceTarget.position - center, Vector3.up);
            if (forward.sqrMagnitude > 0.0001f && toTarget.sqrMagnitude > 0.0001f)
            {
                Vector3 desired = Quaternion.Euler(0f, bigTownFaceTargetYaw, 0f) * forward;
                bigTown.RotateAround(center, Vector3.up, Vector3.SignedAngle(toTarget, desired, Vector3.up));
            }
        }

        Debug.Log($"[Onboarding] Big town shown: ring radius {RingRadius:F2} m, town scale x{k:F3}");
    }

    /// <summary>Shows the dome on the floor around the user, sized from the ring.</summary>
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

        // Respawning (e.g. recenter) restarts the sizing step.
        IsConfirmed = false;
        if (bigTown != null)
        {
            bigTown.gameObject.SetActive(false);
            bigTown.localScale = bigTownStartScale;
        }
        if (dome != null)
            dome.gameObject.SetActive(false);

        setupRoot = new GameObject("DomeSetupRoot");

        // Yaw-only forward so the town doesn't end up in the floor/ceiling if the user is looking up or down.
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
        forward.Normalize();

        holeDisc = null;
        holeCeiling = null;
        SpawnRing();
        SpawnTown(forward);

        // The ring starts (and stays, until the bell) at the mini town's base height, centered on the user.
        Vector3 ringPos = ringLine.transform.position;
        ringLine.transform.position = new Vector3(ringPos.x, townBasePosition.y, ringPos.z);

        Spawned?.Invoke();
    }

    // Flat quads with the "Passthrough Hole" shader: one just above the floor and, optionally, one at ceiling height.
    // Each clears whatever virtual content is behind it inside the ring, so the real floor shows inside the ring
    // (and the real ceiling too, if enabled); the town and sky beyond the ring are untouched.
    private void SpawnHoleDiscs()
    {
        if (!passthroughHole)
            return;
        if (passthroughHoleMaterial == null)
        {
            Debug.LogWarning("[Onboarding] Passthrough Hole Material is not assigned.");
            return;
        }

        holeDisc = CreateHoleDisc("PassthroughHole_Floor");
        if (passthroughCeiling)
            holeCeiling = CreateHoleDisc("PassthroughHole_Ceiling");
        PlaceHoleDisc();
    }

    private Transform CreateHoleDisc(string name)
    {
        var disc = GameObject.CreatePrimitive(PrimitiveType.Quad);
        disc.name = name;
        Destroy(disc.GetComponent<Collider>());
        disc.transform.SetParent(setupRoot.transform, false);
        disc.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        var renderer = disc.GetComponent<MeshRenderer>();
        renderer.material = passthroughHoleMaterial;
        renderer.material.SetFloat(HoleFeatherId, passthroughHoleFeather);
        // The shader fades from the radius outward; pull it in by half the feather so the fade straddles the ring.
        renderer.material.SetFloat(HoleExtraId, -passthroughHoleFeather * 0.5f);
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return disc.transform;
    }

    private void PlaceHoleDisc()
    {
        Vector3 center = ringLine.transform.position;
        // Floor-level tracking origin: the tracking space's y is the real floor.
        float floorY = rig != null ? rig.trackingSpace.position.y : 0f;
        Vector3 size = Vector3.one * 2f * (RingRadius + passthroughHoleFeather + 0.05f);

        // The town's ground sits at the ring's height, so the floor cut goes just above the ring.
        holeDisc.position = new Vector3(center.x, center.y + passthroughHoleHeight, center.z);
        holeDisc.localScale = size;
        if (holeCeiling != null)
        {
            float ceilingY = Mathf.Max(floorY + passthroughCeilingHeight, center.y + passthroughHoleHeight + 0.5f);
            holeCeiling.position = new Vector3(center.x, ceilingY, center.z);
            holeCeiling.localScale = size;
        }

        Shader.SetGlobalVector(PassthroughHoleCenterId, center);
        Shader.SetGlobalFloat(PassthroughHoleRadiusId, RingRadius);
    }

    private void SpawnRing()
    {
        var ring = new GameObject("BoundaryRing");
        ring.transform.SetParent(setupRoot.transform, false);
        // Centered on the user; its height is set to the mini town's base once the town is placed.
        ring.transform.position = head.position;

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
