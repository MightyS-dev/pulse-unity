#if PULSE_UGUI
using System;
using UnityEngine;
using UnityEngine.UI;

namespace Mightys.Pulse
{
    // The uGUI objects of the built-in App Control screen, built from code (no assets, no TextMeshPro). One root object with a
    // screen space overlay canvas on the highest sorting order. The layout is computed by hand in canvas units so that the longest
    // language fits: text sizes step down until everything fits, and only then does the body scroll.
    internal sealed class ControlOverlayView
    {
        private const float Pad = 48f, Gap = 24f, ButtonGap = 24f;
        private static Font font, osFont;
        private static Sprite roundedSprite;
        private static int spriteUsers;

        internal readonly GameObject Root;
        internal Action OnPrimary, OnSecondary;

        private readonly RectTransform safe, card, strip, banner, viewport, content, primaryRect, secondaryRect;
        private readonly Image dim, cardImage, stripImage, bannerImage, primaryImage, secondaryImage;
        private readonly Text title, body, stripText, bannerText, primaryLabel, secondaryLabel;
        private readonly Button primary, secondary;
        private readonly ScrollRect scroll;
        private ControlPlan plan = ControlOverlayRules.Nothing;
        private ControlTheme theme = ControlTheme.Dark;

        internal ControlOverlayView()
        {
            Root = new GameObject("MightyS Pulse Control", typeof(RectTransform)) { hideFlags = HideFlags.HideInHierarchy, layer = 5 };
            UnityEngine.Object.DontDestroyOnLoad(Root);
            var canvas = Root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            var scaler = Root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(ControlOverlayRules.ReferenceWidth, ControlOverlayRules.ReferenceHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = ControlOverlayRules.Match;
            Root.AddComponent<GraphicRaycaster>();
            // The screen is SDK UI: a tap that lands on it is never a heatmap point.
            Root.AddComponent<PulseHeatmapIgnore>();
            if (roundedSprite == null) roundedSprite = MakeRounded();
            spriteUsers++;

            dim = MakeImage("Dim", Root.transform, null); Stretch(dim.rectTransform);
            safe = MakeRect("Safe", Root.transform); Stretch(safe);
            card = MakeRect("Card", safe); cardImage = card.gameObject.AddComponent<Image>(); Round(cardImage); cardImage.raycastTarget = true;
            title = MakeText("Title", card, FontStyle.Bold, TextAnchor.UpperCenter);
            viewport = MakeRect("Body", card); viewport.gameObject.AddComponent<RectMask2D>();
            content = MakeRect("Content", viewport);
            body = MakeText("Text", content, FontStyle.Normal, TextAnchor.UpperCenter); Stretch(body.rectTransform);
            scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 40f;
            primary = MakeButton("Primary", card, out primaryImage, out primaryLabel, out primaryRect);
            secondary = MakeButton("Secondary", card, out secondaryImage, out secondaryLabel, out secondaryRect);
            primary.onClick.AddListener(() => { try { OnPrimary?.Invoke(); } catch (Exception) { } });
            secondary.onClick.AddListener(() => { try { OnSecondary?.Invoke(); } catch (Exception) { } });
            strip = MakeRect("TestStrip", safe); stripImage = strip.gameObject.AddComponent<Image>(); stripImage.raycastTarget = false;
            stripText = MakeText("Text", strip, FontStyle.Bold, TextAnchor.MiddleCenter); Stretch(stripText.rectTransform);
            banner = MakeRect("Banner", safe); bannerImage = banner.gameObject.AddComponent<Image>(); Round(bannerImage); bannerImage.raycastTarget = false;
            bannerText = MakeText("Text", banner, FontStyle.Normal, TextAnchor.MiddleCenter); Stretch(bannerText.rectTransform);
        }

        // Whether a screen pixel (origin bottom left) is on the banner or the test strip, the parts that do not take touches themselves.
        internal bool Covers(double pixelX, double pixelY)
        {
            try
            {
                var point = new Vector2((float)pixelX, (float)pixelY);
                return Over(banner, point) || Over(strip, point);
            }
            catch (Exception) { return false; }
        }

        private static bool Over(RectTransform rect, Vector2 point) =>
            rect != null && rect.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(rect, point, null);

        internal void Destroy()
        {
            try { if (Root != null) UnityEngine.Object.Destroy(Root); } catch (Exception) { }
            if (--spriteUsers <= 0 && roundedSprite != null)
            {
                try { UnityEngine.Object.Destroy(roundedSprite.texture); UnityEngine.Object.Destroy(roundedSprite); } catch (Exception) { }
                roundedSprite = null; spriteUsers = 0;
            }
        }

        internal void SetPrimaryEnabled(bool enabled) { primary.interactable = enabled; }

        internal void Show(ControlPlan next, ControlTheme colours)
        {
            plan = next; theme = colours;
            Layout();
        }

        internal void Relayout() { Layout(); }

        // ---- layout ----

        private void Layout()
        {
            var p = plan;
            var screenW = Mathf.Max(1, Screen.width); var screenH = Mathf.Max(1, Screen.height);
            var scale = ControlOverlayRules.CanvasScale(screenW, screenH);
            var canvasW = screenW / scale; var canvasH = screenH / scale;
            var area = Screen.safeArea;
            float left = Mathf.Max(0, area.xMin) / scale, right = Mathf.Max(0, screenW - area.xMax) / scale;
            float bottom = Mathf.Max(0, area.yMin) / scale, top = Mathf.Max(0, screenH - area.yMax) / scale;
            safe.offsetMin = new Vector2(left, bottom); safe.offsetMax = new Vector2(-right, -top);
            var freeW = canvasW - left - right; var freeH = canvasH - top - bottom;

            var tint = theme;
            dim.gameObject.SetActive(p.Modal);
            dim.color = new Color(0, 0, 0, p.Surface == ControlSurface.Lock ? ControlTheme.DimAlpha : ControlTheme.DialogDimAlpha);
            dim.raycastTarget = p.Modal;
            card.gameObject.SetActive(p.Modal);

            var used = 0f;
            strip.gameObject.SetActive(p.HasTestStrip);
            if (p.HasTestStrip)
            {
                stripImage.color = Rgb(tint.Strip); Style(stripText, p.TestStripText, 30, Rgb(tint.StripText));
                var h = Measure(stripText, freeW - 2 * Gap) + 28f;
                PlaceTop(strip, 0, 0, used, h); Inset(stripText.rectTransform, Gap, 0); used += h;
            }
            banner.gameObject.SetActive(p.HasBanner);
            if (p.HasBanner)
            {
                bannerImage.color = Rgb(tint.Banner); Style(bannerText, p.BannerText, 34, Rgb(tint.BannerText));
                var w = Mathf.Min(freeW - 2 * Gap, 920f); var side = (freeW - w) / 2f;
                var h = Measure(bannerText, w - 2 * Gap) + 40f;
                PlaceTop(banner, side, side, used + Gap, h); Inset(bannerText.rectTransform, Gap, 0); used += Gap + h;
            }
            if (!p.Modal) return;

            var cardW = ControlOverlayRules.CardWidth(freeW, p.Surface);
            var inner = cardW - 2 * Pad;
            var available = freeH - used - 2 * ControlOverlayRules.Margin;
            var buttons = (p.Primary != ControlAction.None ? 1 : 0) + (p.Secondary != ControlAction.None ? 1 : 0);
            var hasTitle = p.Title.Length > 0;
            float titleH = 0, bodyH = 0, buttonH = 0, total = 0, factor = 1f;
            for (; factor >= 0.55f; factor -= 0.05f)
            {
                Style(title, p.Title, Mathf.RoundToInt(60 * factor), Rgb(tint.Title));
                Style(body, p.Body, Mathf.RoundToInt(42 * factor), Rgb(tint.Body));
                titleH = hasTitle ? Measure(title, inner) : 0f;
                bodyH = p.Body.Length > 0 ? Measure(body, inner) : 0f;
                buttonH = Mathf.Max(96f, 120f * factor);
                total = Pad + titleH + (hasTitle && bodyH > 0 ? Gap : 0f) + bodyH + (buttons > 0 ? 40f : 0f)
                    + buttons * buttonH + Mathf.Max(0, buttons - 1) * ButtonGap + Pad;
                if (total <= available) break;
            }
            var viewportH = bodyH;
            if (total > available)
            {
                viewportH = Mathf.Max(120f, bodyH - (total - available));
                total = total - bodyH + viewportH;
            }
            var buttonSize = Mathf.RoundToInt(44 * Mathf.Max(factor, 0.55f));
            var cardTop = used + ControlOverlayRules.Margin + Mathf.Max(0f, (available - total) / 2f);
            var sideMargin = (freeW - cardW) / 2f;
            cardImage.color = Rgb(tint.Card);
            PlaceTop(card, sideMargin, sideMargin, cardTop, total);

            var y = Pad;
            title.gameObject.SetActive(hasTitle);
            if (hasTitle) { PlaceTop(title.rectTransform, Pad, Pad, y, titleH); y += titleH + (bodyH > 0 ? Gap : 0f); }
            viewport.gameObject.SetActive(bodyH > 0);
            if (bodyH > 0)
            {
                PlaceTop(viewport, Pad, Pad, y, viewportH);
                PlaceTop(content, 0, 0, 0, bodyH);
                scroll.enabled = bodyH > viewportH + 1f;
                scroll.verticalNormalizedPosition = 1f;
                y += viewportH;
            }
            if (buttons > 0) y += 40f;
            primary.gameObject.SetActive(p.Primary != ControlAction.None);
            secondary.gameObject.SetActive(p.Secondary != ControlAction.None);
            if (p.Primary != ControlAction.None)
            {
                Paint(primaryImage, primaryLabel, p.PrimaryText, buttonSize, tint.Primary, tint.PrimaryText);
                PlaceTop(primaryRect, Pad, Pad, y, buttonH); y += buttonH + ButtonGap;
            }
            if (p.Secondary != ControlAction.None)
            {
                Paint(secondaryImage, secondaryLabel, p.SecondaryText, buttonSize, tint.Secondary, tint.SecondaryText);
                PlaceTop(secondaryRect, Pad, Pad, y, buttonH);
            }
        }

        private static void Paint(Image image, Text label, string text, int size, uint fill, uint ink)
        {
            image.color = Rgb(fill);
            Style(label, text, size, Rgb(ink));
        }

        // The measured height of a text at the given width, in canvas units. Independent of the canvas scale: the generator works in
        // font pixels scaled by the pixels per unit and the result is divided by the same number.
        private static float Measure(Text text, float width)
        {
            var settings = text.GetGenerationSettings(new Vector2(Mathf.Max(1f, width), 0f));
            var ppu = Mathf.Max(0.01f, text.pixelsPerUnit);
            return text.cachedTextGeneratorForLayout.GetPreferredHeight(text.text, settings) / ppu + 2f;
        }

        private static void Style(Text text, string value, int size, Color color)
        {
            text.font = FontFor(value);
            text.text = value ?? "";
            text.fontSize = size; text.color = color;
        }

        // ---- building blocks ----

        private static RectTransform MakeRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = 5 };
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static Image MakeImage(string name, Transform parent, Sprite sprite)
        {
            var rect = MakeRect(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            return image;
        }

        private static void Round(Image image)
        {
            image.sprite = roundedSprite; image.type = Image.Type.Sliced; image.pixelsPerUnitMultiplier = 1f;
        }

        private static Text MakeText(string name, Transform parent, FontStyle style, TextAnchor anchor)
        {
            var rect = MakeRect(name, parent);
            var text = rect.gameObject.AddComponent<Text>();
            text.fontStyle = style; text.alignment = anchor; text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Overflow;
            text.resizeTextForBestFit = false; text.supportRichText = false;
            return text;
        }

        private static Button MakeButton(string name, Transform parent, out Image image, out Text label, out RectTransform rect)
        {
            rect = MakeRect(name, parent);
            image = rect.gameObject.AddComponent<Image>(); Round(image);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image; button.transition = Selectable.Transition.ColorTint;
            var colors = button.colors;
            colors.normalColor = Color.white; colors.highlightedColor = new Color(0.94f, 0.94f, 0.94f, 1f);
            colors.pressedColor = new Color(0.75f, 0.75f, 0.75f, 1f); colors.selectedColor = Color.white;
            colors.disabledColor = new Color(1f, 1f, 1f, 0.5f);
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            label = MakeText("Label", rect, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform); Inset(label.rectTransform, 16f, 0f);
            return button;
        }

        private static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
        }

        private static void Inset(RectTransform r, float horizontal, float vertical)
        {
            r.offsetMin = new Vector2(horizontal, vertical); r.offsetMax = new Vector2(-horizontal, -vertical);
        }

        // Anchors a rectangle to the top, stretched between the two side margins, at y from the top with the given height.
        private static void PlaceTop(RectTransform r, float leftMargin, float rightMargin, float y, float height)
        {
            r.anchorMin = new Vector2(0f, 1f); r.anchorMax = new Vector2(1f, 1f); r.pivot = new Vector2(0.5f, 1f);
            r.offsetMin = new Vector2(leftMargin, -(y + height)); r.offsetMax = new Vector2(-rightMargin, -y);
        }

        private static Color Rgb(uint value) =>
            new Color(((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f, 1f);

        // ---- fonts and the rounded corner sprite ----

        private static Font FontFor(string text)
        {
            if (NeedsSystemFont(text)) return osFont ?? (osFont = MakeSystemFont());
            return font ?? (font = MakeBuiltinFont());
        }

        // Chinese, Japanese and Korean text is drawn with a system font; the small built-in font has no glyphs for it.
        private static bool NeedsSystemFont(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var c in text) if (c >= 0x2E80) return true;
            return false;
        }

        private static Font MakeBuiltinFont()
        {
            Font made = null;
#if UNITY_2022_2_OR_NEWER
            try { made = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
#else
            try { made = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { }
#endif
            return made != null ? made : Font.CreateDynamicFontFromOSFont("Arial", 16);
        }

        private static Font MakeSystemFont() =>
            Font.CreateDynamicFontFromOSFont(new[]
            {
                "Hiragino Sans", "PingFang SC", "Apple SD Gothic Neo", "Noto Sans CJK JP", "Noto Sans CJK SC", "Noto Sans CJK KR",
                "Noto Sans JP", "Noto Sans SC", "Droid Sans Fallback", "Yu Gothic", "Microsoft YaHei", "Malgun Gothic", "Arial"
            }, 16);

        private static Sprite MakeRounded()
        {
            const int size = 64; const float radius = 24f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[size * size];
            var half = size / 2f;
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var dx = Mathf.Max(Mathf.Abs(x + 0.5f - half) - (half - radius), 0f);
                    var dy = Mathf.Max(Mathf.Abs(y + 0.5f - half) - (half - radius), 0f);
                    var alpha = Mathf.Clamp01(0.5f - (Mathf.Sqrt(dx * dx + dy * dy) - radius));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            texture.SetPixels32(pixels); texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(26, 26, 26, 26));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
    }
}
#endif
