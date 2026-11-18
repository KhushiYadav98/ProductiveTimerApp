using UnityEngine;

// Loops the locomotive's smoke puffs: each puff appears small at the chimney, rises and drifts back along the
// original puff trail while growing, then thins out and dissolves; puffs are staggered so smoke flows steadily.
// Use with the ProductivityTown/Smoke shader (it reads _Fade and _Dissolve).
public class SmokePuffs : MonoBehaviour
{
    [Tooltip("Puffs ordered from the chimney upward (e.g. Loco_Smoke0..3). Their scene positions define the trail.")]
    [SerializeField] private Transform[] puffs;
    [Tooltip("Puff cycles per second while standing.")]
    [SerializeField] private float idleRate = 0.35f;
    [Tooltip("Puff cycles per second while the train is moving.")]
    [SerializeField] private float movingRate = 0.9f;
    [Tooltip("How far past the last puff's position the smoke keeps rising, as a fraction of the trail length.")]
    [SerializeField] private float overshoot = 0.35f;
    [Tooltip("Size of a puff when it leaves the chimney / when it fades, relative to the first / last puff.")]
    [SerializeField] private float startSize = 0.5f;
    [SerializeField] private float endSize = 1.4f;
    [SerializeField] private float sway = 0.15f;

    /// <summary>Set by the train while it's driving; smoke puffs faster.</summary>
    public bool Moving { get; set; }

    private static readonly int FadeId = Shader.PropertyToID("_Fade");
    private static readonly int DissolveId = Shader.PropertyToID("_Dissolve");

    private Vector3[] trail;
    private float startScale, endScale;
    private float[] meshSizes;
    private Renderer[] renderers;
    private MaterialPropertyBlock block;
    private float clock;
    private float rate;

    private void Awake()
    {
        if (puffs == null || puffs.Length < 2)
        {
            enabled = false;
            return;
        }

        int n = puffs.Length;
        trail = new Vector3[n];
        meshSizes = new float[n];
        renderers = new Renderer[n];
        for (int i = 0; i < n; i++)
        {
            trail[i] = puffs[i].localPosition;
            renderers[i] = puffs[i].GetComponent<Renderer>();
            var filter = puffs[i].GetComponent<MeshFilter>();
            float size = filter != null && filter.sharedMesh != null ? filter.sharedMesh.bounds.size.x : 1f;
            meshSizes[i] = size * puffs[i].localScale.x;
        }
        startScale = meshSizes[0] * startSize;
        endScale = meshSizes[n - 1] * endSize;
        block = new MaterialPropertyBlock();
        rate = idleRate;
    }

    private void Update()
    {
        // Ease between idle and moving so the puffing speeds up and slows down with the train.
        rate = Mathf.MoveTowards(rate, Moving ? movingRate : idleRate, Time.deltaTime * 0.8f);
        clock += Time.deltaTime * rate;

        int n = puffs.Length;
        for (int i = 0; i < n; i++)
        {
            float phase = Mathf.Repeat(clock + (float)i / n, 1f);
            var puff = puffs[i];

            // Along the trail (with a bit extra beyond the last puff), rising fastest at first.
            float along = 1f - (1f - phase) * (1f - phase);
            puff.localPosition = TrailPoint(along * (1f + overshoot))
                                 + new Vector3(0f, 0f, Mathf.Sin((clock + i * 1.7f) * 4f) * sway * startScale);

            float size = Mathf.Lerp(startScale, endScale, Mathf.Pow(phase, 0.7f));
            puff.localScale = Vector3.one * (size / meshSizes[i]);

            if (renderers[i] != null)
            {
                float fade = Mathf.SmoothStep(0f, 1f, phase / 0.12f) * (1f - Mathf.SmoothStep(0f, 1f, (phase - 0.55f) / 0.45f));
                renderers[i].GetPropertyBlock(block);
                block.SetFloat(FadeId, fade);
                block.SetFloat(DissolveId, Mathf.Clamp01((phase - 0.45f) / 0.55f) * 0.8f);
                renderers[i].SetPropertyBlock(block);
            }
        }
    }

    // t in [0, 1] runs through the original puff positions; beyond 1 it continues along the last segment.
    private Vector3 TrailPoint(float t)
    {
        int segments = trail.Length - 1;
        float s = t * segments;
        int i = Mathf.Min(Mathf.FloorToInt(s), segments - 1);
        return Vector3.LerpUnclamped(trail[i], trail[i + 1], s - i);
    }
}
