using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// "Building" reveal for the life-size world, in two steps:
//   1. The sky sphere assembles tile by tile, bottom to top (Sky Block Reveal), while the town stays hidden.
//   2. The town is constructed piece by piece (each direct child of the pieces root is one piece):
//      - Foundation: the flat pieces (ground, paths, plaza, river, track...) spread out from the user.
//      - Then a construction wave sweeps around the user, starting in front of them:
//        big buildings rise floor by floor behind a glowing work line, with construction dust billowing at their
//        base and hammer knocks; small props (trees, lamps, fences...) pop up out of the ground with a puff.
// For the length of the reveal, URP/Lit materials are swapped for look-alike Build Reveal copies (which can cut a
// building above its work line), then the originals are put back. Excluded renderers show right away.
public class TownBuildReveal : MonoBehaviour
{
    [Tooltip("Material using ProductivityTown/Build Reveal; copied per original material (keeps the shader in builds).")]
    [SerializeField] private Material revealTemplate;
    [Tooltip("Renderers that are not revealed (shown immediately).")]
    [SerializeField] private Renderer[] exclude;

    [Header("Sky (built first)")]
    [Tooltip("The sky sphere (e.g. Env_Sphere); it builds block by block before the town.")]
    [SerializeField] private Renderer sky;
    [Tooltip("Material using ProductivityTown/Sky Block Reveal (keeps the shader in builds).")]
    [SerializeField] private Material skyTemplate;
    [SerializeField] private float skyDuration = 2.5f;
    [Tooltip("Pause between the finished sky and the town starting to build.")]
    [SerializeField] private float skyToTownDelay = 0.15f;

    [Header("Town construction")]
    [Tooltip("Parent whose direct children are the construction pieces (e.g. 'Town'). Empty: a child named 'Town', " +
             "or this object.")]
    [SerializeField] private Transform piecesRoot;
    [Tooltip("Seconds for the foundation (flat ground pieces) to spread out.")]
    [SerializeField] private float foundationTime = 1f;
    [Tooltip("Seconds over which the construction wave goes around the user (when pieces start building).")]
    [SerializeField] private float duration = 3.5f;
    [Tooltip("Seconds a big building takes to rise.")]
    [SerializeField] private float bigBuildTime = 1.4f;
    [Tooltip("Seconds a small prop takes to pop up.")]
    [SerializeField] private float smallPopTime = 0.45f;
    [Tooltip("Pieces with at least this many renderers, and at least this tall relative to the tallest piece, " +
             "are built as big buildings.")]
    [SerializeField] private int bigMinRenderers = 7;
    [SerializeField] private float bigMinHeightFraction = 0.2f;
    [Tooltip("Height of the glowing work line on big buildings, as a fraction of the building's height.")]
    [SerializeField] private float edgeBandFraction = 0.1f;
    [ColorUsage(false, true)]
    [SerializeField] private Color edgeColor = new Color(1.6f, 0.95f, 0.25f, 1f);

    [Header("Construction dust")]
    [Tooltip("ProductivityTown/Particle Soft material (Puff shape), e.g. FX_ConstructionDust.")]
    [SerializeField] private Material dustMaterial;
    [SerializeField] private Color dustColorA = new Color(0.86f, 0.78f, 0.64f, 0.75f);
    [SerializeField] private Color dustColorB = new Color(0.72f, 0.65f, 0.55f, 0.65f);
    [Tooltip("Dust puffs per second around a big building while it rises.")]
    [SerializeField] private float bigDustRate = 30f;

    public enum RevealPhase { None, Sky, Town }

    public bool IsRevealing => routine != null;
    /// <summary>What's being revealed right now (for sound/FX hooks).</summary>
    public RevealPhase Phase { get; private set; }
    /// <summary>0..1 progress of the current phase.</summary>
    public float Progress { get; private set; }
    public float Duration => foundationTime + duration + bigBuildTime;

    /// <summary>A piece starts building: its base position, its size (meters) and whether it's a big building.</summary>
    public event System.Action<Vector3, float, bool> PieceStarted;
    /// <summary>A hammer blow on a big building under construction (position on the building, its size).</summary>
    public event System.Action<Vector3, float> Hammer;
    /// <summary>A big building is finished (its base position and size).</summary>
    public event System.Action<Vector3, float> BuildingFinished;

    private static readonly int BuildRevealYId = Shader.PropertyToID("_BuildRevealY");
    private static readonly int UnitRevealYId = Shader.PropertyToID("_UnitRevealY");
    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
    private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");
    private static readonly int CullId = Shader.PropertyToID("_Cull");
    private static readonly int EdgeColorId = Shader.PropertyToID("_RevealEdgeColor");
    private static readonly int BandId = Shader.PropertyToID("_RevealBand");
    private static readonly int SkyRevealId = Shader.PropertyToID("_SkyReveal");
    private static readonly int SpecularHighlightsId = Shader.PropertyToID("_SpecularHighlights");
    private static readonly int EnvironmentReflectionsId = Shader.PropertyToID("_EnvironmentReflections");

    private enum Kind { Foundation, Small, Big }

    private class Piece
    {
        public Transform transform;
        public Renderer[] renderers;
        public Kind kind;
        public Bounds bounds;
        public Vector3 pivot;        // bottom center, world
        public Vector3 restPosition; // world
        public Vector3 restScale;    // local
        public float start, length;
        public bool started, finished;
        public float nextHammer, dustCarry;
        public float Size => Mathf.Max(bounds.size.x, bounds.size.z);
    }

    private readonly Dictionary<Material, Material> copies = new Dictionary<Material, Material>();
    private readonly List<(Renderer renderer, Material[] originals)> swapped = new List<(Renderer, Material[])>();
    private readonly List<Piece> pieces = new List<Piece>();
    private readonly List<Renderer> hidden = new List<Renderer>();
    private Material[] skyCopy;
    private MaterialPropertyBlock block;
    private ParticleSystem dust;
    private Coroutine routine;

    public void SetExcluded(params Renderer[] renderers) => exclude = renderers;
    public void SetSky(Renderer skyRenderer) => sky = skyRenderer;

    /// <summary>Plays the sky, then the town construction. Call once the town is active and placed.</summary>
    public void Play()
    {
        Cancel();
        if (revealTemplate == null)
        {
            Debug.LogWarning("[TownBuildReveal] No reveal material set; showing the town without the build effect.");
            return;
        }
        routine = StartCoroutine(Reveal());
    }

    /// <summary>Stops the reveal and puts everything back as it was (the town is fully shown).</summary>
    public void Cancel()
    {
        if (routine != null)
        {
            StopCoroutine(routine);
            routine = null;
        }
        Restore();
    }

    private void OnDisable() => Cancel();

    private IEnumerator Reveal()
    {
        block ??= new MaterialPropertyBlock();
        var rig = FindFirstObjectByType<OVRCameraRig>();
        Transform head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;

        // Swap in reveal copies (hide renderers that can't use them, e.g. smoke and particles).
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            if (r == sky || System.Array.IndexOf(exclude ?? new Renderer[0], r) >= 0)
                continue;
            if (r is ParticleSystemRenderer)
            {
                Hide(r);
                continue;
            }

            var originals = r.sharedMaterials;
            var replaced = (Material[])originals.Clone();
            bool any = false;
            for (int i = 0; i < replaced.Length; i++)
            {
                var copy = GetCopy(originals[i]);
                if (copy == null)
                    continue;
                replaced[i] = copy;
                any = true;
            }
            if (!any)
            {
                Hide(r);
                continue;
            }
            swapped.Add((r, originals));
            r.sharedMaterials = replaced;
        }

        // Step 1: the town is hidden while the sky assembles.
        Shader.SetGlobalFloat(BuildRevealYId, -1e6f);
        if (sky != null && skyTemplate != null && sky.gameObject.activeInHierarchy)
        {
            var skyOriginals = sky.sharedMaterials;
            var skyCopies = new Material[skyOriginals.Length];
            for (int i = 0; i < skyOriginals.Length; i++)
                skyCopies[i] = skyOriginals[i] != null ? MakeCopy(skyOriginals[i], skyTemplate) : null;
            sky.sharedMaterials = skyCopies;
            swapped.Add((sky, skyOriginals));
            skyCopy = skyCopies;

            Phase = RevealPhase.Sky;
            for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(skyDuration, 0.01f))
            {
                Progress = t;
                foreach (var m in skyCopies)
                    if (m != null)
                        m.SetFloat(SkyRevealId, t);
                yield return null;
            }
            RestoreSky();
            Phase = RevealPhase.None;
            yield return new WaitForSeconds(skyToTownDelay);
        }

        // Step 2: construct the town piece by piece.
        BuildPieces(head);
        foreach (var p in pieces)
            Apply(p, 0f);
        Shader.SetGlobalFloat(BuildRevealYId, 1e6f); // no global cut; big buildings use their own line
        if (dust == null && dustMaterial != null)
            dust = CreateDust();

        Phase = RevealPhase.Town;
        float total = 0f;
        foreach (var p in pieces)
            total = Mathf.Max(total, p.start + p.length);
        float time = 0f;
        while (time < total)
        {
            time += Time.deltaTime;
            Progress = Mathf.Clamp01(time / Mathf.Max(total, 0.01f));
            foreach (var p in pieces)
            {
                if (p.finished || time < p.start)
                    continue;
                if (!p.started)
                {
                    p.started = true;
                    OnPieceStarted(p);
                }
                float k = Mathf.Clamp01((time - p.start) / p.length);
                Apply(p, k);
                if (p.kind == Kind.Big)
                    WhileBuilding(p, k);
                if (k >= 1f)
                {
                    p.finished = true;
                    OnPieceFinished(p);
                }
            }
            yield return null;
        }

        routine = null;
        Restore();
    }

    // Splits the town into pieces (direct children of the pieces root) and schedules them.
    private void BuildPieces(Transform head)
    {
        pieces.Clear();
        Transform root = piecesRoot;
        if (root == null)
            root = transform.Find("Town");
        if (root == null)
            root = transform;

        float tallest = 0f;
        foreach (Transform child in root)
        {
            if (!child.gameObject.activeInHierarchy)
                continue;
            var renderers = child.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0 || System.Array.IndexOf(renderers, sky) >= 0)
                continue;

            Bounds b = renderers[0].bounds;
            foreach (var r in renderers)
                b.Encapsulate(r.bounds);
            var p = new Piece
            {
                transform = child,
                renderers = renderers,
                bounds = b,
                pivot = new Vector3(b.center.x, b.min.y, b.center.z),
                restPosition = child.position,
                restScale = child.localScale,
            };
            float footprint = Mathf.Max(b.size.x, b.size.z);
            p.kind = b.size.y < footprint * 0.2f ? Kind.Foundation : Kind.Small;
            tallest = Mathf.Max(tallest, b.size.y);
            pieces.Add(p);
        }

        // Foundation spreads out from the user; the construction wave sweeps around them from the front.
        Vector3 center = head.position;
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;
        float farthest = 0.01f;
        foreach (var p in pieces)
            farthest = Mathf.Max(farthest, Vector3.ProjectOnPlane(p.bounds.center - center, Vector3.up).magnitude);

        foreach (var p in pieces)
        {
            Vector3 flat = Vector3.ProjectOnPlane(p.bounds.center - center, Vector3.up);
            if (p.kind == Kind.Foundation)
            {
                float d = flat.magnitude / farthest;
                p.start = d * foundationTime * 0.6f + Random.Range(0f, 0.08f);
                p.length = foundationTime * 0.45f;
                continue;
            }

            if (p.renderers.Length >= bigMinRenderers && p.bounds.size.y >= tallest * bigMinHeightFraction)
                p.kind = Kind.Big;
            float angle = Vector3.SignedAngle(forward, flat, Vector3.up); // -180..180, 0 = in front
            float around = (angle < 0f ? angle + 360f : angle) / 360f;   // clockwise from the front
            float order = Mathf.Clamp01(around * 0.85f + Random.Range(0f, 0.15f));
            p.start = foundationTime * 0.7f + order * duration;
            p.length = p.kind == Kind.Big ? bigBuildTime : smallPopTime * Random.Range(0.85f, 1.15f);
        }
    }

    // k: 0 = not built yet, 1 = finished.
    private void Apply(Piece p, float k)
    {
        switch (p.kind)
        {
            case Kind.Foundation:
            {
                // Spread out from its middle.
                float s = 1f - Mathf.Pow(1f - k, 3f);
                SetScale(p, Mathf.Max(s, 0.0001f), 1f);
                break;
            }
            case Kind.Small:
            {
                // Pop up out of the ground: shoot up past full height, then settle; widen a touch later.
                float sy = k <= 0f ? 0.0001f : EaseOutBack(k, 2.2f);
                float sxz = k <= 0f ? 0.0001f : EaseOutBack(Mathf.Clamp01(k * 1.25f), 1.4f);
                SetScale(p, sxz, sy);
                break;
            }
            case Kind.Big:
            {
                // Rises floor by floor behind its work line, then a little settle as it completes.
                float band = Mathf.Max(0.01f, p.bounds.size.y * edgeBandFraction);
                float line = k <= 0f ? p.bounds.min.y - 1f
                    : Mathf.Lerp(p.bounds.min.y, p.bounds.max.y + band, 1f - (1f - k) * (1f - k) * 0.5f - 0.5f * (1f - k));
                foreach (var r in p.renderers)
                {
                    if (r == null)
                        continue;
                    r.GetPropertyBlock(block);
                    block.SetFloat(UnitRevealYId, line);
                    block.SetFloat(BandId, band);
                    r.SetPropertyBlock(block);
                }
                float settle = k > 0.9f ? 1f + Mathf.Sin((k - 0.9f) / 0.1f * Mathf.PI) * 0.03f : 1f;
                SetScale(p, 1f, settle);
                break;
            }
        }
    }

    // Scales the piece about its bottom center (sxz across, sy up).
    private static void SetScale(Piece p, float sxz, float sy)
    {
        var t = p.transform;
        Vector3 s = new Vector3(sxz, sy, sxz);
        Quaternion rot = t.rotation;
        Vector3 local = Quaternion.Inverse(rot) * (p.restPosition - p.pivot);
        t.position = p.pivot + rot * Vector3.Scale(s, local);
        t.localScale = Vector3.Scale(p.restScale, s);
    }

    private void OnPieceStarted(Piece p)
    {
        float size = p.Size;
        // Small props: a little puff on most of them (kept light: there are hundreds of props).
        if (p.kind == Kind.Small && Random.value < 0.6f)
            EmitDust(p.pivot, size, Random.Range(1, 3), 0.5f);
        else if (p.kind == Kind.Big)
            p.nextHammer = 0f;
        PieceStarted?.Invoke(p.pivot, size, p.kind == Kind.Big);
    }

    private void WhileBuilding(Piece p, float k)
    {
        // Dust billowing around the base while it rises.
        p.dustCarry += bigDustRate * Time.deltaTime * (1f - k * 0.6f);
        int n = Mathf.FloorToInt(p.dustCarry);
        if (n > 0)
        {
            p.dustCarry -= n;
            EmitDust(p.pivot, p.Size, n, 1f);
        }

        // Hammer blows somewhere on the part being built.
        float t = k * p.length;
        if (t >= p.nextHammer && k < 0.95f)
        {
            p.nextHammer = t + Random.Range(0.18f, 0.32f);
            float y = Mathf.Lerp(p.bounds.min.y, p.bounds.max.y, k);
            Vector3 at = new Vector3(p.bounds.center.x, y, p.bounds.center.z)
                         + Vector3.ProjectOnPlane(Random.onUnitSphere, Vector3.up) * p.Size * 0.4f;
            Hammer?.Invoke(at, p.Size);
        }
    }

    private void OnPieceFinished(Piece p)
    {
        if (p.kind == Kind.Big)
        {
            // Clear the work line and finish with a dust poof.
            foreach (var r in p.renderers)
                if (r != null)
                    r.SetPropertyBlock(null);
            EmitDust(p.pivot, p.Size, 16, 1.6f);
            BuildingFinished?.Invoke(p.pivot, p.Size);
        }
        SetScale(p, 1f, 1f);
        p.transform.position = p.restPosition;
        p.transform.localScale = p.restScale;

        // Renderers this piece hid (e.g. the train's smoke) come back with it.
        for (int i = hidden.Count - 1; i >= 0; i--)
        {
            var h = hidden[i];
            if (h != null && h.transform.IsChildOf(p.transform))
            {
                h.enabled = true;
                hidden.RemoveAt(i);
            }
        }
    }

    // Puffs around a base point, sized to the piece; strength scales size and spread.
    private void EmitDust(Vector3 basePoint, float size, int count, float strength)
    {
        if (dust == null || count <= 0)
            return;
        float s = Mathf.Clamp(size, 0.05f, 4f);
        var p = new ParticleSystem.EmitParams();
        for (int i = 0; i < count; i++)
        {
            Vector2 c = Random.insideUnitCircle.normalized * Random.Range(0.35f, 0.6f);
            Vector3 offset = new Vector3(c.x, 0f, c.y) * s;
            p.position = basePoint + offset + Vector3.up * s * 0.05f;
            p.velocity = (offset.normalized * Random.Range(0.1f, 0.35f) + Vector3.up * Random.Range(0.15f, 0.45f)) * s * strength;
            p.startSize = s * Random.Range(0.25f, 0.45f) * Mathf.Lerp(0.7f, 1.2f, strength * 0.5f);
            p.startLifetime = Random.Range(1.2f, 2.2f);
            p.startColor = Color.Lerp(dustColorA, dustColorB, Random.value);
            p.rotation = Random.Range(0f, 360f);
            dust.Emit(p, 1);
        }
    }

    private ParticleSystem CreateDust()
    {
        var go = new GameObject("ConstructionRevealDust");
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Shape;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        main.maxParticles = 1500;
        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        shape.enabled = false;

        var limit = ps.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.limit = 0.05f;
        limit.dampen = 0.06f;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.5f, 1f, 1.8f));

        var color = ps.colorOverLifetime;
        color.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(0f, 1f) });
        color.color = new ParticleSystem.MinMaxGradient(g);

        var rotation = ps.rotationOverLifetime;
        rotation.enabled = true;
        rotation.z = new ParticleSystem.MinMaxCurve(-0.6f, 0.6f);

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = dustMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sortMode = ParticleSystemSortMode.Distance;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        ps.Play();
        return ps;
    }

    private static float EaseOutBack(float t, float overshoot)
    {
        float c3 = overshoot + 1f;
        float x = t - 1f;
        return 1f + c3 * x * x * x + overshoot * x * x;
    }

    // A Build Reveal copy of a URP/Lit material (cached); null for other shaders, which are left alone.
    private Material GetCopy(Material original)
    {
        if (original == null || original.shader == null || original.shader.name != "Universal Render Pipeline/Lit")
            return null;
        if (copies.TryGetValue(original, out var copy))
            return copy;
        copy = MakeCopy(original, revealTemplate);
        copy.SetColor(EdgeColorId, edgeColor);
        copies[original] = copy;
        return copy;
    }

    // A copy of the template carrying the original's URP/Lit look (color, texture, metal/smoothness, emission, culling).
    private static Material MakeCopy(Material original, Material template)
    {
        var copy = new Material(template) { name = original.name + " (Reveal)" };
        if (original.HasProperty(BaseMapId))
        {
            copy.SetTexture(BaseMapId, original.GetTexture(BaseMapId));
            copy.SetTextureScale(BaseMapId, original.GetTextureScale(BaseMapId));
            copy.SetTextureOffset(BaseMapId, original.GetTextureOffset(BaseMapId));
        }
        if (original.HasProperty(BaseColorId))
            copy.SetColor(BaseColorId, original.GetColor(BaseColorId));
        if (original.HasProperty(MetallicId))
            copy.SetFloat(MetallicId, original.GetFloat(MetallicId));
        if (original.HasProperty(SmoothnessId))
            copy.SetFloat(SmoothnessId, original.GetFloat(SmoothnessId));
        if (original.HasProperty(CullId))
            copy.SetFloat(CullId, original.GetFloat(CullId));
        copy.SetColor(EmissionColorId, original.IsKeywordEnabled("_EMISSION") && original.HasProperty(EmissionColorId)
            ? original.GetColor(EmissionColorId) : Color.black);
        copy.SetTexture(EmissionMapId, original.IsKeywordEnabled("_EMISSION") && original.HasProperty(EmissionMapId)
            ? original.GetTexture(EmissionMapId) : null);

        bool specularOff = original.IsKeywordEnabled("_SPECULARHIGHLIGHTS_OFF");
        bool reflectionsOff = original.IsKeywordEnabled("_ENVIRONMENTREFLECTIONS_OFF");
        copy.SetFloat(SpecularHighlightsId, specularOff ? 0f : 1f);
        copy.SetFloat(EnvironmentReflectionsId, reflectionsOff ? 0f : 1f);
        if (specularOff) copy.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
        if (reflectionsOff) copy.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
        return copy;
    }

    private void Hide(Renderer r)
    {
        if (!r.enabled)
            return;
        r.enabled = false;
        hidden.Add(r);
    }

    private void RestoreSky()
    {
        for (int i = swapped.Count - 1; i >= 0; i--)
        {
            if (swapped[i].renderer != sky)
                continue;
            if (sky != null)
                sky.sharedMaterials = swapped[i].originals;
            swapped.RemoveAt(i);
        }
        if (skyCopy != null)
        {
            foreach (var m in skyCopy)
                if (m != null)
                    Destroy(m);
            skyCopy = null;
        }
    }

    private void Restore()
    {
        Phase = RevealPhase.None;
        Progress = 0f;
        RestoreSky();
        Shader.SetGlobalFloat(BuildRevealYId, 1e6f);

        // Pieces back to their exact rest pose; per-object work lines cleared.
        foreach (var p in pieces)
        {
            if (p.transform == null)
                continue;
            p.transform.position = p.restPosition;
            p.transform.localScale = p.restScale;
            if (p.kind == Kind.Big)
                foreach (var r in p.renderers)
                    if (r != null)
                        r.SetPropertyBlock(null);
        }
        pieces.Clear();

        foreach (var h in hidden)
            if (h != null)
                h.enabled = true;
        hidden.Clear();
        foreach (var (r, originals) in swapped)
            if (r != null)
                r.sharedMaterials = originals;
        swapped.Clear();
    }

    private void OnDestroy()
    {
        foreach (var copy in copies.Values)
            Destroy(copy);
        copies.Clear();
        if (dust != null)
            Destroy(dust.gameObject);
    }
}
