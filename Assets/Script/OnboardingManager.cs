using System.Collections;
using UnityEngine;

// Test setup for the dome boundary flow:
// spawns a mini town model in front of the user and a glowing ring centered on the user, slightly below the head.
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

    private Transform head;
    private GameObject setupRoot;
    private Transform town;
    private Vector3 townBasePosition;

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

        var line = ring.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.loop = true;
        line.positionCount = ringSegments;
        line.widthMultiplier = ringWidth;
        line.material = ringMaterial != null ? ringMaterial : new Material(Shader.Find("Sprites/Default"));
        line.startColor = line.endColor = ringColor;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;

        for (int i = 0; i < ringSegments; i++)
        {
            float a = i * Mathf.PI * 2f / ringSegments;
            line.SetPosition(i, new Vector3(Mathf.Cos(a) * ringRadius, 0f, Mathf.Sin(a) * ringRadius));
        }
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
