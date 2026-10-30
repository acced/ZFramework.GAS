using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    /// <summary>四肢末端的 IK 目标，全部在角色根空间（面朝 +Z）中表达。</summary>
    internal struct LimbTarget
    {
        public Vector3 Position;
        /// <summary>右手：剑身方向；左手：盾面朝向；脚：脚尖朝向。</summary>
        public Vector3 Aim;
        /// <summary>肘或膝指向的一点。</summary>
        public Vector3 Pole;
        public LimbTarget(Vector3 position, Vector3 aim, Vector3 pole) { Position = position; Aim = aim.normalized; Pole = pole; }
    }

    internal enum Ease
    {
        /// <summary>两端减速。</summary>
        Smooth,
        /// <summary>加速进入：出手与劈砍，到达时速度最大。</summary>
        In,
        /// <summary>减速到达：跟随与收势。</summary>
        Out,
        /// <summary>略微越过后回落。</summary>
        Back,
        /// <summary>Catmull-Rom 经过点，前后速度连续。</summary>
        Spline,
        Linear,
    }

    /// <summary>一帧姿势：躯干的欧拉角（FK）与四肢的 IK 目标。</summary>
    internal sealed class Pose
    {
        public Vector3 HipsOffset;
        public readonly Dictionary<string, Vector3> Euler = new();
        public LimbTarget HandR, HandL, FootR, FootL;

        public Pose Clone()
        {
            var copy = new Pose { HipsOffset = HipsOffset, HandR = HandR, HandL = HandL, FootR = FootR, FootL = FootL };
            foreach (var pair in Euler) copy.Euler[pair.Key] = pair.Value;
            return copy;
        }

        public Pose With(System.Action<Pose> change) { var copy = Clone(); change(copy); return copy; }
        public Pose Set(string bone, float x, float y = 0, float z = 0) { Euler[bone] = new Vector3(x, y, z); return this; }
        public Vector3 Get(string bone) => Euler.TryGetValue(bone, out var value) ? value : Vector3.zero;
    }

    internal readonly struct Key
    {
        public readonly float Time;
        public readonly Pose Pose;
        /// <summary>从上一关键帧到达本帧所用的缓动。</summary>
        public readonly Ease Ease;
        public Key(float time, Pose pose, Ease ease = Ease.Smooth) { Time = time; Pose = pose; Ease = ease; }
    }

    /// <summary>
    /// 把关键姿势烘焙成 AnimationClip：躯干插值欧拉角，四肢用两骨 IK 解算，披风与羽饰做弹簧二次运动。
    /// 结果按 60fps 逐帧写入四元数曲线，运行期与预览都只需普通的 Generic 片段。
    /// </summary>
    internal sealed class PoseRig
    {
        private const float SampleRate = 60f;
        private static readonly string[] FkBones = { "Hips", "Spine", "Chest", "Neck", "Head" };

        private readonly Transform _root;
        private readonly Dictionary<string, Transform> _bones = new();
        private readonly Dictionary<Transform, Vector3> _bindPositions = new();
        // 网格部件自带摆放旋转（盾牌朝向、肩甲外倾等），复位时必须还原而不是清零。
        private readonly Dictionary<Transform, Quaternion> _bindRotations = new();

        public PoseRig(GameObject root)
        {
            _root = root.transform;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == _root) continue;
                _bones[t.name] = t;
                _bindPositions[t] = t.localPosition;
                _bindRotations[t] = t.localRotation;
            }
        }

        public Transform this[string name] => _bones[name];

        private static readonly string[] Animated =
        {
            "Hips", "Spine", "Chest", "Neck", "Head", "Plume", "Cape",
            "UpperArm.R", "Forearm.R", "Hand.R", "UpperArm.L", "Forearm.L", "Hand.L",
            "Thigh.R", "Shin.R", "Foot.R", "Thigh.L", "Shin.L", "Foot.L",
        };

        public AnimationClip Bake(string name, float length, bool loop, IList<Key> keys)
        {
            int frames = Mathf.Max(2, Mathf.RoundToInt(length * SampleRate) + 1);
            var rotations = new Dictionary<string, Quaternion[]>();
            foreach (var bone in Animated) if (_bones.ContainsKey(bone)) rotations[bone] = new Quaternion[frames];
            var hips = new Vector3[frames];
            var springs = new[] { new Spring("Cape", 70, 9, 24, 0, 55), new Spring("Plume", 110, 10, 28, -25, 45) };

            // 循环片段先预跑一圈，让弹簧进入稳态，首尾才能衔接。
            int warmup = loop ? frames - 1 : 0;
            for (int step = -warmup; step < frames; step++)
            {
                int frame = step < 0 ? step + frames - 1 : step;
                float time = frame / SampleRate;
                Apply(Evaluate(keys, time, length, loop));
                foreach (var spring in springs) spring.Step(this, 1f / SampleRate);
                if (step < 0) continue;
                foreach (var pair in rotations) pair.Value[frame] = _bones[pair.Key].localRotation;
                hips[frame] = _bones["Hips"].localPosition;
            }
            if (loop)
                foreach (var pair in rotations) pair.Value[frames - 1] = pair.Value[0];

            var clip = new AnimationClip { name = name, frameRate = 30 };
            foreach (var pair in rotations) WriteRotation(clip, Path(_bones[pair.Key]), pair.Value);
            WritePosition(clip, Path(_bones["Hips"]), hips);
            clip.EnsureQuaternionContinuity();
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            clip.wrapMode = loop ? WrapMode.Loop : WrapMode.Once;
            ResetToBind();
            return clip;
        }

        /// <summary>按给定时间点姿势摆好骨骼，供烘焙剑光轨迹等需要正向运动学的步骤使用。</summary>
        public void Apply(Pose pose)
        {
            ResetToBind();
            _bones["Hips"].localPosition = _bindPositions[_bones["Hips"]] + pose.HipsOffset;
            foreach (var bone in FkBones) _bones[bone].localRotation = UnityEngine.Quaternion.Euler(pose.Get(bone));
            SolveArm("R", pose.HandR, true);
            SolveArm("L", pose.HandL, false);
            SolveLeg("R", pose.FootR);
            SolveLeg("L", pose.FootL);
        }

        public void ResetToBind()
        {
            foreach (var pair in _bindPositions) { pair.Key.localPosition = pair.Value; pair.Key.localRotation = _bindRotations[pair.Key]; }
        }

        public static Pose Evaluate(IList<Key> keys, float time, float length, bool loop)
        {
            if (time <= keys[0].Time) return keys[0].Pose;
            if (time >= keys[keys.Count - 1].Time) return keys[keys.Count - 1].Pose;
            int i = 0;
            while (i < keys.Count - 2 && time > keys[i + 1].Time) i++;
            Key a = keys[i], b = keys[i + 1];
            float u = Mathf.InverseLerp(a.Time, b.Time, time);
            Pose p0 = i > 0 ? keys[i - 1].Pose : loop ? keys[keys.Count - 2].Pose : a.Pose;
            Pose p3 = i + 2 < keys.Count ? keys[i + 2].Pose : loop ? keys[1].Pose : b.Pose;
            return Blend(p0, a.Pose, b.Pose, p3, u, b.Ease);
        }

        private static float Shape(float u, Ease ease) => ease switch
        {
            Ease.Smooth => u * u * (3 - 2 * u),
            Ease.In => u * u * u,
            Ease.Out => 1 - Mathf.Pow(1 - u, 3),
            Ease.Back => 1 + 2.2f * Mathf.Pow(u - 1, 3) + 1.2f * Mathf.Pow(u - 1, 2),
            _ => u,
        };

        private static Pose Blend(Pose p0, Pose a, Pose b, Pose p3, float u, Ease ease)
        {
            bool spline = ease == Ease.Spline;
            float s = Shape(u, ease);
            Vector3 V(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3) => spline ? CatmullRom(v0, v1, v2, v3, u) : Vector3.LerpUnclamped(v1, v2, s);
            Vector3 D(Vector3 d0, Vector3 d1, Vector3 d2, Vector3 d3) => spline ? CatmullRom(d0, d1, d2, d3, u).normalized : Vector3.SlerpUnclamped(d1, d2, s).normalized;
            LimbTarget L(LimbTarget l0, LimbTarget l1, LimbTarget l2, LimbTarget l3) => new()
            {
                Position = V(l0.Position, l1.Position, l2.Position, l3.Position),
                Aim = D(l0.Aim, l1.Aim, l2.Aim, l3.Aim),
                Pole = V(l0.Pole, l1.Pole, l2.Pole, l3.Pole),
            };
            var pose = new Pose
            {
                HipsOffset = V(p0.HipsOffset, a.HipsOffset, b.HipsOffset, p3.HipsOffset),
                HandR = L(p0.HandR, a.HandR, b.HandR, p3.HandR),
                HandL = L(p0.HandL, a.HandL, b.HandL, p3.HandL),
                FootR = L(p0.FootR, a.FootR, b.FootR, p3.FootR),
                FootL = L(p0.FootL, a.FootL, b.FootL, p3.FootL),
            };
            foreach (var bone in FkBones) pose.Euler[bone] = V(p0.Get(bone), a.Get(bone), b.Get(bone), p3.Get(bone));
            return pose;
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return .5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
        }

        // ───────────── IK ─────────────

        /// <summary>两骨解析 IK，返回肘/膝与末端的世界位置。</summary>
        private static (Vector3 joint, Vector3 end) TwoBone(Vector3 origin, float upper, float lower, Vector3 target, Vector3 pole)
        {
            Vector3 toTarget = target - origin;
            float distance = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(upper - lower) + .001f, upper + lower - .0005f);
            Vector3 direction = toTarget.normalized;
            float cos = Mathf.Clamp((upper * upper + distance * distance - lower * lower) / (2 * upper * distance), -1, 1);
            Vector3 bend = Vector3.ProjectOnPlane(pole - origin, direction);
            if (bend.sqrMagnitude < 1e-8f) bend = Vector3.ProjectOnPlane(Vector3.forward, direction);
            Vector3 joint = origin + direction * (upper * cos) + bend.normalized * (upper * Mathf.Sqrt(1 - cos * cos));
            return (joint, origin + direction * distance);
        }

        private static Vector3 Perpendicular(Vector3 v, Vector3 axis, Vector3 fallback)
        {
            var p = Vector3.ProjectOnPlane(v, axis);
            return p.sqrMagnitude > 1e-6f ? p.normalized : Vector3.ProjectOnPlane(fallback, axis).normalized;
        }

        private void SolveArm(string side, LimbTarget target, bool sword)
        {
            Transform upper = _bones["UpperArm." + side], forearm = _bones["Forearm." + side], hand = _bones["Hand." + side];
            Vector3 goal = _root.TransformPoint(target.Position), aim = _root.TransformDirection(target.Aim), pole = _root.TransformPoint(target.Pole);
            var (elbow, wrist) = TwoBone(upper.position, _bindPositions[forearm].magnitude, _bindPositions[hand].magnitude, goal, pole);
            Vector3 d1 = (elbow - upper.position).normalized, d2 = (wrist - elbow).normalized;
            // 肘关节屈曲让前臂朝上臂的 +Z 一侧折，因此上臂 +Z 取前臂偏离上臂的方向。
            upper.rotation = UnityEngine.Quaternion.LookRotation(Perpendicular(d2, d1, aim), -d1);
            if (sword)
            {
                // 前臂 +Z 贴近剑身方向，扭转主要由前臂承担；手腕保持平直、剑身与前臂垂直。
                forearm.rotation = UnityEngine.Quaternion.LookRotation(Perpendicular(aim, d2, upper.forward), -d2);
                hand.rotation = UnityEngine.Quaternion.LookRotation(aim, -Perpendicular(d2, aim, -forearm.up));
            }
            else
            {
                // 盾面是前臂的 -X：+X = up × forward，故 forward = face × d2。
                Vector3 face = Perpendicular(aim, d2, upper.forward);
                forearm.rotation = UnityEngine.Quaternion.LookRotation(Vector3.Cross(face, d2), -d2);
                hand.rotation = forearm.rotation;
            }
        }

        private void SolveLeg(string side, LimbTarget target)
        {
            Transform thigh = _bones["Thigh." + side], shin = _bones["Shin." + side], foot = _bones["Foot." + side];
            Vector3 goal = _root.TransformPoint(target.Position), toe = _root.TransformDirection(target.Aim), pole = _root.TransformPoint(target.Pole);
            var (knee, ankle) = TwoBone(thigh.position, _bindPositions[shin].magnitude, _bindPositions[foot].magnitude, goal, pole);
            Vector3 d1 = (knee - thigh.position).normalized, d2 = (ankle - knee).normalized;
            // 膝关节向后折：大腿 +Z 与小腿偏离方向相反。
            thigh.rotation = UnityEngine.Quaternion.LookRotation(Perpendicular(-d2, d1, toe), -d1);
            shin.rotation = UnityEngine.Quaternion.LookRotation(Perpendicular(toe, d2, thigh.forward), -d2);
            Vector3 up = Vector3.Cross(toe, Vector3.Cross(Vector3.up, toe)).normalized;
            foot.rotation = UnityEngine.Quaternion.LookRotation(toe, up.sqrMagnitude > .5f ? up : Vector3.up);
        }

        // ───────────── 二次运动 ─────────────

        /// <summary>
        /// 绕本地 X 的阻尼弹簧：随挂点的前进速度与父骨骼倾角摆动，产生披风、羽饰的跟随与回摆。
        /// </summary>
        private sealed class Spring
        {
            private readonly string _bone;
            private readonly float _stiffness, _damping, _gain, _min, _max;
            private float _angle, _velocity;
            private Vector3 _lastPosition;
            private bool _primed;
            public Spring(string bone, float stiffness, float damping, float gain, float min, float max)
            { _bone = bone; _stiffness = stiffness; _damping = damping; _gain = gain; _min = min; _max = max; }

            public void Step(PoseRig rig, float dt)
            {
                Transform bone = rig._bones[_bone], parent = bone.parent;
                Vector3 position = bone.position;
                Vector3 velocity = _primed ? (position - _lastPosition) / dt : Vector3.zero;
                _lastPosition = position;
                Vector3 forward = Vector3.ProjectOnPlane(parent.forward, Vector3.up).normalized;
                // 父骨骼前倾时布料仍应大致下垂：目标在世界空间里给出，再换算回本地角度。
                float parentPitch = -Mathf.Asin(Mathf.Clamp(parent.forward.y, -1, 1)) * Mathf.Rad2Deg;
                float world = Mathf.Clamp(Vector3.Dot(velocity, forward) * _gain + Mathf.Abs(Vector3.Dot(velocity, Vector3.up)) * _gain * .3f, _min, _max);
                float target = world - parentPitch;
                if (!_primed) { _angle = target; _primed = true; }
                for (int i = 0; i < 4; i++)
                {
                    float h = dt / 4;
                    _velocity += (_stiffness * (target - _angle) - _damping * _velocity) * h;
                    _angle += _velocity * h;
                }
                bone.localRotation = UnityEngine.Quaternion.Euler(_angle, 0, 0);
            }
        }

        // ───────────── 写曲线 ─────────────

        private string Path(Transform bone)
        {
            var parts = new List<string>();
            for (var t = bone; t != null && t != _root; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        private static void WriteRotation(AnimationClip clip, string path, Quaternion[] values)
        {
            var curves = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
            for (int i = 0; i < values.Length; i++)
            {
                // 保持半球连续，避免插值走长路。
                if (i > 0 && UnityEngine.Quaternion.Dot(values[i - 1], values[i]) < 0) values[i] = new Quaternion(-values[i].x, -values[i].y, -values[i].z, -values[i].w);
                float t = i / SampleRate;
                curves[0].AddKey(t, values[i].x); curves[1].AddKey(t, values[i].y); curves[2].AddKey(t, values[i].z); curves[3].AddKey(t, values[i].w);
            }
            string[] names = { "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" };
            for (int c = 0; c < 4; c++) { Smooth(curves[c]); AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), names[c]), curves[c]); }
        }

        private static void WritePosition(AnimationClip clip, string path, Vector3[] values)
        {
            var curves = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
            for (int i = 0; i < values.Length; i++)
            {
                float t = i / SampleRate;
                curves[0].AddKey(t, values[i].x); curves[1].AddKey(t, values[i].y); curves[2].AddKey(t, values[i].z);
            }
            string[] names = { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z" };
            for (int c = 0; c < 3; c++) { Smooth(curves[c]); AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), names[c]), curves[c]); }
        }

        private static void Smooth(AnimationCurve curve)
        {
            for (int i = 0; i < curve.length; i++) curve.SmoothTangents(i, 0);
        }
    }
}
