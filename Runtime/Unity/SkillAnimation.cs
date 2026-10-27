using GameplayTags;
using System;

namespace ZFramework.GAS.Unity
{
    /// <summary>标记点上的效果作用于谁。</summary>
    public enum AnimationMarkerTarget
    {
        /// <summary>施法时传入的固定目标。</summary>
        Target,
        /// <summary>施法者自己（增益、自伤、蓄力标记等）。</summary>
        Self,
    }

    /// <summary>
    /// 时间轴上的一个时间点。
    ///
    /// <see cref="Effect"/> 为 null 时是纯回调标记：作用对象由使用方在回调里决定
    /// （扇形查询、多目标、生成投射物…），库不代为猜测。
    /// </summary>
    public readonly struct AnimationMarker
    {
        /// <summary>相对<b>授权时长</b>的秒数；运行期按实际时长等比缩放。</summary>
        public readonly float Time;

        /// <summary>
        /// 授权侧给这个时间点起的名字，回调据此分辨是哪一下（<c>"命中"</c> / <c>"发射"</c>…）。
        /// 用名字而非下标：时间轴上拖动会改变先后顺序，下标不稳定。可为空。
        /// </summary>
        public readonly string Key;

        /// <summary>到点施加的效果；可为 null。</summary>
        public readonly GameplayEffectDefinition Effect;

        public readonly AnimationMarkerTarget Target;

        public AnimationMarker(float time, string key = null, GameplayEffectDefinition effect = null,
            AnimationMarkerTarget target = AnimationMarkerTarget.Target)
        {
            if (time < 0f || float.IsNaN(time))
            {
                throw new ArgumentOutOfRangeException(nameof(time), time, "Marker time must be a non-negative number.");
            }

            Time = time;
            Key = string.IsNullOrEmpty(key) ? null : key;
            Effect = effect;
            Target = target;
        }
    }

    /// <summary>
    /// 时间轴上的一个区间，映射到一对 Cue 事件：开始派发
    /// <see cref="GameplayCueEvent.OnActive"/>，结束派发 <see cref="GameplayCueEvent.Removed"/>。
    /// 不引入新概念——持续特效、蓄力音效都是这一对时机。
    /// </summary>
    public readonly struct AnimationCueRegion
    {
        public readonly float Start;

        /// <summary>小于 0 表示随动画结束。</summary>
        public readonly float End;

        public readonly GameplayTag CueTag;

        public AnimationCueRegion(float start, float end, GameplayTag cueTag)
        {
            if (start < 0f || float.IsNaN(start))
            {
                throw new ArgumentOutOfRangeException(nameof(start), start, "Region start must be a non-negative number.");
            }

            if (float.IsNaN(end) || (end >= 0f && end < start))
            {
                throw new ArgumentOutOfRangeException(nameof(end), end,
                    "Region end must not precede its start; use a negative value to end with the animation.");
            }

            if (!cueTag.IsValid)
            {
                throw new ArgumentException("Region cue tag is not valid.", nameof(cueTag));
            }

            Start = start;
            End = end < 0f ? -1f : end;
            CueTag = cueTag;
        }
    }

    /// <summary>
    /// 一段技能动画的不可变运行期定义：播哪个动画、多长、是否循环，
    /// 以及时间轴上的标记点与 Cue 区间。
    ///
    /// <b>时间单位一律是秒</b>。「第几帧」只是编辑器的显示方式，
    /// 不存在任何硬编码帧率参与运行期计算。
    ///
    /// <see cref="Duration"/> 是<b>授权时长</b>，也就是时间轴的长度。运行期实际时长
    /// （驱动的自然时长，或施法时下达的覆盖值）与它的比值就是标记点的缩放系数——
    /// 因此攻击动画被攻速拉快时，命中点仍落在同一个相对位置上。
    /// </summary>
    public sealed class SkillAnimationDefinition
    {
        private static readonly AnimationMarker[] NoMarkers = Array.Empty<AnimationMarker>();
        private static readonly AnimationCueRegion[] NoRegions = Array.Empty<AnimationCueRegion>();

        public string ClipName { get; }

        /// <summary>授权时长（秒），必须为正——它同时是时间轴长度与标记点缩放的分母。</summary>
        public float Duration { get; }

        public bool Loop { get; }

        /// <summary>按时间升序，构造时排好；运行期只按游标前进，不再排序。</summary>
        public AnimationMarker[] Markers { get; }

        /// <summary>按开始时间升序。</summary>
        public AnimationCueRegion[] CueRegions { get; }
        public AnimationTagWindow[] TagWindows { get; }
        public AnimationTaskEvent[] TaskEvents { get; }

        /// <summary>是否存在「作用于固定目标」的效果标记点——任务据此在构造时就要求传入目标。</summary>
        public bool RequiresTarget { get; }

        public SkillAnimationDefinition(
            string clipName,
            float duration,
            bool loop = false,
            AnimationMarker[] markers = null,
            AnimationCueRegion[] cueRegions = null,
            AnimationTagWindow[] tagWindows = null,
            AnimationTaskEvent[] taskEvents = null)
        {
            if (string.IsNullOrEmpty(clipName))
            {
                throw new ArgumentException("Clip name must not be empty.", nameof(clipName));
            }

            if (!(duration > 0f) || float.IsInfinity(duration))
            {
                throw new ArgumentOutOfRangeException(nameof(duration), duration, "Animation duration must be positive.");
            }

            ClipName = clipName;
            Duration = duration;
            Loop = loop;

            Markers = markers == null || markers.Length == 0 ? NoMarkers : (AnimationMarker[])markers.Clone();
            CueRegions = cueRegions == null || cueRegions.Length == 0 ? NoRegions : (AnimationCueRegion[])cueRegions.Clone();
            TagWindows = tagWindows == null ? Array.Empty<AnimationTagWindow>() : (AnimationTagWindow[])tagWindows.Clone();
            TaskEvents = taskEvents == null ? Array.Empty<AnimationTaskEvent>() : (AnimationTaskEvent[])taskEvents.Clone();
            foreach (var window in TagWindows)
                if (window.Start > duration || window.End > duration) throw new ArgumentOutOfRangeException(nameof(tagWindows));
            foreach (var task in TaskEvents)
                if (task.Time > duration || task.Spawner == null) throw new ArgumentOutOfRangeException(nameof(taskEvents));

            bool requiresTarget = false;
            for (int i = 0; i < Markers.Length; i++)
            {
                // 落在时长之外的标记点永远不会触发，是配置错误而非「暂时禁用」——立刻暴露。
                if (Markers[i].Time > duration)
                {
                    throw new ArgumentOutOfRangeException(nameof(markers), Markers[i].Time,
                        $"Marker at {Markers[i].Time}s lies beyond the animation duration {duration}s.");
                }

                if (Markers[i].Effect != null && Markers[i].Target == AnimationMarkerTarget.Target)
                {
                    requiresTarget = true;
                }

                // 重名的标记点会让回调分不清是哪一下，属于授权错误。
                for (int j = i + 1; j < Markers.Length; j++)
                {
                    if (Markers[i].Key != null && string.Equals(Markers[i].Key, Markers[j].Key, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            $"Two markers share the key \"{Markers[i].Key}\"; marker keys must be unique.", nameof(markers));
                    }
                }
            }

            RequiresTarget = requiresTarget;

            for (int i = 0; i < CueRegions.Length; i++)
            {
                ref readonly AnimationCueRegion region = ref CueRegions[i];
                if (region.Start > duration || region.End > duration)
                {
                    throw new ArgumentOutOfRangeException(nameof(cueRegions), duration,
                        $"Cue region [{region.Start}, {region.End}] lies beyond the animation duration {duration}s.");
                }
            }

            // 授权侧不必按时间填写（时间轴上拖动只改时间、不动下标，索引才稳定）；
            // 排序在这里做一次，运行期就只剩游标前进。
            Array.Sort(Markers, static (a, b) => a.Time.CompareTo(b.Time));
            Array.Sort(CueRegions, static (a, b) => a.Start.CompareTo(b.Start));
            Array.Sort(TagWindows, static (a, b) => a.Start.CompareTo(b.Start));
            Array.Sort(TaskEvents, static (a, b) => a.Time.CompareTo(b.Time));
        }
    }
}
