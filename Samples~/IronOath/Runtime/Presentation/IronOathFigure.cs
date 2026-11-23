using UnityEngine;
using UnityEngine.UI;

namespace IronOath.Presentation
{
    /// <summary>Original heraldic stand-in. Its feet remain the pivot when a real actor replaces it.</summary>
    [AddComponentMenu("")]
    public sealed class IronOathFigure : MaskableGraphic
    {
        public Color Cloak = new Color32(55, 101, 111, 255);
        public bool LightArmor;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            Color dark = new Color32(20, 28, 32, 255);
            Color steel = new Color32(157, 168, 170, 255);
            Color shade = new Color32(92, 110, 119, 255);
            Color gold = IronOathUi.Gold;
            // Foot pivot at lower center; a restrained painted-paper silhouette makes missing assets explicit.
            Poly(vh, r, new Color(0, 0, 0, .38f), .10f, .025f, .24f, .07f, .78f, .07f, .92f, .025f, .68f, 0, .30f, 0);
            Poly(vh, r, Cloak, .38f, .80f, .66f, .80f, .88f, .16f, .70f, .21f, .54f, .12f, .25f, .17f);
            Poly(vh, r, dark, .40f, .45f, .54f, .44f, .49f, .08f, .30f, .07f, .32f, .13f);
            Poly(vh, r, shade, .55f, .45f, .69f, .44f, .75f, .08f, .54f, .07f, .56f, .15f);
            Poly(vh, r, LightArmor ? Cloak : steel, .36f, .77f, .64f, .79f, .73f, .49f, .37f, .45f, .29f, .64f);
            Poly(vh, r, shade, .52f, .76f, .64f, .79f, .73f, .49f, .54f, .47f);
            Poly(vh, r, gold, .34f, .50f, .71f, .51f, .72f, .46f, .37f, .43f);
            Poly(vh, r, steel, .28f, .77f, .39f, .78f, .42f, .69f, .26f, .66f, .18f, .70f);
            Poly(vh, r, steel, .61f, .80f, .76f, .77f, .79f, .69f, .62f, .68f);
            Poly(vh, r, shade, .45f, .94f, .61f, .95f, .68f, .86f, .61f, .77f, .46f, .79f, .41f, .86f);
            Poly(vh, r, LightArmor ? new Color32(204, 177, 142, 255) : steel,
                .49f, .92f, .62f, .91f, .66f, .86f, .58f, .79f, .49f, .81f);
            Poly(vh, r, dark, .50f, .88f, .66f, .87f, .65f, .85f, .51f, .85f);
            Poly(vh, r, gold, .47f, .95f, .56f, 1f, .62f, .95f, .56f, .92f);
            if (LightArmor)
            {
                Poly(vh, r, steel, .77f, .61f, .92f, .85f, .95f, .93f, .94f, .83f, .81f, .58f);
                Poly(vh, r, gold, .73f, .60f, .82f, .66f, .86f, .63f, .78f, .56f);
            }
            else
            {
                Poly(vh, r, gold, .13f, .64f, .34f, .67f, .43f, .62f, .41f, .39f, .29f, .29f, .14f, .40f);
                Poly(vh, r, Cloak, .17f, .60f, .33f, .63f, .39f, .59f, .37f, .41f, .29f, .34f, .19f, .43f);
                Poly(vh, r, gold, .26f, .58f, .30f, .59f, .32f, .39f, .28f, .38f);
                Poly(vh, r, steel, .78f, .57f, .83f, .56f, .91f, .94f, .88f, .99f, .85f, .94f);
                Poly(vh, r, gold, .74f, .58f, .89f, .60f, .89f, .64f, .74f, .62f);
            }
        }

        static void Poly(VertexHelper vh, Rect r, Color color, params float[] xy)
        {
            int first = vh.currentVertCount;
            for (int i = 0; i < xy.Length; i += 2)
                vh.AddVert(new Vector3(r.xMin + xy[i] * r.width, r.yMin + xy[i + 1] * r.height), color, Vector2.zero);
            for (int i = 1; i < xy.Length / 2 - 1; i++) vh.AddTriangle(first, first + i, first + i + 1);
        }
    }
}
