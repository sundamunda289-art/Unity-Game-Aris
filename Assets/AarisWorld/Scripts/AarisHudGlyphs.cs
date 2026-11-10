using UnityEngine;
using UnityEngine.UI;

namespace Aaris.MapReview
{
    /// <summary>
    /// Original offline, white silhouette glyphs in the style of a mobile
    /// battle HUD. One-time CPU rasterization; no PNG downloads or network.
    /// Made of antialiased vector strokes in normalized 0..1 coordinates.
    /// </summary>
    internal static class AarisHudGlyphs
    {
        internal enum Glyph { Fire, Jump, Crouch, Prone, Sprint }
        private const int Size = 128;
        private static readonly Sprite[] Sprites = new Sprite[5];
        private static Sprite ring, knob;

        // Resources holds the Shader asset in the Android player build. Shader.Find
        // by name alone can return null after shader variant stripping on mobile.
        // One shared material is assigned once per HUD canvas, never per frame.
        private static Material overlayMaterial;
        private static bool overlayChecked;

        internal static void ApplyCanvasShader(Transform canvasRoot)
        {
            if (canvasRoot == null) return;
            if (!overlayChecked)
            {
                overlayChecked = true;
                Shader shader = Resources.Load<Shader>("AarisHUDOverlay");
                if (shader != null && shader.isSupported)
                {
                    overlayMaterial = new Material(shader)
                    {
                        name = "Aaris Android Overlay (shared)",
                        hideFlags = HideFlags.DontSave
                    };
                }
                else
                {
                    Debug.LogError("Aaris UI: build-retained AarisHUDOverlay shader unavailable or unsupported.");
                }
            }
            if (overlayMaterial == null) return;
            foreach (Graphic graphic in canvasRoot.GetComponentsInChildren<Graphic>(true))
                if (graphic != null) graphic.material = overlayMaterial;
        }

        internal static Sprite Ring => ring != null ? ring : (ring = BuildDisc(true));
        internal static Sprite Knob => knob != null ? knob : (knob = BuildDisc(false));

        internal static Sprite Get(Glyph glyph)
        {
            int index = (int)glyph;
            if (Sprites[index] == null) Sprites[index] = BuildGlyph(glyph);
            return Sprites[index];
        }

        private static float Circle(Vector2 p, float x, float y, float radius)
        {
            float distance = (p - new Vector2(x, y)).magnitude - radius;
            return Mathf.Clamp01(0.5f - distance / 0.016f);
        }

        private static float Line(Vector2 p, float ax, float ay, float bx, float by, float width)
        {
            Vector2 a = new Vector2(ax, ay);
            Vector2 b = new Vector2(bx, by);
            Vector2 ba = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ba) / Mathf.Max(0.0001f, ba.sqrMagnitude));
            return Mathf.Clamp01(0.5f - ((p - (a + ba * t)).magnitude - width * 0.5f) / 0.016f);
        }

        private static float Human(Glyph id, Vector2 p)
        {
            float a = 0f;
            if (id == Glyph.Sprint)
            {
                a = Mathf.Max(a, Circle(p, .64f, .80f, .081f));
                a = Mathf.Max(a, Line(p, .59f,.71f, .49f,.52f, .104f));
                a = Mathf.Max(a, Line(p, .52f,.64f, .35f,.56f, .082f));
                a = Mathf.Max(a, Line(p, .35f,.56f, .26f,.68f, .074f));
                a = Mathf.Max(a, Line(p, .55f,.65f, .70f,.53f, .071f));
                a = Mathf.Max(a, Line(p, .49f,.52f, .31f,.38f, .09f));
                a = Mathf.Max(a, Line(p, .31f,.38f, .18f,.23f, .078f));
                a = Mathf.Max(a, Line(p, .49f,.51f, .64f,.31f, .094f));
                a = Mathf.Max(a, Line(p, .64f,.31f, .82f,.25f, .079f));
            }
            else if (id == Glyph.Jump)
            {
                a = Mathf.Max(a, Circle(p, .55f,.78f,.08f));
                a = Mathf.Max(a, Line(p,.55f,.67f,.50f,.45f,.098f));
                a = Mathf.Max(a, Line(p,.54f,.63f,.32f,.72f,.071f));
                a = Mathf.Max(a, Line(p,.32f,.72f,.24f,.87f,.071f));
                a = Mathf.Max(a, Line(p,.55f,.62f,.72f,.79f,.073f));
                a = Mathf.Max(a, Line(p,.72f,.79f,.73f,.89f,.068f));
                a = Mathf.Max(a, Line(p,.50f,.45f,.35f,.32f,.091f));
                a = Mathf.Max(a, Line(p,.35f,.32f,.24f,.25f,.083f));
                a = Mathf.Max(a, Line(p,.50f,.45f,.67f,.33f,.097f));
                a = Mathf.Max(a, Line(p,.67f,.33f,.78f,.44f,.079f));
            }
            else if (id == Glyph.Crouch)
            {
                a = Mathf.Max(a, Circle(p,.69f,.73f,.085f));
                a = Mathf.Max(a, Line(p,.65f,.64f,.49f,.48f,.11f));
                a = Mathf.Max(a, Line(p,.55f,.57f,.71f,.43f,.076f));
                a = Mathf.Max(a, Line(p,.71f,.43f,.82f,.43f,.07f));
                a = Mathf.Max(a, Line(p,.50f,.48f,.35f,.30f,.108f));
                a = Mathf.Max(a, Line(p,.35f,.30f,.57f,.29f,.095f));
                a = Mathf.Max(a, Line(p,.51f,.47f,.67f,.33f,.084f));
                a = Mathf.Max(a, Line(p,.67f,.33f,.79f,.24f,.081f));
                a = Mathf.Max(a, Line(p,.22f,.22f,.83f,.22f,.019f));
            }
            else if (id == Glyph.Prone)
            {
                a = Mathf.Max(a, Circle(p,.83f,.54f,.07f));
                a = Mathf.Max(a, Line(p,.74f,.48f,.42f,.44f,.15f));
                a = Mathf.Max(a, Line(p,.42f,.43f,.19f,.40f,.105f));
                a = Mathf.Max(a, Line(p,.21f,.39f,.12f,.34f,.067f));
                a = Mathf.Max(a, Line(p,.73f,.46f,.66f,.30f,.08f));
                a = Mathf.Max(a, Line(p,.66f,.30f,.79f,.26f,.075f));
                a = Mathf.Max(a, Line(p,.51f,.42f,.34f,.27f,.072f));
                a = Mathf.Max(a, Line(p,.34f,.27f,.16f,.28f,.07f));
                a = Mathf.Max(a, Line(p,.10f,.23f,.91f,.23f,.018f));
            }
            return a;
        }

        private static float Fire(Vector2 p)
        {
            // Angled cartridge with speed lines, clearly distinct from the
            // limb/stance glyphs. Fire becomes unarmed melee via game state.
            float a = 0f;
            a = Mathf.Max(a, Line(p,.32f,.31f,.72f,.71f,.125f));
            a = Mathf.Max(a, Circle(p,.73f,.72f,.077f));
            a = Mathf.Max(a, Line(p,.24f,.22f,.39f,.37f,.09f));
            a = Mathf.Max(a, Line(p,.19f,.53f,.37f,.53f,.035f));
            a = Mathf.Max(a, Line(p,.26f,.68f,.40f,.58f,.035f));
            a = Mathf.Max(a, Line(p,.48f,.24f,.54f,.39f,.035f));
            return a;
        }

        private static Sprite BuildGlyph(Glyph glyph)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            texture.name = "AarisHUD_" + glyph;
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var p = new Vector2((x + 0.5f) / Size, (y + 0.5f) / Size);
                    float alpha = glyph == Glyph.Fire ? Fire(p) : Human(glyph, p);
                    pixels[y * Size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            return Sprite.Create(texture, new Rect(0f, 0f, Size, Size), Vector2.one * 0.5f, 100f);
        }

        private static Sprite BuildDisc(bool outlined)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            texture.name = outlined ? "AarisHUD_OutlinedDisc" : "AarisHUD_Knob";
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = (x + 0.5f) / Size - 0.5f;
                    float dy = (y + 0.5f) / Size - 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float edge = Mathf.Clamp01((0.496f - d) * Size / 1.5f);
                    float rim = outlined ? Mathf.SmoothStep(0f, 1f, (d - 0.452f) * 32f) : 0f;
                    float highlight = Mathf.Clamp01(0.5f + dy) * 0.15f;
                    Color fill = outlined
                        ? Color.Lerp(new Color(0.07f, 0.12f, 0.17f, 0.65f),
                            new Color(0.63f, 0.79f, 0.89f, 0.85f), rim * (0.82f + highlight))
                        : new Color(0.78f, 0.9f, 0.95f, 0.92f);
                    fill.a *= edge;
                    pixels[y * Size + x] = fill;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            return Sprite.Create(texture, new Rect(0f, 0f, Size, Size), Vector2.one * 0.5f, 100f);
        }
    }
}
