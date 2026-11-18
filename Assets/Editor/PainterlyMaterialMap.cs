using System.Collections.Generic;
using UnityEngine;

// Records which original material each Painterly material replaced, so PainterlyStyleTool can revert the town.
public class PainterlyMaterialMap : ScriptableObject
{
    [System.Serializable]
    public struct Pair
    {
        public Material original;
        public Material painterly;
    }

    public List<Pair> pairs = new List<Pair>();
}
