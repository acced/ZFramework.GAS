using UnityEngine;
using UnityEditor;
using ZFramework.GAS.Unity;
using T = ZFramework.GAS.Samples.BasicCombat.Editor.KnightAnimations.Timing;

namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    /// <summary>
    /// 生成独立特效 Prefab 与共享 Cue 映射库，由通用播放器和预览读取。
    /// 曲线时间以「区间开始后的秒数」计，与时间轴上的区间长度对应。
    /// </summary>
    internal static class SampleVfx
    {
        public static CueVfxLibrary Build(SampleArt art, GameObject knight, AnimationClip slash, string folder)
        {
            var glowTexture = art.RadialTexture("Vfx_Glow", 128);
            var ringTexture = art.RadialTexture("Vfx_Ring", 256, .78f, .07f, 0);
            var circleTexture = art.MagicCircleTexture("Vfx_MagicCircle", 512);
            var columnTexture = art.ColumnTexture("Vfx_Column", 128, 128);
            var streakTexture = art.StreakTexture("Vfx_Streak", 128, 64);

            var gold = art.Additive("Vfx_Gold", ringTexture, "#ffffff");
            var trail = art.Additive("Vfx_Trail", streakTexture, "#ffffff");
            var glow = art.Additive("Vfx_Glow", glowTexture, "#ffffff");
            var circle = art.Additive("Vfx_Circle", circleTexture, "#ffffff");
            var column = art.Additive("Vfx_Column", columnTexture, "#ffffff");

            var root = knight.transform;
            var cross = art.Save(MeshKit.GlowCross("Vfx.GlowCross", 1));
            var quad = art.Save(MeshKit.Quad("Vfx.Quad", 1));
            var smallRing = art.Save(MeshKit.FlatRing("Vfx.SmallRing", .9f, 1f, 48));
            Mesh Column(string name, float radius, float height) => art.Save(MeshKit.Lathe(name, new[] { new Vector2(radius, 0), new Vector2(radius * .92f, height) }, 40, 1, 1, false, false));

            // ── 剑光：烘焙斩击动画中剑身两端的轨迹 ──
            var trailGo = Holder(root, "VFX_SwordTrail", Vector3.zero);
            trailGo.AddComponent<MeshFilter>();
            trailGo.AddComponent<MeshRenderer>().sharedMaterial = trail;
            var visual = trailGo.AddComponent<SwordTrailVisual>();
            Transform tip = Find(root, "SwordTip"), blade = Find(root, "SwordBase");
            const float rate = 60;
            int samples = Mathf.CeilToInt((T.SlashTrailEnd - T.SlashTrailStart) * rate) + 2;
            var bases = new Vector3[samples]; var tips = new Vector3[samples];
            for (int i = 0; i < samples; i++)
            {
                slash.SampleAnimation(knight, T.SlashTrailStart + i / rate);
                bases[i] = root.InverseTransformPoint(blade.position); tips[i] = root.InverseTransformPoint(tip.position);
            }
            visual.Configure(AnimationUtility.CalculateTransformPath(blade, root), AnimationUtility.CalculateTransformPath(tip, root),
                new Color(1f, .78f, .42f, .85f), .16f, rate, bases, tips);

            // ── 能量弹蓄能：剑尖光球，释放前骤亮 ──
            float charge = T.BoltRelease - T.BoltChargeStart;
            var orb = Holder(tip, "VFX_BoltCharge", Vector3.zero);
            var orbGlow = Child(orb.transform, "Glow", cross, glow, Vector3.zero, Vector3.one * .7f);
            var orbRing = Child(orb.transform, "Ring", smallRing, glow, Vector3.zero, Vector3.one * .22f);
            Pulse(orb, "#4fe3ff", new[] { orbGlow, orbRing },
                Curve((0, .15f), (charge * .7f, .85f), (charge - .03f, 1.35f), (charge, .4f)),
                Curve((0, 0), (.08f, .9f), (charge - .03f, 1), (charge, 0)), 260, .12f);

            // ── 战吼：脚下扩散的金环 + 金色光柱 ──
            float aura = T.WarcryLength - T.WarcryAuraStart;
            var auraRing = Holder(root, "VFX_WarcryRing", new Vector3(0, .02f, 0));
            Pulse(auraRing, "#ffb347", new[] { Child(auraRing.transform, "Ring", quad, gold, Vector3.zero, Vector3.one * 2.6f) },
                Curve((0, .25f), (.35f, 1.15f), (aura, 1.4f)), Curve((0, 0), (.06f, 1), (.4f, .7f), (aura, 0)), 0, 0);
            var auraColumn = Holder(root, "VFX_WarcryColumn", Vector3.zero);
            Pulse(auraColumn, "#ffa940", new[] { Child(auraColumn.transform, "Column", Column("Vfx.AuraColumn", .55f, 2.1f), column, Vector3.zero, Vector3.one) },
                Curve((0, .7f), (.2f, 1.05f), (aura, 1)), Curve((0, 0), (.1f, .9f), (aura * .7f, .55f), (aura, 0)), 90, .35f);

            // ── 圣域：脚下缓转的青色法阵 + 呼吸光柱 ──
            float sanctuary = T.SanctuaryEnd - T.SanctuaryBegin;
            var sanctuaryCircle = Holder(root, "VFX_SanctuaryCircle", new Vector3(0, .015f, 0));
            Pulse(sanctuaryCircle, "#5fe8ff", new[] { Child(sanctuaryCircle.transform, "Circle", quad, circle, Vector3.zero, Vector3.one * 2.8f) },
                Curve((0, .4f), (.3f, 1.04f), (.45f, 1f), (sanctuary, 1f)), Curve((0, 0), (.25f, 1), (sanctuary - .25f, 1), (sanctuary, 0)), 24, .8f);
            var sanctuaryColumn = Holder(root, "VFX_SanctuaryColumn", Vector3.zero);
            Pulse(sanctuaryColumn, "#58d8ff", new[] { Child(sanctuaryColumn.transform, "Column", Column("Vfx.SanctuaryColumn", 1.15f, 1.5f), column, Vector3.zero, Vector3.one) },
                Curve((0, 1), (sanctuary, 1)), Curve((0, 0), (.4f, .55f), (sanctuary - .3f, .55f), (sanctuary, 0)), -40, .8f);

            // ── 召唤：身前紫色法阵 ──
            float summon = T.SummonCircleEnd - T.SummonCircleStart;
            var summonCircle = Holder(root, "VFX_SummonCircle", T.SummonPoint + new Vector3(0, .015f, 0));
            Pulse(summonCircle, "#c46bff", new[] { Child(summonCircle.transform, "Circle", quad, circle, Vector3.zero, Vector3.one * 1.9f) },
                Curve((0, 0), (.14f, 1.12f), (.24f, 1f), (summon, 1f)), Curve((0, 0), (.08f, 1), (summon - .2f, 1), (summon, 0)), -60, .3f);
            var summonColumn = Holder(root, "VFX_SummonColumn", T.SummonPoint);
            Pulse(summonColumn, "#b05cff", new[] { Child(summonColumn.transform, "Column", Column("Vfx.SummonColumn", .62f, 1.8f), column, Vector3.zero, Vector3.one) },
                Curve((0, .2f), (.18f, 1f), (summon, 1.05f)), Curve((0, 0), (.12f, 1), (summon * .6f, .6f), (summon, 0)), 120, 0);

            var ground = BuildGroundParticles(root, circle, glow);
            SampleArt.EnsureFolder(folder + "/VFX");
            var library = ScriptableObject.CreateInstance<CueVfxLibrary>();
            CueVfxBinding Export(GameObject go, string tag, bool follow = true, CueVfxStopMode stop = CueVfxStopMode.Immediate)
            {
                var binding = new CueVfxBinding
                {
                    cueTag = new GameplayTags.GameplayTag { m_Name = tag },
                    anchorPath = AnimationUtility.CalculateTransformPath(go.transform.parent, root),
                    follow = follow, position = go.transform.localPosition, stopMode = stop, tailSeconds = 1.5f
                };
                go.transform.SetParent(null, false); go.transform.localPosition = Vector3.zero;
                binding.prefab = PrefabUtility.SaveAsPrefabAsset(go, folder + "/VFX/" + go.name + ".prefab");
                Object.DestroyImmediate(go);
                // 编辑器资产仅需保存标签名，不依赖本次域是否已重载生成的注册表。
                return binding;
            }
            library.bindings = new[]
            {
                Export(trailGo, SampleTags.CueSwordTrail), Export(orb, SampleTags.CueBoltCharge),
                Export(auraRing, SampleTags.CueWarcryAura), Export(auraColumn, SampleTags.CueWarcryAura),
                Export(sanctuaryCircle, SampleTags.CueSanctuary, false), Export(sanctuaryColumn, SampleTags.CueSanctuary, false),
                Export(summonCircle, SampleTags.CueSummon, false), Export(summonColumn, SampleTags.CueSummon, false),
                Export(ground, SampleTags.CueGroundTest, false, CueVfxStopMode.StopEmitting)
            };
            AssetDatabase.CreateAsset(library, folder + "/CueVfxLibrary.asset");
            return library;
        }

        // 普通粒子预制体：无需 CueVfxBehaviour 或每标签预览类，用于验证配置接入。
        private static GameObject BuildGroundParticles(Transform root, Material circle, Material glow)
        {
            var ground = Holder(root, "VFX_GroundSigil", new Vector3(-1.3f, .025f, 0));
            var ring = ground.AddComponent<ParticleSystem>();
            var main = ring.main; main.playOnAwake = false; main.duration = .7f; main.loop = true;
            main.startLifetime = .7f; main.startSpeed = 0; main.startSize = 2.2f; main.startColor = new Color(.3f, 1, .65f, .8f);
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            var shape = ring.shape; shape.enabled = false;
            var emission = ring.emission; emission.rateOverTime = 0; emission.SetBursts(new[] { new ParticleSystem.Burst(0, 1) });
            var fade = ring.colorOverLifetime; fade.enabled = true;
            fade.color = new Gradient { colorKeys = new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                alphaKeys = new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, .15f), new GradientAlphaKey(1, .6f), new GradientAlphaKey(0, 1) } };
            var renderer = ring.GetComponent<ParticleSystemRenderer>(); renderer.sharedMaterial = circle;
            renderer.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
            var sparks = Holder(ground.transform, "Sparks", Vector3.zero).AddComponent<ParticleSystem>();
            main = sparks.main; main.playOnAwake = false; main.duration = 1; main.loop = true;
            main.startLifetime = .6f; main.startSpeed = 0; main.startSize = .06f; main.startColor = new Color(.4f, 1, .7f, .9f);
            shape = sparks.shape; shape.shapeType = ParticleSystemShapeType.Circle; shape.radius = .8f; shape.rotation = new Vector3(90, 0, 0);
            emission = sparks.emission; emission.rateOverTime = 24;
            var velocity = sparks.velocityOverLifetime; velocity.enabled = true; velocity.y = .7f;
            fade = sparks.colorOverLifetime; fade.enabled = true; fade.color = ring.colorOverLifetime.color;
            sparks.GetComponent<ParticleSystemRenderer>().sharedMaterial = glow;
            ring.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            return ground;
        }

        /// <summary>能量弹的飞行外观：发光核心、柔光与拖尾。由宿主的对象池按需取用。</summary>
        public static GameObject BuildProjectile(SampleArt art)
        {
            var root = new GameObject("ArcaneBolt");
            // 预制体不保存 MaterialPropertyBlock，颜色直接写进专用材质。
            var glow = art.Additive("Vfx_BoltGlow", art.RadialTexture("Vfx_BoltGlowTexture", 64), "#5fd8ff");
            var core = art.Opaque("Vfx_BoltCore", "#c9fbff", 0, .9f, "#3fe0ff", 4f);
            SampleArt.Part(root.transform, "Core", art.Save(MeshKit.Lathe("Vfx.BoltCore",
                new[] { new Vector2(0, -.07f), new Vector2(.05f, -.05f), new Vector2(.07f, 0), new Vector2(.05f, .05f), new Vector2(0, .07f) }, 16)), core, Vector3.zero);
            Child(root.transform, "Halo", art.Save(MeshKit.GlowCross("Vfx.BoltHalo", 1)), glow, Vector3.zero, Vector3.one * .55f);
            var trail = root.AddComponent<TrailRenderer>();
            trail.sharedMaterial = glow;
            trail.time = .22f; trail.minVertexDistance = .03f;
            trail.widthCurve = new AnimationCurve(new Keyframe(0, .22f), new Keyframe(1, 0));
            trail.colorGradient = new Gradient
            {
                colorKeys = new[] { new GradientColorKey(new Color(.55f, .95f, 1f), 0), new GradientColorKey(new Color(.2f, .5f, 1f), 1) },
                alphaKeys = new[] { new GradientAlphaKey(.9f, 0), new GradientAlphaKey(0, 1) },
            };
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return root;
        }

        private static GameObject Holder(Transform parent, string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            return go;
        }

        private static Renderer Child(Transform parent, string name, Mesh mesh, Material material, Vector3 position, Vector3 scale)
        {
            var go = SampleArt.Part(parent, name, mesh, material, position, Vector3.zero, scale);
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }

        private static void Pulse(GameObject holder, string hex, Renderer[] renderers, AnimationCurve scale, AnimationCurve alpha, float spin, float pulse)
        {
            var visual = holder.AddComponent<PulseVisual>();
            var color = SampleArt.Hex(hex); color.a = .75f;
            visual.Configure(renderers, color, scale, alpha, spin, pulse);
        }

        private static AnimationCurve Curve(params (float time, float value)[] keys)
        {
            var curve = new AnimationCurve();
            foreach (var (time, value) in keys) curve.AddKey(time, value);
            for (int i = 0; i < curve.length; i++) curve.SmoothTangents(i, 0);
            return curve;
        }

        private static Transform Find(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            throw new System.InvalidOperationException("骨骼不存在：" + name);
        }
    }

}
