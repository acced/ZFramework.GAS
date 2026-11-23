using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace IronOath.Presentation
{
    /// <summary>Only screen construction allocates. Battle views are retained and updated in place.</summary>
    internal static class IronOathUi
    {
        internal static readonly Color Ink = new Color32(17, 25, 32, 255);
        internal static readonly Color PanelInk = new Color32(23, 31, 37, 245);
        internal static readonly Color Gold = new Color32(197, 160, 96, 255);
        internal static readonly Color PaleGold = new Color32(237, 211, 159, 255);
        internal static readonly Color Ivory = new Color32(237, 229, 207, 255);
        internal static readonly Color Muted = new Color32(160, 166, 164, 255);
        internal static readonly Color Red = new Color32(173, 61, 53, 255);
        internal static readonly Color Blue = new Color32(73, 145, 181, 255);
        internal static readonly Color Teal = new Color32(72, 125, 118, 255);
        internal static Font Font;

        internal static RectTransform Rect(Transform parent, string name, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
            return rect;
        }

        internal static Image Fill(Transform parent, string name, float x, float y, float w, float h, Color color)
        {
            var image = Rect(parent, name, x, y, w, h).gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        internal static RectTransform Panel(Transform parent, string name, float x, float y, float w, float h,
            Color? fill = null, bool ornament = true)
        {
            var rect = Rect(parent, name, x, y, w, h);
            Fill(rect, "Shadow", 8, 10, w, h, new Color(0, 0, 0, 0.3f));
            Fill(rect, "Surface", 0, 0, w, h, fill ?? PanelInk);
            Color edge = new Color(Gold.r, Gold.g, Gold.b, 0.45f);
            Fill(rect, "Top", 0, 0, w, 1, edge);
            Fill(rect, "Bottom", 0, h - 1, w, 1, edge);
            Fill(rect, "Left", 0, 0, 1, h, edge);
            Fill(rect, "Right", w - 1, 0, 1, h, edge);
            if (ornament)
            {
                Corner(rect, 0, 0, 1, 1);
                Corner(rect, w, 0, -1, 1);
                Corner(rect, 0, h, 1, -1);
                Corner(rect, w, h, -1, -1);
            }
            return rect;
        }

        static void Corner(Transform parent, float x, float y, float dx, float dy)
        {
            Fill(parent, "CornerH", x + (dx < 0 ? -24 : 0), y + (dy < 0 ? -3 : 0), 24, 3, Gold);
            Fill(parent, "CornerV", x + (dx < 0 ? -3 : 0), y + (dy < 0 ? -24 : 0), 3, 24, Gold);
        }

        internal static Text Label(Transform parent, string value, float x, float y, float w, float h,
            int size = 22, Color? color = null, TextAnchor align = TextAnchor.UpperLeft, bool bold = false)
        {
            var text = Rect(parent, "Text", x, y, w, h).gameObject.AddComponent<Text>();
            text.font = Font;
            text.text = value;
            text.fontSize = size;
            text.color = color ?? Ivory;
            text.alignment = align;
            text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            text.lineSpacing = 1.12f;
            return text;
        }

        internal static Button Button(Transform parent, string value, float x, float y, float w, float h,
            UnityAction action, bool primary = false, bool enabled = true)
        {
            var rect = Rect(parent, value, x, y, w, h);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = primary ? new Color32(129, 91, 46, 255) : new Color32(35, 47, 53, 250);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color32(238, 223, 186, 255);
            colors.pressedColor = new Color32(168, 149, 117, 255);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color32(106, 106, 106, 150);
            colors.fadeDuration = .12f;
            button.colors = colors;
            button.interactable = enabled;
            button.onClick.AddListener(action);
            Fill(rect, "UpperRule", 0, 0, w, 1, primary ? PaleGold : Gold * .65f);
            Fill(rect, "LowerRule", 0, h - 1, w, 1, Gold * .65f);
            Label(rect, value, 8, 0, w - 16, h, 21, primary ? PaleGold : Ivory, TextAnchor.MiddleCenter);
            return button;
        }

        internal static void Rule(Transform parent, float x, float y, float w)
        {
            Fill(parent, "Rule", x, y, w, 1, new Color(Gold.r, Gold.g, Gold.b, .35f));
        }

        internal static Image Bar(Transform parent, string name, float x, float y, float w, float h, Color fill)
        {
            Fill(parent, name + " Back", x, y, w, h, new Color32(8, 12, 17, 235));
            var front = Fill(parent, name, x + 1, y + 1, w - 2, h - 2, fill);
            return front;
        }

        internal static void SetBar(Image image, float amount, float maximum, float width)
        {
            image.rectTransform.sizeDelta = new Vector2(Mathf.Clamp01(maximum > 0 ? amount / maximum : 0) * width,
                image.rectTransform.sizeDelta.y);
        }

        internal static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        internal static void Art(Transform parent, string resource, float x, float y, float w, float h,
            Color? tint = null)
        {
            var texture = Resources.Load<Texture2D>(resource);
            if (texture == null) return;
            var image = Rect(parent, "Painted backdrop", x, y, w, h).gameObject.AddComponent<RawImage>();
            image.texture = texture;
            image.color = tint ?? Color.white;
            image.raycastTarget = false;
            float viewAspect = w / h, artAspect = (float)texture.width / texture.height;
            if (artAspect > viewAspect)
            {
                float fraction = viewAspect / artAspect;
                image.uvRect = new Rect((1 - fraction) * .5f, 0, fraction, 1);
            }
            else
            {
                float fraction = artAspect / viewAspect;
                image.uvRect = new Rect(0, (1 - fraction) * .5f, 1, fraction);
            }
        }
    }
}
