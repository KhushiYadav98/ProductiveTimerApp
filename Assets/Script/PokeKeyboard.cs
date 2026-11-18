using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// In-world keyboard typed by poking with the hands. Lives on its own world-space root canvas (with its own
// ISDK poke surface), because the Interaction SDK only raycasts root canvases on their own plane — a tilted
// keyboard nested in another canvas couldn't be poked. It follows an anchor (e.g. under the Name Task panel),
// tilted back like a laptop keyboard. The trapezoid base and glossy keys are drawn procedurally at startup.
[RequireComponent(typeof(Canvas))]
public class PokeKeyboard : MonoBehaviour
{
    [SerializeField] private TMP_InputField target;

    [Header("Placement")]
    [Tooltip("The keyboard's top edge follows this transform's position, rotation and scale.")]
    [SerializeField] private Transform anchor;
    [Tooltip("Tilt around the top edge, in degrees. Positive brings the bottom rows toward the user.")]
    [SerializeField] private float tiltAngle = 45f;

    [Header("Layout (canvas units)")]
    [SerializeField] private float keySize = 36f;
    [SerializeField] private float gap = 6f;
    [SerializeField] private float padding = 16f;
    [Tooltip("How far each slanted side of the base reaches out at the bottom.")]
    [SerializeField] private float slant = 56f;
    [SerializeField] private float baseCornerRadius = 14f;
    [SerializeField] private float keyCornerRadius = 10f;

    [Header("Colors")]
    [SerializeField] private Color baseColor = new Color32(0x54, 0xB3, 0xF5, 0xFF);
    [SerializeField] private Color baseEdgeColor = new Color32(0x3A, 0x8F, 0xD6, 0xFF);
    [SerializeField] private Color keyLight = new Color32(0x7A, 0xCB, 0xFA, 0xFF);
    [SerializeField] private Color keyDark = new Color32(0x3F, 0x9D, 0xEC, 0xFF);
    [SerializeField] private Color keyShadow = new Color32(0x2B, 0x7F, 0xC9, 0xFF);
    [SerializeField] private Color spaceLeft = new Color32(0x6F, 0xC3, 0xF7, 0xFF);
    [SerializeField] private Color spaceMid = new Color32(0x45, 0x9E, 0xF2, 0xFF);
    [SerializeField] private Color spaceRight = new Color32(0xB0, 0xC4, 0xFF, 0xFF);
    [SerializeField] private Color accentLight = new Color32(0x3C, 0x8E, 0xF5, 0xFF);
    [SerializeField] private Color accentDark = new Color32(0x1E, 0x66, 0xD0, 0xFF);
    [SerializeField] private Color labelColor = Color.white;

    [Header("Text")]
    [SerializeField] private TMP_FontAsset font;
    [SerializeField] private float fontSize = 17f;

    [Tooltip("Called when the Done key is pressed (the keyboard hides itself).")]
    public UnityEvent onDone;

    private enum Look { Key, Space, Accent }

    private struct KeySpec
    {
        public string Label;
        public char Char;          // typed character; '\0' for action keys
        public UnityAction Action;
        public float Width;        // fixed width; <= 0 means it shares the row's leftover width
        public Look Look;
    }

    private const float PixelsPerUnit = 3f;
    private const float KeyShadowPad = 6f;

    private readonly List<(TMP_Text label, char c)> letterKeys = new List<(TMP_Text, char)>();
    private readonly Dictionary<(Look, int), Sprite> keySprites = new Dictionary<(Look, int), Sprite>();
    private Image shiftImage;
    private bool shift;
    private bool built;
    private float topWidth, bottomWidth, height;

    public bool IsShown => gameObject.activeSelf;

    private void Awake()
    {
        Build();
    }

    private void LateUpdate()
    {
        Follow();
    }

    public void Show(TMP_InputField field)
    {
        if (field != null)
            target = field;
        Build();
        Follow();
        gameObject.SetActive(true);
    }

    public void Hide()
    {
        gameObject.SetActive(false);
    }

    private void Follow()
    {
        if (anchor == null)
            return;
        // The canvas pivot is its top-center, so the tilt hinges on the top edge.
        transform.SetPositionAndRotation(anchor.position, anchor.rotation * Quaternion.Euler(tiltAngle, 0f, 0f));
        transform.localScale = anchor.lossyScale;
    }

    // ---------- Typing ----------

    private void Type(char c)
    {
        if (target == null)
            return;
        if (target.characterLimit > 0 && target.text.Length >= target.characterLimit)
            return;

        target.text += shift ? char.ToUpperInvariant(c) : c;
        if (shift)
            SetShift(false);
    }

    private void Backspace()
    {
        if (target != null && target.text.Length > 0)
            target.text = target.text.Substring(0, target.text.Length - 1);
    }

    private void Done()
    {
        Hide();
        onDone?.Invoke();
    }

    private void SetShift(bool on)
    {
        shift = on;
        foreach (var (label, c) in letterKeys)
            label.text = (on ? char.ToUpperInvariant(c) : c).ToString();
        if (shiftImage != null)
            shiftImage.color = on ? new Color(0.7f, 0.82f, 1f) : Color.white;
    }

    // ---------- Layout ----------

    private void Build()
    {
        if (built)
            return;
        built = true;

        var rows = new List<List<KeySpec>>
        {
            Letters("1234567890"),
            Letters("qwertyuiop"),
            Letters("asdfghjkl"),
        };
        var row3 = new List<KeySpec> { Action("Shift", () => SetShift(!shift), -1f, Look.Key) };
        row3.AddRange(Letters("zxcvbnm"));
        row3.Add(Action("Del", Backspace, -1f, Look.Key));
        rows.Add(row3);
        rows.Add(new List<KeySpec>
        {
            Letter(','), Letter('-'),
            new KeySpec { Label = "", Char = ' ', Width = -1f, Look = Look.Space },
            Letter('.'),
            Action("Done", Done, keySize * 2.5f + gap * 1.5f, Look.Accent),
        });

        // Base: a trapezoid whose top fits the widest top row and whose sides slant outward.
        float topRow = 10 * keySize + 9 * gap;
        topWidth = topRow + 2 * padding;
        bottomWidth = topWidth + 2 * slant;
        height = rows.Count * keySize + (rows.Count - 1) * gap + 2 * padding + 2f;

        var rt = (RectTransform)transform;
        rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(bottomWidth, height);

        BuildBase();

        for (int r = 0; r < rows.Count; r++)
            BuildRow(rows[r], r);
    }

    private List<KeySpec> Letters(string chars)
    {
        var list = new List<KeySpec>();
        foreach (char c in chars)
            list.Add(Letter(c));
        return list;
    }

    private KeySpec Letter(char c) => new KeySpec { Label = c.ToString(), Char = c, Width = keySize, Look = Look.Key };

    private static KeySpec Action(string label, UnityAction action, float width, Look look) =>
        new KeySpec { Label = label, Action = action, Width = width, Look = look };

    private void BuildRow(List<KeySpec> row, int r)
    {
        float top = padding + r * (keySize + gap);
        // Width available inside the slanted sides, measured at the row's top (the narrower end).
        float available = Mathf.Lerp(topWidth, bottomWidth, top / height) - 2 * padding;

        float fixedWidth = gap * (row.Count - 1);
        int flexCount = 0;
        foreach (var k in row)
        {
            if (k.Width > 0f) fixedWidth += k.Width;
            else flexCount++;
        }
        float flexWidth = flexCount > 0 ? Mathf.Max(keySize, (available - fixedWidth) / flexCount) : 0f;
        float rowWidth = fixedWidth + flexWidth * flexCount;

        float x = -rowWidth * 0.5f;
        foreach (var k in row)
        {
            float w = k.Width > 0f ? k.Width : flexWidth;
            var image = AddKey(k, new Vector2(x + w * 0.5f, -(top + keySize * 0.5f)), w);
            x += w + gap;

            if (k.Char != '\0' && char.IsLetter(k.Char))
                letterKeys.Add((image.GetComponentInChildren<TMP_Text>(), k.Char));
            if (k.Label == "Shift")
                shiftImage = image;
        }
    }

    private Image AddKey(KeySpec spec, Vector2 center, float width)
    {
        var key = new GameObject(spec.Label.Length > 0 ? $"Key_{spec.Label}" : "Key_Space",
            typeof(RectTransform), typeof(Image), typeof(Button));
        key.layer = gameObject.layer;
        var krt = (RectTransform)key.transform;
        krt.SetParent(transform, false);
        krt.anchorMin = krt.anchorMax = new Vector2(0.5f, 1f);
        // The sprite includes room for the drop shadow around the key.
        krt.sizeDelta = new Vector2(width + 2 * KeyShadowPad, keySize + 2 * KeyShadowPad);
        krt.anchoredPosition = center;

        var image = key.GetComponent<Image>();
        image.sprite = KeySprite(spec.Look, width);

        var button = key.GetComponent<Button>();
        var colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.93f, 0.97f, 1f);
        colors.selectedColor = Color.white;
        colors.pressedColor = new Color(0.68f, 0.8f, 0.95f);
        colors.fadeDuration = 0.05f;
        button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        char c = spec.Char;
        button.onClick.AddListener(spec.Action ?? (() => Type(c)));

        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelGo.layer = gameObject.layer;
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(krt, false);
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = lrt.offsetMax = new Vector2(0f, 0f);
        lrt.anchoredPosition = new Vector2(0f, 1f);
        var label = labelGo.GetComponent<TextMeshProUGUI>();
        if (font != null)
            label.font = font;
        label.text = spec.Label;
        label.fontSize = fontSize;
        label.fontStyle = FontStyles.Bold;
        label.color = labelColor;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;

        return image;
    }

    private void BuildBase()
    {
        const float margin = 10f; // room for the soft shadow
        const float thickness = 5f;
        float w = bottomWidth + 2 * margin;
        float h = height + thickness + 2 * margin;

        // Shape in units: y up, top edge at y = 0, centered on x = 0.
        var outline = new[]
        {
            new Vector2(-topWidth * 0.5f, 0f), new Vector2(-bottomWidth * 0.5f, -height),
            new Vector2(bottomWidth * 0.5f, -height), new Vector2(topWidth * 0.5f, 0f)
        };
        var core = Inset(outline, baseCornerRadius);
        float r = baseCornerRadius;

        var tex = Bake(w, h, new Vector2(-w * 0.5f, -height - thickness - margin), p =>
        {
            float body = SdPolygon(core, p) - r;
            float edge = SdPolygon(core, p + new Vector2(0f, thickness)) - r;
            float shadow = SdPolygon(core, p + new Vector2(0f, thickness + 3f)) - r;

            var c = new Color(0f, 0f, 0f, 0.22f * SoftFill(shadow, 6f));
            c = Over(new Color(baseEdgeColor.r, baseEdgeColor.g, baseEdgeColor.b, Fill(edge)), c);
            // Gentle top-to-bottom lightening of the body.
            var top = Color.Lerp(baseColor, Color.white, 0.06f);
            var body01 = Color.Lerp(top, baseColor, Mathf.Clamp01(-p.y / height));
            c = Over(new Color(body01.r, body01.g, body01.b, Fill(body)), c);
            return c;
        });

        var go = new GameObject("Base", typeof(RectTransform), typeof(Image));
        go.layer = gameObject.layer;
        var brt = (RectTransform)go.transform;
        brt.SetParent(transform, false);
        brt.SetAsFirstSibling();
        brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 1f);
        brt.pivot = new Vector2(0.5f, 1f);
        brt.sizeDelta = new Vector2(w, h);
        brt.anchoredPosition = new Vector2(0f, margin);
        var image = go.GetComponent<Image>();
        image.sprite = MakeSprite(tex);
        image.raycastTarget = false;
    }

    // ---------- Procedural sprites ----------

    private Sprite KeySprite(Look look, float width)
    {
        var id = (look, Mathf.RoundToInt(width));
        if (keySprites.TryGetValue(id, out var cached))
            return cached;

        float w = width + 2 * KeyShadowPad;
        float h = keySize + 2 * KeyShadowPad;
        var half = new Vector2(width, keySize) * 0.5f;
        float r = Mathf.Min(keyCornerRadius, keySize * 0.5f);
        var shadowOffset = new Vector2(1.5f, -2.5f);

        var tex = Bake(w, h, new Vector2(-w * 0.5f, -h * 0.5f), p =>
        {
            float d = SdRoundBox(p, half, r);
            float ds = SdRoundBox(p - shadowOffset, half, r);

            var c = new Color(keyShadow.r, keyShadow.g, keyShadow.b, 0.55f * SoftFill(ds, 3f));

            // u: 0 left .. 1 right, v: 0 bottom .. 1 top
            float u = (p.x + half.x) / (2 * half.x);
            float v = (p.y + half.y) / (2 * half.y);
            Color body;
            switch (look)
            {
                case Look.Space:
                    body = u < 0.45f
                        ? Color.Lerp(spaceLeft, spaceMid, u / 0.45f)
                        : Color.Lerp(spaceMid, spaceRight, (u - 0.45f) / 0.55f);
                    body = Color.Lerp(body, Color.white, 0.12f * v);
                    break;
                case Look.Accent:
                    body = Color.Lerp(accentDark, accentLight, 0.5f * (v + 1f - u));
                    break;
                default:
                    // Diagonal gloss: light at the top-left, deeper at the bottom-right.
                    body = Color.Lerp(keyDark, keyLight, Mathf.Clamp01(0.5f * (1f - u + v)));
                    break;
            }

            // Bevel: a bright rim on the top-left inside edge, a darker one on the bottom-right.
            float rim = Mathf.Clamp01(1f - Mathf.Abs(d + 1.2f) / 1.4f);
            float lightSide = Mathf.Clamp01(0.5f + (v - u) * 1.2f);
            body = Color.Lerp(body, Color.white, rim * 0.55f * lightSide);
            body = Color.Lerp(body, keyShadow, rim * 0.35f * (1f - lightSide));

            return Over(new Color(body.r, body.g, body.b, Fill(d)), c);
        });

        var sprite = MakeSprite(tex);
        keySprites[id] = sprite;
        return sprite;
    }

    // Renders shade(p) for p in units over a w x h area starting at origin (bottom-left).
    private static Texture2D Bake(float w, float h, Vector2 origin, Func<Vector2, Color> shade)
    {
        int pw = Mathf.CeilToInt(w * PixelsPerUnit);
        int ph = Mathf.CeilToInt(h * PixelsPerUnit);
        var pixels = new Color[pw * ph];
        for (int y = 0; y < ph; y++)
        for (int x = 0; x < pw; x++)
        {
            var p = origin + new Vector2((x + 0.5f) / PixelsPerUnit, (y + 0.5f) / PixelsPerUnit);
            pixels[y * pw + x] = shade(p);
        }

        var tex = new Texture2D(pw, ph, TextureFormat.RGBA32, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4
        };
        tex.SetPixels(pixels);
        tex.Apply(true, true);
        return tex;
    }

    private static Sprite MakeSprite(Texture2D tex) =>
        Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect);

    // Antialiased coverage of a signed distance (units), about one pixel wide.
    private static float Fill(float d) => Mathf.Clamp01(0.5f - d * PixelsPerUnit);

    private static float SoftFill(float d, float blur) => Mathf.Clamp01(0.5f - d / blur);

    // Straight-alpha "a over b".
    private static Color Over(Color a, Color b)
    {
        float outA = a.a + b.a * (1f - a.a);
        if (outA <= 0f)
            return new Color(0f, 0f, 0f, 0f);
        var rgb = ((Vector4)a * a.a + (Vector4)b * b.a * (1f - a.a)) / outA;
        return new Color(rgb.x, rgb.y, rgb.z, outA);
    }

    private static float SdRoundBox(Vector2 p, Vector2 half, float r)
    {
        var q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - half + new Vector2(r, r);
        return new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - r;
    }

    // Signed distance to a polygon (negative inside).
    private static float SdPolygon(Vector2[] v, Vector2 p)
    {
        float d = Vector2.Dot(p - v[0], p - v[0]);
        float s = 1f;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i, i++)
        {
            Vector2 e = v[j] - v[i];
            Vector2 w = p - v[i];
            Vector2 b = w - e * Mathf.Clamp01(Vector2.Dot(w, e) / Vector2.Dot(e, e));
            d = Mathf.Min(d, Vector2.Dot(b, b));
            bool c1 = p.y >= v[i].y, c2 = p.y < v[j].y, c3 = e.x * w.y > e.y * w.x;
            if ((c1 && c2 && c3) || (!c1 && !c2 && !c3))
                s = -s;
        }
        return s * Mathf.Sqrt(d);
    }

    // Moves every edge of a convex polygon inward by `amount` (so SdPolygon(inset) - amount is a rounded original).
    private static Vector2[] Inset(Vector2[] poly, float amount)
    {
        int n = poly.Length;
        var center = Vector2.zero;
        foreach (var p in poly) center += p;
        center /= n;

        var points = new Vector2[n];
        var dirs = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % n];
            Vector2 dir = (b - a).normalized;
            Vector2 normal = new Vector2(-dir.y, dir.x);
            if (Vector2.Dot(normal, center - a) < 0f)
                normal = -normal;
            points[i] = a + normal * amount;
            dirs[i] = dir;
        }

        var result = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            int prev = (i + n - 1) % n;
            // Intersect edge prev (through points[prev] along dirs[prev]) with edge i.
            Vector2 p = points[prev], r = dirs[prev], q = points[i], s = dirs[i];
            float cross = r.x * s.y - r.y * s.x;
            float t = ((q.x - p.x) * s.y - (q.y - p.y) * s.x) / cross;
            result[i] = p + r * t;
        }
        return result;
    }
}
