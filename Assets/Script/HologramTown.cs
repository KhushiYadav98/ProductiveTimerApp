using UnityEngine;

// Optional, on the root of a model using the Hologram Blueprint shader.
// Enables the bottom-up "build" reveal: drive `reveal` from 0 to 1 to construct the town.
// The shader works without this component; edges are baked at import (models in a "Hologram" folder).
[DisallowMultipleComponent]
public class HologramTown : MonoBehaviour
{
    [Tooltip("0 = hidden, 1 = fully built. Animate this for the build effect.")]
    [Range(0f, 1f)]
    public float reveal = 1f;

    private static readonly int TownWorldToLocalId = Shader.PropertyToID("_TownWorldToLocal");
    private static readonly int UseTownSpaceId = Shader.PropertyToID("_UseTownSpace");
    private static readonly int TownMinYId = Shader.PropertyToID("_TownMinY");
    private static readonly int TownMaxYId = Shader.PropertyToID("_TownMaxY");
    private static readonly int RevealId = Shader.PropertyToID("_Reveal");

    private Renderer[] renderers;
    private MaterialPropertyBlock block;
    private float minY, maxY;

    private void Awake()
    {
        renderers = GetComponentsInChildren<Renderer>(true);
        block = new MaterialPropertyBlock();

        // Height range in town (root-local) space, from renderer bounds.
        minY = float.MaxValue;
        maxY = float.MinValue;
        foreach (var r in renderers)
        {
            var b = r.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                float y = transform.InverseTransformPoint(corner).y;
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }
        }
    }

    private void LateUpdate()
    {
        foreach (var r in renderers)
        {
            r.GetPropertyBlock(block);
            block.SetFloat(UseTownSpaceId, 1f);
            block.SetMatrix(TownWorldToLocalId, transform.worldToLocalMatrix);
            block.SetFloat(TownMinYId, minY);
            block.SetFloat(TownMaxYId, maxY);
            block.SetFloat(RevealId, reveal);
            r.SetPropertyBlock(block);
        }
    }
}
