using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    /// <summary>
    /// 骑士的待机与五个技能动作，以及训练假人的受击摇晃。
    /// 坐标在角色根空间：面朝 +Z，右手侧为 +X，单位米。每个动作都按「预备 → 出手 → 跟随 → 收势」编排。
    /// </summary>
    internal static class KnightAnimations
    {
        /// <summary>技能动作的关键时间点，生成器据此摆放时间轴事件与 Cue 区间。</summary>
        public static class Timing
        {
            public const float SlashLength = .9f, SlashHit = .36f, SlashTrailStart = .24f, SlashTrailEnd = .52f;
            public const float BoltLength = 1f, BoltRelease = .42f;
            public const float WarcryLength = 1.2f, WarcryShout = .5f;
            public const float SanctuaryLength = 2.4f, SanctuaryBegin = .55f, SanctuaryEnd = 2f;
            public const float SummonLength = 1.6f, SummonStrike = .85f;
            // Cue 区间（起, 止）
            public const float BoltChargeStart = .05f, WarcryAuraStart = .45f, SummonCircleStart = .8f, SummonCircleEnd = 1.55f;
            /// <summary>召唤点（角色根空间）：左前方，避开正前方的对手。</summary>
            public static readonly UnityEngine.Vector3 SummonPoint = new(-1.15f, 0, .95f);
        }

        private static Vector3 V(float x, float y, float z) => new(x, y, z);

        /// <summary>剑盾架势：左脚在前、盾护胸前，剑尖斜指前上方。</summary>
        public static Pose Stance()
        {
            var p = new Pose
            {
                HipsOffset = V(0, -.04f, 0),
                HandR = new LimbTarget(V(.3f, .98f, .22f), V(.12f, .4f, 1), V(.75f, 1.05f, -.35f)),
                HandL = new LimbTarget(V(.04f, 1.16f, .33f), V(.1f, .05f, 1), V(-.6f, .85f, .4f)),
                FootL = new LimbTarget(V(-.13f, .065f, .15f), V(-.12f, 0, 1), V(-.25f, .5f, 1.2f)),
                FootR = new LimbTarget(V(.16f, .065f, -.17f), V(.5f, 0, 1), V(.5f, .5f, 1)),
            };
            return p.Set("Hips", 0, -8).Set("Spine", 5).Set("Chest", 5, -8).Set("Neck", 0, 6).Set("Head", -3, 8);
        }

        public static Dictionary<string, AnimationClip> Bake(PoseRig rig)
        {
            var clips = new Dictionary<string, AnimationClip>
            {
                ["Idle"] = rig.Bake("Idle", 2.4f, true, Idle()),
                ["Slash"] = rig.Bake("Slash", Timing.SlashLength, false, Slash()),
                ["Bolt"] = rig.Bake("Bolt", Timing.BoltLength, false, Bolt()),
                ["Warcry"] = rig.Bake("Warcry", Timing.WarcryLength, false, Warcry()),
                ["Sanctuary"] = rig.Bake("Sanctuary", Timing.SanctuaryLength, false, Sanctuary()),
                ["Summon"] = rig.Bake("Summon", Timing.SummonLength, false, Summon()),
            };
            return clips;
        }

        private static List<Key> Idle()
        {
            var a = Stance();
            // 吸气：胸口抬起、重心微升，剑尖与盾随之漂移。
            var b = a.With(p =>
            {
                p.HipsOffset = V(0, -.03f, 0);
                p.Set("Spine", 3).Set("Chest", 2, -10).Set("Head", -5, 10);
                p.HandR.Position += V(.01f, .025f, .01f); p.HandR.Aim = V(.16f, .5f, 1).normalized;
                p.HandL.Position += V(-.01f, .02f, -.01f);
            });
            return new List<Key> { new(0, a), new(1.2f, b, Ease.Spline), new(2.4f, a, Ease.Spline) };
        }

        private static List<Key> Slash()
        {
            var stance = Stance();
            // 预备：向右拧身、重心后移，剑收到右后方。
            var windup = stance.With(p =>
            {
                p.HipsOffset = V(.02f, -.07f, -.05f);
                p.Set("Hips", 0, 22).Set("Spine", 2, 8).Set("Chest", -2, 26).Set("Neck", 0, -14).Set("Head", 2, -18);
                p.HandR = new LimbTarget(V(.48f, 1.3f, -.12f), V(.65f, .3f, -.7f), V(.8f, 1.35f, -.45f));
                p.HandL = new LimbTarget(V(.08f, 1.14f, .3f), V(.35f, .05f, 1), V(-.55f, .85f, .45f));
            });
            // 左脚离地前踏。
            var step = windup.With(p =>
            {
                p.HipsOffset = V(.01f, -.06f, .03f);
                p.FootL = new LimbTarget(V(-.14f, .17f, .26f), V(-.1f, 0, 1), V(-.25f, .6f, 1.2f));
                p.Set("Hips", 0, 10).Set("Chest", 0, 14);
                p.HandR = new LimbTarget(V(.5f, 1.32f, .05f), V(.85f, .25f, -.2f), V(.8f, 1.4f, -.3f));
            });
            // 命中：突进落脚，剑身横扫过正前方。
            var hit = stance.With(p =>
            {
                p.HipsOffset = V(-.02f, -.1f, .16f);
                p.FootL = new LimbTarget(V(-.15f, .065f, .4f), V(-.1f, 0, 1), V(-.25f, .5f, 1.4f));
                p.Set("Hips", 0, -14).Set("Spine", 10, -6).Set("Chest", 6, -26).Set("Neck", 0, 14).Set("Head", -2, 18);
                p.HandR = new LimbTarget(V(.04f, 1.18f, .66f), V(-.3f, .04f, 1), V(.55f, 1.0f, .05f));
                p.HandL = new LimbTarget(V(-.16f, 1.1f, .28f), V(-.45f, .05f, 1), V(-.75f, .85f, .2f));
            });
            // 跟随：剑甩到左侧，身体继续拧转。
            var follow = hit.With(p =>
            {
                p.HipsOffset = V(-.03f, -.11f, .17f);
                p.Set("Hips", 0, -24).Set("Spine", 12, -10).Set("Chest", 8, -40).Set("Neck", 0, 22).Set("Head", 0, 26);
                p.HandR = new LimbTarget(V(-.36f, 1.1f, .36f), V(-1, -.12f, .05f), V(.3f, .85f, .3f));
            });
            var settle = stance.With(p =>
            {
                p.HipsOffset = V(0, -.05f, .08f);
                p.FootL = new LimbTarget(V(-.14f, .065f, .3f), V(-.12f, 0, 1), V(-.25f, .5f, 1.3f));
                p.Set("Hips", 0, -10).Set("Chest", 5, -12);
            });
            return new List<Key>
            {
                new(0, stance), new(.2f, windup, Ease.Out), new(.29f, step, Ease.In), new(Timing.SlashHit, hit, Ease.In),
                new(.52f, follow, Ease.Out), new(.72f, settle, Ease.Smooth), new(Timing.SlashLength, stance, Ease.Smooth),
            };
        }

        private static List<Key> Bolt()
        {
            var stance = Stance();
            // 蓄能：后仰举剑向天。
            var charge = stance.With(p =>
            {
                p.HipsOffset = V(.01f, -.06f, -.06f);
                p.Set("Hips", 0, 14).Set("Spine", -4, 6).Set("Chest", -8, 18).Set("Neck", -6, -8).Set("Head", -10, -10);
                p.HandR = new LimbTarget(V(.34f, 1.7f, -.04f), V(.05f, 1, -.25f), V(.85f, 1.5f, -.3f));
                p.HandL = new LimbTarget(V(.03f, 1.15f, .32f), V(.25f, .05f, 1), V(-.6f, .85f, .4f));
            });
            // 突刺：剑尖直指前方，能量弹由剑尖射出。
            var thrust = stance.With(p =>
            {
                p.HipsOffset = V(0, -.09f, .15f);
                p.FootL = new LimbTarget(V(-.15f, .065f, .38f), V(-.1f, 0, 1), V(-.25f, .5f, 1.4f));
                p.Set("Hips", 0, 6).Set("Spine", 8, 0).Set("Chest", 6, -4).Set("Neck", 0, 0).Set("Head", -4, 0);
                p.HandR = new LimbTarget(V(.14f, 1.33f, .66f), V(-.04f, .04f, 1), V(.6f, 1.1f, .1f));
                p.HandL = new LimbTarget(V(-.18f, 1.08f, .26f), V(-.5f, 0, 1), V(-.78f, .8f, .15f));
            });
            var hold = thrust.With(p => { p.HipsOffset = V(0, -.1f, .17f); p.HandR.Position += V(0, -.01f, .03f); });
            var settle = stance.With(p => { p.FootL = thrust.FootL; p.HipsOffset = V(0, -.05f, .08f); });
            return new List<Key>
            {
                new(0, stance), new(.3f, charge, Ease.Smooth), new(Timing.BoltRelease, thrust, Ease.In),
                new(.62f, hold, Ease.Out), new(.84f, settle, Ease.Smooth), new(Timing.BoltLength, stance, Ease.Smooth),
            };
        }

        private static List<Key> Warcry()
        {
            var stance = Stance();
            // 下蹲蓄势。
            var gather = stance.With(p =>
            {
                p.HipsOffset = V(0, -.14f, -.02f);
                p.Set("Hips", 0, 0).Set("Spine", 16, 0).Set("Chest", 12, 0).Set("Neck", 8, 0).Set("Head", 12, 0);
                p.HandR = new LimbTarget(V(.32f, .82f, .26f), V(.1f, -.4f, 1), V(.8f, .9f, -.3f));
                p.HandL = new LimbTarget(V(-.04f, .98f, .3f), V(.1f, -.2f, 1), V(-.6f, .7f, .3f));
            });
            // 怒吼：挺胸仰头，剑举向天，盾向外展开。
            var shout = stance.With(p =>
            {
                p.HipsOffset = V(0, -.01f, 0);
                p.Set("Hips", 0, 0).Set("Spine", -8, 0).Set("Chest", -12, 0).Set("Neck", -10, 0).Set("Head", -18, 0);
                p.HandR = new LimbTarget(V(.3f, 1.98f, .06f), V(.02f, 1, .08f), V(.9f, 1.6f, -.2f));
                p.HandL = new LimbTarget(V(-.6f, 1.28f, .12f), V(-.35f, .1f, 1), V(-.8f, 1.0f, -.5f));
            });
            var hold = shout.With(p => { p.HipsOffset = V(0, -.02f, 0); p.Set("Chest", -10, 0); p.HandR.Position += V(0, -.02f, 0); });
            return new List<Key>
            {
                new(0, stance), new(.26f, gather, Ease.Smooth), new(Timing.WarcryShout, shout, Ease.Back),
                new(.86f, hold, Ease.Smooth), new(Timing.WarcryLength, stance, Ease.Smooth),
            };
        }

        private static List<Key> Sanctuary()
        {
            var stance = Stance();
            // 单膝跪地，剑尖插入身前地面，低头祈祷。
            var kneel = stance.With(p =>
            {
                p.HipsOffset = V(0, -.36f, .02f);
                p.FootL = new LimbTarget(V(-.15f, .065f, .3f), V(-.05f, 0, 1), V(-.2f, .6f, 1.5f));
                p.FootR = new LimbTarget(V(.15f, .09f, -.34f), V(0, -.55f, 1), V(.15f, .05f, .6f));
                p.Set("Hips", 0, 0).Set("Spine", 14, 0).Set("Chest", 10, 0).Set("Neck", 12, 0).Set("Head", 20, 0);
                p.HandR = new LimbTarget(V(.06f, .92f, .44f), V(0, -1, .06f), V(.6f, .95f, .1f));
                p.HandL = new LimbTarget(V(-.05f, .86f, .38f), V(.1f, 0, 1), V(-.6f, .7f, .35f));
            });
            var breathe = kneel.With(p => { p.HipsOffset = V(0, -.345f, .02f); p.Set("Spine", 12, 0).Set("Chest", 7, 0).Set("Head", 16, 0); p.HandL.Position += V(0, .015f, 0); });
            return new List<Key>
            {
                new(0, stance), new(Timing.SanctuaryBegin, kneel, Ease.Out), new(1.3f, breathe, Ease.Smooth),
                new(Timing.SanctuaryEnd, kneel, Ease.Smooth), new(Timing.SanctuaryLength, stance, Ease.Smooth),
            };
        }

        private static List<Key> Summon()
        {
            var stance = Stance();
            // 展臂聚能。
            var open = stance.With(p =>
            {
                p.HipsOffset = V(0, -.02f, -.03f);
                p.Set("Hips", 0, 0).Set("Spine", -6, 0).Set("Chest", -10, 0).Set("Neck", -6, 0).Set("Head", -12, 0);
                p.HandR = new LimbTarget(V(.62f, 1.62f, .12f), V(.6f, .8f, .2f), V(.8f, 1.2f, -.4f));
                p.HandL = new LimbTarget(V(-.62f, 1.5f, .14f), V(-.4f, .4f, 1), V(-.8f, 1.1f, -.4f));
            });
            // 剑指前方地面：召唤点落在身前。
            var point = stance.With(p =>
            {
                p.HipsOffset = V(0, -.12f, .12f);
                p.FootL = new LimbTarget(V(-.15f, .065f, .38f), V(-.1f, 0, 1), V(-.25f, .5f, 1.4f));
                p.Set("Hips", 0, -10).Set("Spine", 14, -6).Set("Chest", 10, -14).Set("Neck", 4, -10).Set("Head", 8, -14);
                p.HandR = new LimbTarget(V(.12f, 1.2f, .58f), V(-.72f, -.62f, .3f), V(.7f, 1.1f, .1f));
                p.HandL = new LimbTarget(V(-.1f, 1.12f, .3f), V(-.1f, 0, 1), V(-.6f, .85f, .4f));
            });
            var hold = point.With(p => { p.HipsOffset = V(0, -.13f, .13f); p.HandR.Position += V(0, -.02f, .02f); });
            var settle = stance.With(p => { p.FootL = point.FootL; p.HipsOffset = V(0, -.05f, .08f); });
            return new List<Key>
            {
                new(0, stance), new(.5f, open, Ease.Smooth), new(Timing.SummonStrike, point, Ease.In),
                new(1.15f, hold, Ease.Out), new(1.38f, settle, Ease.Smooth), new(Timing.SummonLength, stance, Ease.Smooth),
            };
        }

        /// <summary>训练假人受击：先被打得后仰，再阻尼回摆。</summary>
        public static AnimationClip DummyHit()
        {
            var clip = new AnimationClip { name = "Hit", frameRate = 30 };
            (float t, float x, float z)[] keys = { (0, 0, 0), (.06f, -16, 3), (.2f, 9, -2), (.34f, -5, 1), (.48f, 2.5f, 0), (.64f, 0, 0) };
            var curves = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
            foreach (var (t, x, z) in keys)
            {
                var q = Quaternion.Euler(x, 0, z);
                curves[0].AddKey(t, q.x); curves[1].AddKey(t, q.y); curves[2].AddKey(t, q.z); curves[3].AddKey(t, q.w);
            }
            string[] names = { "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" };
            for (int c = 0; c < 4; c++)
            {
                for (int i = 0; i < curves[c].length; i++) curves[c].SmoothTangents(i, 0);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(Transform), names[c]), curves[c]);
            }
            return clip;
        }
    }
}
