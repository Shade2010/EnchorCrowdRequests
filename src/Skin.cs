using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EnchorCrowdRequests
{
    // Runtime-generated sprites so the uGUI overlay can have rounded corners, vertical gradients,
    // and soft drop shadows without shipping any image assets. All sprites are 9-sliced and cached.
    internal static class Skin
    {
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        // Flat white rounded-rect (all four corners), 9-sliced. Tint via Image.color.
        public static Sprite Rounded(int radius)
        {
            return Get("r" + radius, () =>
            {
                int b = radius + 1;
                int size = b * 2 + 2;
                var px = new Il2CppStructArray<Color32>(size * size);
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        px[y * size + x] = new Color32(255, 255, 255, A(CornerAlpha(x, y, size, size, radius, true, true)));
                return MakeSprite(px, size, size, new Vector4(b, b, b, b));
            });
        }

        // Vertical gradient + rounded, sliced horizontally only (border on left/right). Bake at the
        // element's true height so the gradient and corners are never stretched vertically.
        public static Sprite VGradient(int height, int radius, Color top, Color bottom, bool roundTop, bool roundBottom)
        {
            string key = "g" + height + "_" + radius + "_" + Ck(top) + Ck(bottom) + (roundTop ? 1 : 0) + (roundBottom ? 1 : 0);
            return Get(key, () =>
            {
                int w = radius * 2 + 4;
                var px = new Il2CppStructArray<Color32>(w * height);
                for (int y = 0; y < height; y++)
                {
                    float t = height <= 1 ? 1f : (float)y / (height - 1);   // 0 bottom .. 1 top
                    Color c = Color.Lerp(bottom, top, t);
                    for (int x = 0; x < w; x++)
                    {
                        float a = CornerAlpha(x, y, w, height, radius, roundTop, roundBottom);
                        px[y * w + x] = new Color32(A(c.r), A(c.g), A(c.b), A(c.a * a));
                    }
                }
                return MakeSprite(px, w, height, new Vector4(radius, 0, radius, 0));
            });
        }

        // Bake a banner image into a sprite with smooth rounded TOP corners (no Mask, so the corners
        // are anti-aliased and match the panel). Crops the banner vertically to the target aspect so it
        // isn't squished. Render at w x h (use 2x the on-screen size for sharpness; radius scales too).
        public static Sprite BannerTop(Texture2D banner, int w, int h, int radius)
        {
            try
            {
                int bw = banner.width, bh = banner.height;
                var src = banner.GetPixels32();
                float barAspect = (float)w / h, texAspect = (float)bw / bh;
                float vH = texAspect < barAspect ? texAspect / barAspect : 1f;   // sample a center band
                float v0 = (1f - vH) * 0.5f;
                var px = new Il2CppStructArray<Color32>(w * h);
                for (int y = 0; y < h; y++)
                {
                    float vv = v0 + ((y + 0.5f) / h) * vH;
                    int sy = Mathf.Clamp((int)(vv * bh), 0, bh - 1);
                    for (int x = 0; x < w; x++)
                    {
                        int sx = Mathf.Clamp((int)(((x + 0.5f) / w) * bw), 0, bw - 1);
                        Color32 c = src[sy * bw + sx];
                        float a = CornerAlpha(x, y, w, h, radius, true, false);
                        px[y * w + x] = new Color32(c.r, c.g, c.b, A((c.a / 255f) * a));
                    }
                }
                return MakeSprite(px, w, h, Vector4.zero);   // Simple sprite, no 9-slice
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("Skin.BannerTop failed: " + ex.Message); return null; }
        }

        // Soft drop shadow: rounded, alpha fades to 0 over 'blur' px at the edge. White; tint black.
        public static Sprite Shadow(int radius, int blur)
        {
            return Get("s" + radius + "_" + blur, () =>
            {
                int r = radius + blur;
                int size = r * 2 + 2;
                var px = new Il2CppStructArray<Color32>(size * size);
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float d = EdgeDistance(x, y, size, size, r);
                        float a = Mathf.Clamp01(d / blur); a *= a;
                        px[y * size + x] = new Color32(255, 255, 255, A(a));
                    }
                return MakeSprite(px, size, size, new Vector4(r, r, r, r));
            });
        }

        // ---- internals ----------------------------------------------------------

        private static Sprite Get(string key, Func<Sprite> make)
        {
            try
            {
                if (_cache.TryGetValue(key, out var s) && s != null) return s;
                s = make();
                _cache[key] = s;
                return s;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("Skin '" + key + "' failed: " + ex.Message); return null; }
        }

        private static Sprite MakeSprite(Il2CppStructArray<Color32> px, int w, int h, Vector4 border)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels32(px);
            tex.Apply(false);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        }

        private static byte A(float v) { return (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255); }

        // Alpha that rounds only the selected corners; 1 in straight regions (y up: 0 = bottom).
        private static float CornerAlpha(int x, int y, int w, int h, int r, bool roundTop, bool roundBottom)
        {
            if (r <= 0) return 1f;
            float fx = x + 0.5f, fy = y + 0.5f;
            bool left = fx < r, right = fx > w - r;
            bool bottom = fy < r, top = fy > h - r;
            if ((bottom && !roundBottom) || (top && !roundTop)) return 1f;
            if ((left || right) && (bottom || top))
            {
                float cx = left ? r : w - r;
                float cy = bottom ? r : h - r;
                float d = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
                return Mathf.Clamp01(r - d + 0.5f);
            }
            return 1f;
        }

        // Distance from the rounded-rect edge inward (0 at edge), for the shadow falloff.
        private static float EdgeDistance(int x, int y, int w, int h, int r)
        {
            float fx = x + 0.5f, fy = y + 0.5f;
            float cx = Mathf.Clamp(fx, r, w - r);
            float cy = Mathf.Clamp(fy, r, h - r);
            float dCorner = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
            float dEdge = Mathf.Min(Mathf.Min(fx, w - fx), Mathf.Min(fy, h - fy));
            return Mathf.Min(dEdge, r - dCorner);
        }

        private static string Ck(Color c) { return A(c.r).ToString("x2") + A(c.g).ToString("x2") + A(c.b).ToString("x2"); }
    }
}
