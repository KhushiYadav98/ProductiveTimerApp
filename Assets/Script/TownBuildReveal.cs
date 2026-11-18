using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// "Building" reveal for the life-size world, in two steps:
//   1. The sky sphere assembles tile by tile, bottom to top (Sky Block Reveal), while the town stays hidden.
//   2. The town appears from the ground up behind a rising, glowing build line (Build Reveal).
// For the length of the reveal, URP/Lit materials are swapped for look-alike reveal copies, then the originals are
// put back. Excluded renderers show right away.
public class TownBuildReveal : MonoBehaviour
{
    [Tooltip("Material using ProductivityTown/Build Reveal; copied per original material (keeps the shader in builds).")]
    [SerializeField] private Material revealTemplate;
    [Tooltip("Seconds for the town's ground-to-rooftop build.")]
    [SerializeField] private float duration = 4f;
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
    [Tooltip("Height of the glowing band below the build line, as a fraction of the town's height.")]
    [SerializeField] private float edgeBandFraction = 0.08f;
    [ColorUsage(false, true)]
    [SerializeField] private Color edgeColor = new Color(1.6f, 0.95f, 0.25f, 1f);

    public bool IsRevealing => routine != null;
    public float Duration => duration;

    private static readonly int BuildRevealYId = Shader.PropertyToID("_BuildRevealY");
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

    private readonly Dictionary<Material, Material> copies = new Dictionary<Material, Material>();
    private readonly List<(Renderer renderer, Material[] originals)> swapped = new List<(Renderer, Material[])>();
    private Coroutine routine;

    public void SetExcluded(params Renderer[] renderers) => exclude = renderers;
    public void SetSky(Renderer skyRenderer) => sky = skyRenderer;

    /// <summary>Plays the bottom-to-top build. Call once the town is active and placed.</summary>
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

    /// <summary>Stops the reveal and puts the original materials back (the town is fully shown).</summary>
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
        // Swap in reveal copies and measure the height range of what's being revealed.
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer || r == sky || System.Array.IndexOf(exclude ?? new Renderer[0], r) >= 0)
                continue;

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
                continue;

            swapped.Add((r, originals));
            r.sharedMaterials = replaced;
            if (r.gameObject.activeInHierarchy)
            {
                minY = Mathf.Min(minY, r.bounds.min.y);
                maxY = Mathf.Max(maxY, r.bounds.max.y);
            }
        }

        if (minY > maxY)
        {
            routine = null;
            Restore();
            yield break;
        }

        float band = Mathf.Max(0.005f, (maxY - minY) * edgeBandFraction);
        foreach (var copy in copies.Values)
            copy.SetFloat(BandId, band);
        float from = minY - 0.01f, to = maxY + band;

        // Step 1: the town is hidden (build line below the ground) while the sky assembles.
        Shader.SetGlobalFloat(BuildRevealYId, from - 1000f);
        if (sky != null && skyTemplate != null && sky.gameObject.activeInHierarchy)
        {
            var skyOriginals = sky.sharedMaterials;
            var skyCopies = new Material[skyOriginals.Length];
            for (int i = 0; i < skyOriginals.Length; i++)
                skyCopies[i] = skyOriginals[i] != null ? MakeCopy(skyOriginals[i], skyTemplate) : null;
            sky.sharedMaterials = skyCopies;
            swapped.Add((sky, skyOriginals));
            skyCopy = skyCopies;

            for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(skyDuration, 0.01f))
            {
                foreach (var m in skyCopies)
                    if (m != null)
                        m.SetFloat(SkyRevealId, t);
                yield return null;
            }
            RestoreSky();
            yield return new WaitForSeconds(skyToTownDelay);
        }

        // Step 2: the town builds from the ground up.
        for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(duration, 0.01f))
        {
            // Ease in-out: a quick foundation, steady build, gentle finish at the rooftops.
            float k = t * t * (3f - 2f * t);
            Shader.SetGlobalFloat(BuildRevealYId, Mathf.Lerp(from, to, k));
            yield return null;
        }
        Shader.SetGlobalFloat(BuildRevealYId, to);

        routine = null;
        Restore();
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

    private Material[] skyCopy;

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
        RestoreSky();
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
    }
}
