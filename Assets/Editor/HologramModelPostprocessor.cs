using UnityEditor;
using UnityEngine;

// Any model inside a folder named "Hologram" gets feature-edge data baked in at import time,
// so the Hologram Blueprint shader draws glowing edges just by assigning the material.
// To re-bake after changing the angle: right-click the model > Reimport.
public class HologramModelPostprocessor : AssetPostprocessor
{
    private const float FeatureAngle = HologramEdgeBaker.DefaultFeatureAngle;

    public override uint GetVersion() => 1;

    private void OnPostprocessModel(GameObject root)
    {
        if (!assetPath.Replace('\\', '/').Contains("/Hologram/"))
            return;

        int count = 0;
        foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null)
                continue;
            HologramEdgeBaker.BakeInPlace(filter.sharedMesh, FeatureAngle);
            count++;
        }
        Debug.Log($"[Hologram] Baked edges into {count} meshes of {assetPath}");
    }
}
