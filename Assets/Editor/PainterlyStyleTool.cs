using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Tools > Productivity Town > Town Style (the "ProductivityTown/Ghibli Toon" look; files keep the Painterly name):
//  - Generate Pattern Texture: bakes the tileable 256x256 gouache texture the shader samples
//    (R = soft dapple patches, G = shadow-shape breakup, B = small speckles, A = paper grain).
//  - Apply To ProductivityTown_Unity: makes a styled copy of each URP Lit material used under the
//    ProductivityTown_Unity object (colors and emission carried over) and assigns them there; the sky sphere and
//    any other shaders are left alone. The original materials are recorded so it can be reverted.
//  - Revert ProductivityTown_Unity: puts the original materials back.
//  - Sync Settings From Template: the look (dapple, shadows, ink, haze...) of all painterly materials comes from
//    Painterly_Template.mat; tweak that one material, then run this to copy its settings to every town material
//    (each keeps its own color, albedo and emission).
public static class PainterlyStyleTool
{
    private const string Folder = "Assets/Materials/Painterly";
    private const string BrushPath = Folder + "/PainterlyBrush.png";
    private const string MapPath = Folder + "/PainterlyMaterialMap.asset";
    private const string TemplatePath = Folder + "/Painterly_Template.mat";
    private const string ShaderName = "ProductivityTown/Ghibli Toon";
    private const string TownName = "ProductivityTown_Unity";
    private static readonly string[] SkippedRenderers = { "Env_Sphere" };

    [MenuItem("Tools/Productivity Town/Town Style/Generate Pattern Texture")]
    public static Texture2D GenerateBrushTexture()
    {
        EnsureFolder();
        const int size = 256;
        var rng = new System.Random(1234);

        // Dapple: low-frequency noise pushed toward two tones, so patches have soft but readable edges (gouache/moss).
        float[] dappleNoise = Fbm(size, new[] { 4, 8, 16 }, new[] { 0.55f, 0.3f, 0.15f }, rng);
        var dapple = new float[dappleNoise.Length];
        for (int i = 0; i < dapple.Length; i++)
            dapple[i] = Mathf.Lerp(dappleNoise[i], Mathf.SmoothStep(0f, 1f, (dappleNoise[i] - 0.38f) / 0.24f), 0.7f);
        float[] breakup = Fbm(size, new[] { 8, 16, 32 }, new[] { 0.5f, 0.32f, 0.18f }, rng);
        float[] speckles = Speckles(size, 420, rng);
        float[] grain = Fbm(size, new[] { 32, 64, 128 }, new[] { 0.4f, 0.35f, 0.25f }, rng);

        var pixels = new Color32[size * size];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color32(ToByte(dapple[i]), ToByte(breakup[i]), ToByte(speckles[i]), ToByte(grain[i]));

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
        tex.SetPixels32(pixels);
        tex.Apply();
        File.WriteAllBytes(BrushPath, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(BrushPath);

        var importer = (TextureImporter)AssetImporter.GetAtPath(BrushPath);
        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = false; // data, not color
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.filterMode = FilterMode.Bilinear;
        importer.mipmapEnabled = true;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
        importer.SaveAndReimport();

        Debug.Log($"[TownStyle] Pattern texture written to {BrushPath}");
        return AssetDatabase.LoadAssetAtPath<Texture2D>(BrushPath);
    }

    [MenuItem("Tools/Productivity Town/Town Style/Apply To ProductivityTown_Unity")]
    public static void Apply()
    {
        var town = GameObject.Find(TownName);
        if (town == null)
        {
            Debug.LogError($"[TownStyle] No '{TownName}' in the open scene.");
            return;
        }
        var shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogError($"[TownStyle] Shader '{ShaderName}' not found.");
            return;
        }

        EnsureFolder();
        var brush = AssetDatabase.LoadAssetAtPath<Texture2D>(BrushPath) ?? GenerateBrushTexture();
        var map = LoadOrCreateMap();

        var created = new Dictionary<Material, Material>();
        int renderers = 0;
        foreach (var renderer in town.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is ParticleSystemRenderer || System.Array.IndexOf(SkippedRenderers, renderer.name) >= 0)
                continue;

            var materials = renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < materials.Length; i++)
            {
                var source = materials[i];
                if (source == null || source.shader == null || source.shader.name != "Universal Render Pipeline/Lit")
                    continue;

                if (!created.TryGetValue(source, out var painterly))
                {
                    painterly = GetOrCreatePainterly(source, shader, brush, map);
                    created[source] = painterly;
                }
                materials[i] = painterly;
                changed = true;
            }

            if (changed)
            {
                Undo.RecordObject(renderer, "Apply Painterly Style");
                renderer.sharedMaterials = materials;
                renderers++;
            }
        }

        EditorUtility.SetDirty(map);
        AssetDatabase.SaveAssets();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(town.scene);
        Debug.Log($"[TownStyle] Applied to {renderers} renderers using {created.Count} painterly materials.");
    }

    [MenuItem("Tools/Productivity Town/Town Style/Revert ProductivityTown_Unity")]
    public static void Revert()
    {
        var town = GameObject.Find(TownName);
        var map = AssetDatabase.LoadAssetAtPath<PainterlyMaterialMap>(MapPath);
        if (town == null || map == null)
        {
            Debug.LogWarning("[TownStyle] Nothing to revert.");
            return;
        }

        var lookup = new Dictionary<Material, Material>();
        foreach (var pair in map.pairs)
            if (pair.painterly != null && pair.original != null)
                lookup[pair.painterly] = pair.original;

        int renderers = 0;
        foreach (var renderer in town.GetComponentsInChildren<Renderer>(true))
        {
            var materials = renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null && lookup.TryGetValue(materials[i], out var original))
                {
                    materials[i] = original;
                    changed = true;
                }
            }
            if (changed)
            {
                Undo.RecordObject(renderer, "Revert Painterly Style");
                renderer.sharedMaterials = materials;
                renderers++;
            }
        }
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(town.scene);
        Debug.Log($"[TownStyle] Reverted {renderers} renderers.");
    }

    private static Material GetOrCreatePainterly(Material source, Shader shader, Texture2D brush, PainterlyMaterialMap map)
    {
        foreach (var pair in map.pairs)
            if (pair.original == source && pair.painterly != null)
                return pair.painterly;

        // Same-named materials can come from different models, so prefix with the model name.
        string model = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(source));
        string path = AssetDatabase.GenerateUniqueAssetPath($"{Folder}/{model}_{source.name}.mat");

        var mat = new Material(LoadOrCreateTemplate(shader, brush)) { name = Path.GetFileNameWithoutExtension(path), enableInstancing = true };
        mat.SetColor("_BaseColor", source.GetColor("_BaseColor"));
        mat.SetTexture("_BaseMap", source.GetTexture("_BaseMap"));
        Color emission = source.IsKeywordEnabled("_EMISSION") ? source.GetColor("_EmissionColor") : Color.black;
        mat.SetColor("_EmissionColor", emission);

        AssetDatabase.CreateAsset(mat, path);
        map.pairs.Add(new PainterlyMaterialMap.Pair { original = source, painterly = mat });
        return mat;
    }

    [MenuItem("Tools/Productivity Town/Town Style/Sync Settings From Template")]
    public static void SyncFromTemplate()
    {
        var map = AssetDatabase.LoadAssetAtPath<PainterlyMaterialMap>(MapPath);
        var shader = Shader.Find(ShaderName);
        if (map == null || shader == null)
        {
            Debug.LogWarning("[TownStyle] Apply the style first.");
            return;
        }
        var template = LoadOrCreateTemplate(shader, AssetDatabase.LoadAssetAtPath<Texture2D>(BrushPath));

        int count = 0;
        foreach (var pair in map.pairs)
        {
            var mat = pair.painterly;
            if (mat == null)
                continue;
            Color color = mat.GetColor("_BaseColor");
            Texture albedo = mat.GetTexture("_BaseMap");
            Color emission = mat.GetColor("_EmissionColor");
            mat.CopyPropertiesFromMaterial(template);
            mat.SetColor("_BaseColor", color);
            mat.SetTexture("_BaseMap", albedo);
            mat.SetColor("_EmissionColor", emission);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            count++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[TownStyle] Synced {count} materials from {TemplatePath}.");
    }

    private static Material LoadOrCreateTemplate(Shader shader, Texture2D brush)
    {
        var template = AssetDatabase.LoadAssetAtPath<Material>(TemplatePath);
        if (template != null)
            return template;
        template = new Material(shader) { name = "Painterly_Template", enableInstancing = true };
        template.SetTexture("_BrushTex", brush);
        AssetDatabase.CreateAsset(template, TemplatePath);
        return template;
    }

    private static PainterlyMaterialMap LoadOrCreateMap()
    {
        var map = AssetDatabase.LoadAssetAtPath<PainterlyMaterialMap>(MapPath);
        if (map != null)
            return map;
        map = ScriptableObject.CreateInstance<PainterlyMaterialMap>();
        AssetDatabase.CreateAsset(map, MapPath);
        return map;
    }

    private static void EnsureFolder()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");
        if (!AssetDatabase.IsValidFolder(Folder))
            AssetDatabase.CreateFolder("Assets/Materials", "Painterly");
    }

    private static byte ToByte(float v) => (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f);

    // Small soft dots of random size scattered over a dark background, wrapped so the texture tiles.
    private static float[] Speckles(int size, int count, System.Random rng)
    {
        var v = new float[size * size];
        for (int s = 0; s < count; s++)
        {
            float cx = (float)rng.NextDouble() * size;
            float cy = (float)rng.NextDouble() * size;
            float radius = 0.8f + (float)rng.NextDouble() * 1.8f;
            float value = 0.6f + (float)rng.NextDouble() * 0.4f;
            int reach = Mathf.CeilToInt(radius + 1f);
            for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d > radius + 1f)
                    continue;
                float a = Mathf.Clamp01(radius + 0.5f - d) * value;
                int idx = Mod(Mathf.FloorToInt(cy) + dy, size) * size + Mod(Mathf.FloorToInt(cx) + dx, size);
                v[idx] = Mathf.Max(v[idx], a);
            }
        }
        return v;
    }

    // Tileable value-noise octaves (lattice periods divide the texture size).
    private static float[] Fbm(int size, int[] periods, float[] weights, System.Random rng)
    {
        var v = new float[size * size];
        for (int o = 0; o < periods.Length; o++)
        {
            int n = periods[o];
            var lattice = new float[n * n];
            for (int i = 0; i < lattice.Length; i++)
                lattice[i] = (float)rng.NextDouble();
            float cell = (float)size / n;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = x / cell, fy = y / cell;
                int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
                float tx = Smooth(fx - x0), ty = Smooth(fy - y0);
                float a = lattice[Mod(y0, n) * n + Mod(x0, n)];
                float b = lattice[Mod(y0, n) * n + Mod(x0 + 1, n)];
                float c = lattice[Mod(y0 + 1, n) * n + Mod(x0, n)];
                float d = lattice[Mod(y0 + 1, n) * n + Mod(x0 + 1, n)];
                v[y * size + x] += Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty) * weights[o];
            }
        }
        return v;
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);
    private static int Mod(int a, int n) => ((a % n) + n) % n;

}
