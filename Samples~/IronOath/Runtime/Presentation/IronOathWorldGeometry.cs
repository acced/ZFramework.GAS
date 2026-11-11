using System.Collections.Generic;
using UnityEngine;

namespace IronOath.Presentation
{
    /// <summary>Small original stand-ins shared by every renderer pipeline. Built once when a stage opens.</summary>
    internal static class IronOathWorldGeometry
    {
        internal static Mesh Quad(float width, float height)
        {
            var mesh = new Mesh { name = "Iron Oath backdrop quad" };
            mesh.vertices = new[]
            {
                new Vector3(-width * .5f, -height * .5f, 0), new Vector3(width * .5f, -height * .5f, 0),
                new Vector3(width * .5f, height * .5f, 0), new Vector3(-width * .5f, height * .5f, 0)
            };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        internal static Mesh Ground()
        {
            const int segments = 24;
            var vertices = new Vector3[segments + 1];
            var colors = new Color[vertices.Length];
            var triangles = new int[segments * 3];
            colors[0] = Color.white;
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2 / segments;
                vertices[i + 1] = new Vector3(Mathf.Cos(angle) * .67f, Mathf.Sin(angle) * .09f, 0);
                colors[i + 1] = Color.white;
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = (i + 1) % segments + 1;
            }
            var mesh = new Mesh { name = "Iron Oath ground marker" };
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        internal static Mesh Figure(bool lightArmor, Color cloak)
        {
            var vertices = new List<Vector3>(128);
            var colors = new List<Color>(128);
            var triangles = new List<int>(256);
            Color dark = new Color32(20, 28, 32, 255);
            Color steel = new Color32(157, 168, 170, 255);
            Color shade = new Color32(92, 110, 119, 255);
            Color gold = IronOathUi.Gold;
            Add(cloak, .38f, .80f, .66f, .80f, .88f, .16f, .70f, .21f, .54f, .12f, .25f, .17f);
            Add(dark, .40f, .45f, .54f, .44f, .49f, .08f, .30f, .07f, .32f, .13f);
            Add(shade, .55f, .45f, .69f, .44f, .75f, .08f, .54f, .07f, .56f, .15f);
            Add(lightArmor ? cloak : steel, .36f, .77f, .64f, .79f, .73f, .49f, .37f, .45f, .29f, .64f);
            Add(shade, .52f, .76f, .64f, .79f, .73f, .49f, .54f, .47f);
            Add(gold, .34f, .50f, .71f, .51f, .72f, .46f, .37f, .43f);
            Add(steel, .28f, .77f, .39f, .78f, .42f, .69f, .26f, .66f, .18f, .70f);
            Add(steel, .61f, .80f, .76f, .77f, .79f, .69f, .62f, .68f);
            Add(shade, .45f, .94f, .61f, .95f, .68f, .86f, .61f, .77f, .46f, .79f, .41f, .86f);
            Add(lightArmor ? new Color32(204, 177, 142, 255) : steel,
                .49f, .92f, .62f, .91f, .66f, .86f, .58f, .79f, .49f, .81f);
            Add(dark, .50f, .88f, .66f, .87f, .65f, .85f, .51f, .85f);
            Add(gold, .47f, .95f, .56f, 1f, .62f, .95f, .56f, .92f);
            if (lightArmor)
            {
                Add(steel, .77f, .61f, .92f, .85f, .95f, .93f, .94f, .83f, .81f, .58f);
                Add(gold, .73f, .60f, .82f, .66f, .86f, .63f, .78f, .56f);
            }
            else
            {
                Add(gold, .13f, .64f, .34f, .67f, .43f, .62f, .41f, .39f, .29f, .29f, .14f, .40f);
                Add(cloak, .17f, .60f, .33f, .63f, .39f, .59f, .37f, .41f, .29f, .34f, .19f, .43f);
                Add(gold, .26f, .58f, .30f, .59f, .32f, .39f, .28f, .38f);
                Add(steel, .78f, .57f, .83f, .56f, .91f, .94f, .88f, .99f, .85f, .94f);
                Add(gold, .74f, .58f, .89f, .60f, .89f, .64f, .74f, .62f);
            }
            var mesh = new Mesh { name = "Iron Oath heraldic stand-in" };
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;

            void Add(Color color, params float[] xy)
            {
                int first = vertices.Count;
                for (int i = 0; i < xy.Length; i += 2)
                {
                    // Tiny local depth offsets preserve polygon layering on a single mesh.
                    vertices.Add(new Vector3((xy[i] - .5f) * BattleStageLayout.FigureWidth,
                        xy[i + 1] * BattleStageLayout.FigureHeight, -first * .00001f));
                    colors.Add(color);
                }
                for (int i = 1; i < xy.Length / 2 - 1; i++)
                { triangles.Add(first); triangles.Add(first + i); triangles.Add(first + i + 1); }
            }
        }
    }
}
