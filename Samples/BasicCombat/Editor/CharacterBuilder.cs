using UnityEngine;

namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    /// <summary>
    /// 程序化角色：持剑盾的骑士与训练假人。
    /// 骨骼按人形命名并面朝 +Z；绑定姿势下四肢沿 -Y 下垂，所有骨骼本地旋转为单位四元数，动画只写旋转与髋部位移。
    /// </summary>
    internal static class CharacterBuilder
    {
        public sealed class Palette
        {
            public Material Steel, DarkSteel, Navy, Crimson, Gold, Leather, Glow, Wood, Straw, Burlap, Stone, TargetRed, TargetWhite;
        }

        public static Palette CreatePalette(SampleArt art) => new()
        {
            Steel = art.Opaque("Knight_Steel", "#c8d0da", .42f, .76f),
            DarkSteel = art.Opaque("Knight_Mail", "#5e6878", .3f, .5f),
            Navy = art.Opaque("Knight_Navy", "#24448a", 0, .3f),
            Crimson = art.Opaque("Knight_Crimson", "#a3203a", 0, .3f),
            Gold = art.Opaque("Knight_Gold", "#f2bf55", .45f, .72f, "#5a3a08", .5f),
            Leather = art.Opaque("Knight_Leather", "#5e3b27", 0, .38f),
            Glow = art.Opaque("Knight_Glow", "#7fe9ff", 0, .9f, "#2bd6ff", 1.7f),
            Wood = art.Opaque("Dummy_Wood", "#8f5c36", 0, .22f),
            Straw = art.Opaque("Dummy_Straw", "#d8b56a", 0, .12f),
            Burlap = art.Opaque("Dummy_Burlap", "#c4a06a", 0, .1f),
            Stone = art.Opaque("Arena_Stone", "#6c7280", 0, .25f),
            TargetRed = art.Opaque("Dummy_TargetRed", "#c8323b", 0, .3f),
            TargetWhite = art.Opaque("Dummy_TargetWhite", "#f1ece0", 0, .3f),
        };

        public static GameObject BuildKnight(SampleArt art, Palette m)
        {
            var root = new GameObject("Knight");
            var hips = SampleArt.Bone(root.transform, "Hips", new Vector3(0, .94f, 0));

            // ── 腰带与下摆 ──
            SampleArt.Part(hips, "Belt", art.Save(MeshKit.RoundedBox("Knight.Belt", new Vector3(.36f, .1f, .26f), .045f)), m.Leather, new Vector3(0, .01f, 0));
            SampleArt.Part(hips, "Buckle", art.Save(MeshKit.RoundedBox("Knight.Buckle", new Vector3(.1f, .075f, .03f), .012f)), m.Gold, new Vector3(0, .01f, .132f));
            SampleArt.Part(hips, "MailSkirt", art.Save(MeshKit.Lathe("Knight.MailSkirt",
                new[] { new Vector2(.17f, -.24f), new Vector2(.19f, -.18f), new Vector2(.18f, -.06f), new Vector2(.165f, 0) }, 28, 1.05f, .8f, false, false)), m.DarkSteel, Vector3.zero);
            var tabard = art.Save(MeshKit.Cloth("Knight.Tabard", .2f, .36f, .025f, .25f));
            SampleArt.Part(hips, "TabardFront", tabard, m.Navy, new Vector3(0, -.03f, .15f), new Vector3(-6, 180, 0));
            SampleArt.Part(hips, "TabardBack", tabard, m.Navy, new Vector3(0, -.03f, -.15f), new Vector3(6, 0, 0));
            var trim = art.Save(MeshKit.RoundedBox("Knight.TabardTrim", new Vector3(.25f, .025f, .02f), .008f));
            SampleArt.Part(hips, "TabardTrimFront", trim, m.Gold, new Vector3(0, -.38f, .19f), new Vector3(-6, 0, 0));
            SampleArt.Part(hips, "TabardTrimBack", trim, m.Gold, new Vector3(0, -.38f, -.19f), new Vector3(6, 0, 0));

            // ── 躯干 ──
            var spine = SampleArt.Bone(hips, "Spine", new Vector3(0, .08f, 0));
            SampleArt.Part(spine, "Abdomen", art.Save(MeshKit.Lathe("Knight.Abdomen",
                new[] { new Vector2(.15f, -.04f), new Vector2(.155f, .06f), new Vector2(.165f, .18f) }, 28, 1.1f, .8f)), m.DarkSteel, Vector3.zero);
            var chest = SampleArt.Bone(spine, "Chest", new Vector3(0, .17f, 0));
            SampleArt.Part(chest, "Breastplate", art.Save(MeshKit.Lathe("Knight.Breastplate",
                new[] { new Vector2(.155f, -.05f), new Vector2(.172f, 0), new Vector2(.205f, .1f), new Vector2(.205f, .18f), new Vector2(.165f, .245f), new Vector2(.09f, .275f), new Vector2(.05f, .28f) },
                32, 1.12f, .82f, true, true, .03f)), m.Steel, Vector3.zero);
            SampleArt.Part(chest, "PlateRidge", art.Save(MeshKit.RoundedBox("Knight.PlateRidge", new Vector3(.03f, .26f, .03f), .012f)), m.Steel, new Vector3(0, .1f, .173f), new Vector3(-8, 0, 0));
            SampleArt.Part(chest, "Emblem", art.Save(MeshKit.RoundedBox("Knight.Emblem", new Vector3(.11f, .11f, .025f), .01f)), m.Gold, new Vector3(0, .13f, .178f), new Vector3(-6, 0, 45));
            SampleArt.Part(chest, "EmblemGem", art.Save(MeshKit.Lathe("Knight.Gem",
                new[] { new Vector2(0, -.02f), new Vector2(.025f, -.008f), new Vector2(.025f, .008f), new Vector2(0, .02f) }, 8)), m.Glow, new Vector3(0, .13f, .195f), new Vector3(90, 0, 0));
            SampleArt.Part(chest, "Collar", art.Save(MeshKit.Torus("Knight.Collar", .085f, .022f, 28, 8)), m.Gold, new Vector3(0, .268f, .004f), new Vector3(0, 0, 0), new Vector3(1.1f, 1, .95f));

            // ── 头盔 ──
            var neck = SampleArt.Bone(chest, "Neck", new Vector3(0, .27f, 0));
            SampleArt.Part(neck, "NeckMail", art.Save(MeshKit.Lathe("Knight.Neck", new[] { new Vector2(.065f, -.01f), new Vector2(.06f, .09f) }, 16)), m.DarkSteel, Vector3.zero);
            var head = SampleArt.Bone(neck, "Head", new Vector3(0, .08f, 0));
            var helmScale = SampleArt.Bone(head, "HelmScale", Vector3.zero); helmScale.localScale = Vector3.one * 1.12f;
            SampleArt.Part(helmScale, "Helm", art.Save(MeshKit.Lathe("Knight.Helm",
                new[] { new Vector2(.112f, -.02f), new Vector2(.128f, .03f), new Vector2(.136f, .11f), new Vector2(.128f, .18f), new Vector2(.096f, .238f), new Vector2(.045f, .266f), new Vector2(0, .272f) },
                32, 1f, 1.08f)), m.Steel, Vector3.zero);
            SampleArt.Part(helmScale, "Visor", art.Save(MeshKit.RoundedBox("Knight.Visor", new Vector3(.17f, .024f, .03f), .011f)), m.Glow, new Vector3(0, .125f, .137f));
            SampleArt.Part(helmScale, "VisorBrow", art.Save(MeshKit.RoundedBox("Knight.VisorBrow", new Vector3(.2f, .03f, .04f), .014f)), m.Gold, new Vector3(0, .152f, .135f), new Vector3(-8, 0, 0));
            SampleArt.Part(helmScale, "NoseGuard", art.Save(MeshKit.RoundedBox("Knight.NoseGuard", new Vector3(.032f, .1f, .035f), .014f)), m.Steel, new Vector3(0, .07f, .145f), new Vector3(-6, 0, 0));
            SampleArt.Part(helmScale, "Crest", art.Save(MeshKit.RoundedBox("Knight.Crest", new Vector3(.03f, .05f, .27f), .014f)), m.Gold, new Vector3(0, .25f, -.005f), new Vector3(-6, 0, 0));
            var plume = SampleArt.Bone(helmScale, "Plume", new Vector3(0, .27f, -.04f));
            SampleArt.Part(plume, "PlumeMesh", art.Save(MeshKit.Sweep("Knight.Plume",
                new[] { new Vector3(0, -.01f, .04f), new Vector3(0, .07f, -.02f), new Vector3(0, .085f, -.12f), new Vector3(0, .04f, -.22f), new Vector3(0, -.05f, -.28f), new Vector3(0, -.16f, -.3f) },
                new[] { .036f, .062f, .07f, .06f, .04f, .008f }, 14, true, .6f)), m.Crimson, Vector3.zero);

            // ── 披风 ──
            var cape = SampleArt.Bone(chest, "Cape", new Vector3(0, .22f, -.15f));
            SampleArt.Part(cape, "CapeCloth", art.Save(MeshKit.Cloth("Knight.Cape", .4f, .98f, .07f, .45f, 24, 18, 3, .035f)), m.Crimson, Vector3.zero, new Vector3(4, 0, 0));
            SampleArt.Part(cape, "CapeHem", art.Save(MeshKit.Cloth("Knight.CapeHem", .4f, .98f, .07f, .45f, 24, 2, 3, .035f, .945f, 1f, .004f)), m.Gold, Vector3.zero, new Vector3(4, 0, 0));
            SampleArt.Part(cape, "CapeClasp", art.Save(MeshKit.RoundedBox("Knight.CapeClasp", new Vector3(.42f, .045f, .04f), .018f)), m.Gold, new Vector3(0, .005f, .01f));

            // ── 手臂 ──
            var pauldron = art.Save(MeshKit.Lathe("Knight.Pauldron",
                new[] { new Vector2(.125f, -.07f), new Vector2(.13f, -.02f), new Vector2(.112f, .045f), new Vector2(.07f, .09f), new Vector2(0, .105f) }, 24, 1f, 1.05f));
            var pauldronRim = art.Save(MeshKit.Torus("Knight.PauldronRim", .126f, .014f, 28, 6));
            var upperArm = art.Save(MeshKit.Lathe("Knight.UpperArm", new[] { new Vector2(.05f, -.28f), new Vector2(.06f, -.14f), new Vector2(.058f, 0) }, 18));
            var elbow = art.Save(MeshKit.Lathe("Knight.Elbow", new[] { new Vector2(0, -.05f), new Vector2(.05f, -.035f), new Vector2(.062f, 0), new Vector2(.05f, .035f), new Vector2(0, .05f) }, 16));
            var gauntlet = art.Save(MeshKit.Lathe("Knight.Gauntlet",
                new[] { new Vector2(.048f, 0), new Vector2(.054f, -.06f), new Vector2(.063f, -.18f), new Vector2(.075f, -.215f), new Vector2(.075f, -.215f), new Vector2(.07f, -.235f) }, 20));
            var cuffBand = art.Save(MeshKit.Torus("Knight.CuffBand", .066f, .011f, 24, 6));
            var fist = art.Save(MeshKit.RoundedBox("Knight.Fist", new Vector3(.085f, .1f, .1f), .032f));
            Transform BuildArm(string side, float sign)
            {
                var shoulder = SampleArt.Bone(chest, "Shoulder." + side, new Vector3(.19f * sign, .2f, 0));
                var upper = SampleArt.Bone(shoulder, "UpperArm." + side, new Vector3(.06f * sign, 0, 0));
                SampleArt.Part(upper, "Pauldron", pauldron, m.Steel, new Vector3(.02f * sign, .025f, 0), new Vector3(0, 0, -18 * sign));
                SampleArt.Part(upper, "PauldronRim", pauldronRim, m.Gold, new Vector3(.02f * sign - .02f * sign, -.04f, 0), new Vector3(0, 0, -18 * sign), new Vector3(1, 1, 1.05f));
                SampleArt.Part(upper, "UpperArmMesh", upperArm, m.Navy, Vector3.zero);
                var forearm = SampleArt.Bone(upper, "Forearm." + side, new Vector3(0, -.28f, 0));
                SampleArt.Part(forearm, "Elbow", elbow, m.Steel, new Vector3(0, 0, -.01f));
                SampleArt.Part(forearm, "Gauntlet", gauntlet, m.Steel, Vector3.zero);
                SampleArt.Part(forearm, "CuffBand", cuffBand, m.Gold, new Vector3(0, -.2f, 0));
                var hand = SampleArt.Bone(forearm, "Hand." + side, new Vector3(0, -.25f, 0));
                SampleArt.Part(hand, "Fist", fist, m.Leather, new Vector3(0, -.045f, .005f));
                return hand;
            }
            var handR = BuildArm("R", 1);
            var handL = BuildArm("L", -1);

            // ── 骑士剑：握柄沿手的 Z 轴，剑身指向 +Z ──
            var sword = SampleArt.Bone(handR, "Sword", new Vector3(0, -.05f, 0));
            SampleArt.Part(sword, "Grip", art.Save(MeshKit.Sweep("Knight.Grip", new[] { new Vector3(0, 0, -.085f), new Vector3(0, 0, .075f) }, new[] { .02f, .02f }, 12)), m.Leather, Vector3.zero);
            SampleArt.Part(sword, "Guard", art.Save(MeshKit.Sweep("Knight.Guard",
                new[] { new Vector3(-.13f, 0, .1f), new Vector3(-.07f, 0, .085f), new Vector3(0, 0, .08f), new Vector3(.07f, 0, .085f), new Vector3(.13f, 0, .1f) },
                new[] { .016f, .019f, .024f, .019f, .016f }, 10)), m.Gold, Vector3.zero);
            SampleArt.Part(sword, "Ricasso", art.Save(MeshKit.RoundedBox("Knight.Ricasso", new Vector3(.05f, .028f, .05f), .01f)), m.Gold, new Vector3(0, 0, .095f));
            SampleArt.Part(sword, "Blade", art.Save(MeshKit.Blade("Knight.Blade", .86f, .062f, .014f, .16f)), m.Steel, new Vector3(0, 0, .11f));
            SampleArt.Part(sword, "Fuller", art.Save(MeshKit.RoundedBox("Knight.Fuller", new Vector3(.012f, .016f, .5f), .005f)), m.Glow, new Vector3(0, 0, .42f), Vector3.zero, new Vector3(1, .9f, 1));
            SampleArt.Part(sword, "Pommel", art.Save(MeshKit.Lathe("Knight.Pommel",
                new[] { new Vector2(0, -.03f), new Vector2(.028f, -.018f), new Vector2(.03f, .005f), new Vector2(.018f, .028f), new Vector2(0, .032f) }, 16)), m.Gold, new Vector3(0, 0, -.11f), new Vector3(90, 0, 0));
            var tip = SampleArt.Bone(sword, "SwordTip", new Vector3(0, 0, .97f));
            SampleArt.Bone(sword, "SwordBase", new Vector3(0, 0, .15f));
            tip.gameObject.SetActive(true);

            // ── 纹章盾：正面朝手臂外侧 ──
            var shieldMount = SampleArt.Bone(handL.parent, "Shield", new Vector3(-.075f, -.13f, .01f));
            shieldMount.localRotation = Quaternion.Euler(0, -90, 0);
            var outline = HeaterOutline(.24f, .36f, 24);
            SampleArt.Part(shieldMount, "ShieldRim", art.Save(MeshKit.Extrude("Knight.ShieldRim", outline, .045f, .014f)), m.Gold, Vector3.zero);
            SampleArt.Part(shieldMount, "ShieldFace", art.Save(MeshKit.Extrude("Knight.ShieldFace", Scale(outline, .86f, new Vector2(0, .01f)), .03f, .012f)), m.Navy, new Vector3(0, 0, .014f));
            // V 形纹章是凹多边形，拆成两条凸的斜带（挤出按扇形三角化，只接受凸轮廓）。
            SampleArt.Part(shieldMount, "ShieldChevronL", art.Save(MeshKit.Extrude("Knight.ShieldChevronL",
                new[] { new Vector2(0, .09f), new Vector2(-.16f, -.05f), new Vector2(-.16f, -.11f), new Vector2(0, .03f) }, .015f, .006f)), m.Gold, new Vector3(0, 0, .034f));
            SampleArt.Part(shieldMount, "ShieldChevronR", art.Save(MeshKit.Extrude("Knight.ShieldChevronR",
                new[] { new Vector2(.16f, -.11f), new Vector2(.16f, -.05f), new Vector2(0, .09f), new Vector2(0, .03f) }, .015f, .006f)), m.Gold, new Vector3(0, 0, .034f));
            SampleArt.Part(shieldMount, "ShieldGem", art.Save(MeshKit.Lathe("Knight.ShieldGem",
                new[] { new Vector2(0, -.025f), new Vector2(.035f, -.01f), new Vector2(.035f, .01f), new Vector2(0, .03f) }, 10)), m.Glow, new Vector3(0, .13f, .04f), new Vector3(90, 0, 0));

            // ── 腿 ──
            var thigh = art.Save(MeshKit.Lathe("Knight.Thigh", new[] { new Vector2(.068f, -.42f), new Vector2(.085f, -.12f), new Vector2(.08f, 0) }, 20));
            var knee = art.Save(MeshKit.RoundedBox("Knight.Knee", new Vector3(.105f, .1f, .085f), .04f));
            var greave = art.Save(MeshKit.Lathe("Knight.Greave", new[] { new Vector2(.055f, -.36f), new Vector2(.068f, -.14f), new Vector2(.064f, -.02f) }, 20, 1f, 1.1f));
            var boot = art.Save(MeshKit.RoundedBox("Knight.Boot", new Vector3(.11f, .09f, .24f), .04f));
            var toe = art.Save(MeshKit.RoundedBox("Knight.Toe", new Vector3(.104f, .06f, .09f), .03f));
            void BuildLeg(string side, float sign)
            {
                var upper = SampleArt.Bone(hips, "Thigh." + side, new Vector3(.1f * sign, -.06f, 0));
                SampleArt.Part(upper, "ThighMesh", thigh, m.Navy, Vector3.zero);
                var shin = SampleArt.Bone(upper, "Shin." + side, new Vector3(0, -.42f, 0));
                SampleArt.Part(shin, "Knee", knee, m.Steel, new Vector3(0, .005f, .03f));
                SampleArt.Part(shin, "Greave", greave, m.Steel, Vector3.zero);
                var foot = SampleArt.Bone(shin, "Foot." + side, new Vector3(0, -.39f, 0));
                SampleArt.Part(foot, "Boot", boot, m.Leather, new Vector3(0, -.012f, .045f));
                SampleArt.Part(foot, "Toe", toe, m.Steel, new Vector3(0, -.02f, .14f));
            }
            BuildLeg("R", 1);
            BuildLeg("L", -1);
            return root;
        }

        public static GameObject BuildDummy(SampleArt art, Palette m)
        {
            var root = new GameObject("TrainingDummy");
            SampleArt.Part(root.transform, "Base", art.Save(MeshKit.Lathe("Dummy.Base",
                new[] { new Vector2(.34f, 0), new Vector2(.36f, .03f), new Vector2(.33f, .08f), new Vector2(.33f, .08f), new Vector2(.12f, .1f), new Vector2(0, .1f) }, 32)), m.Stone, Vector3.zero);
            SampleArt.Part(root.transform, "Post", art.Save(MeshKit.Lathe("Dummy.Post", new[] { new Vector2(.05f, .08f), new Vector2(.045f, .55f) }, 14)), m.Wood, Vector3.zero);
            // 受击时绕这个枢轴前后摇晃。
            var body = SampleArt.Bone(root.transform, "Body", new Vector3(0, .5f, 0));
            SampleArt.Part(body, "Torso", art.Save(MeshKit.Lathe("Dummy.Torso",
                new[] { new Vector2(.12f, 0), new Vector2(.19f, .08f), new Vector2(.215f, .25f), new Vector2(.2f, .42f), new Vector2(.15f, .5f), new Vector2(.06f, .54f) }, 28, 1f, .85f)), m.Straw, Vector3.zero);
            var rope = art.Save(MeshKit.Torus("Dummy.Rope", .2f, .016f, 32, 6));
            SampleArt.Part(body, "RopeLow", rope, m.Leather, new Vector3(0, .12f, 0), Vector3.zero, new Vector3(1.04f, 1, .9f));
            SampleArt.Part(body, "RopeHigh", rope, m.Leather, new Vector3(0, .4f, 0), Vector3.zero, new Vector3(1.02f, 1, .88f));
            SampleArt.Part(body, "Crossbar", art.Save(MeshKit.Sweep("Dummy.Crossbar",
                new[] { new Vector3(-.46f, .36f, 0), new Vector3(0, .38f, 0), new Vector3(.46f, .36f, 0) }, new[] { .032f, .036f, .032f }, 12)), m.Wood, Vector3.zero);
            var tuft = art.Save(MeshKit.Lathe("Dummy.Tuft", new[] { new Vector2(.045f, 0), new Vector2(.07f, .07f), new Vector2(.02f, .12f) }, 14));
            SampleArt.Part(body, "TuftR", tuft, m.Straw, new Vector3(.46f, .36f, 0), new Vector3(0, 0, -90));
            SampleArt.Part(body, "TuftL", tuft, m.Straw, new Vector3(-.46f, .36f, 0), new Vector3(0, 0, 90));
            SampleArt.Part(body, "Head", art.Save(MeshKit.Lathe("Dummy.Head",
                new[] { new Vector2(0, -.13f), new Vector2(.1f, -.1f), new Vector2(.14f, 0), new Vector2(.12f, .09f), new Vector2(.06f, .13f), new Vector2(0, .14f) }, 24)), m.Burlap, new Vector3(0, .68f, 0));
            SampleArt.Part(body, "HeadTie", art.Save(MeshKit.Torus("Dummy.HeadTie", .1f, .014f, 24, 6)), m.Leather, new Vector3(0, .55f, 0));
            var disc = MeshKit.Extrude("Dummy.TargetDisc", Circle(1, 32), .02f, .004f);
            SampleArt.Part(body, "TargetOuter", art.Save(Scaled(disc, "Dummy.TargetOuter", .12f)), m.TargetRed, new Vector3(0, .26f, .185f), new Vector3(-6, 0, 0));
            SampleArt.Part(body, "TargetMiddle", art.Save(Scaled(disc, "Dummy.TargetMiddle", .08f)), m.TargetWhite, new Vector3(0, .26f, .195f), new Vector3(-6, 0, 0));
            SampleArt.Part(body, "TargetCenter", art.Save(Scaled(disc, "Dummy.TargetCenter", .035f)), m.TargetRed, new Vector3(0, .26f, .205f), new Vector3(-6, 0, 0));
            return root;
        }

        private static Mesh Scaled(Mesh source, string name, float radius)
        {
            var mesh = Object.Instantiate(source); mesh.name = name;
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = new Vector3(vertices[i].x * radius, vertices[i].y * radius, vertices[i].z);
            mesh.vertices = vertices; mesh.RecalculateBounds(); return mesh;
        }

        private static Vector2[] Circle(float radius, int count)
        {
            var points = new Vector2[count];
            for (int i = 0; i < count; i++) { float a = i / (float)count * Mathf.PI * 2; points[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius; }
            return points;
        }

        /// <summary>纹章盾（heater）轮廓：上沿微拱，两侧收成底部尖角。逆时针。</summary>
        private static Vector2[] HeaterOutline(float halfWidth, float height, int sideSteps)
        {
            var points = new System.Collections.Generic.List<Vector2>();
            float top = height * .42f, bottom = -height * .58f;
            // 右侧：自上而下弧线收尖
            for (int i = 0; i <= sideSteps; i++)
            {
                float t = i / (float)sideSteps;
                float y = Mathf.Lerp(top, bottom, t);
                float x = halfWidth * Mathf.Cos(Mathf.Pow(t, 1.6f) * Mathf.PI * .5f);
                points.Add(new Vector2(x, y));
            }
            // 左侧：自下而上
            for (int i = sideSteps - 1; i >= 0; i--)
            {
                float t = i / (float)sideSteps;
                points.Add(new Vector2(-halfWidth * Mathf.Cos(Mathf.Pow(t, 1.6f) * Mathf.PI * .5f), Mathf.Lerp(top, bottom, t)));
            }
            // 上沿拱起
            for (int i = 1; i < 8; i++)
            {
                float t = i / 8f;
                points.Add(new Vector2(Mathf.Lerp(-halfWidth, halfWidth, t), top + .02f * Mathf.Sin(t * Mathf.PI)));
            }
            // 上面依次为顺时针（右侧向下、底尖、左侧向上、上沿向右），反转成逆时针
            points.Reverse();
            return points.ToArray();
        }

        private static Vector2[] Scale(Vector2[] outline, float factor, Vector2 offset)
        {
            var result = new Vector2[outline.Length];
            for (int i = 0; i < outline.Length; i++) result[i] = outline[i] * factor + offset;
            return result;
        }
    }
}
