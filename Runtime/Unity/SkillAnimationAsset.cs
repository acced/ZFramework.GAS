using GameplayTags;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>时间点的可序列化镜像。运行期类型 <see cref="AnimationMarker"/> 是 readonly struct。</summary>
    [Serializable]
    public sealed class AnimationMarkerAsset
    {
        [Tooltip("相对授权时长的秒数")]
        [SerializeField] private float time;
        [Tooltip("回调据此分辨是哪一下，可留空")]
        [SerializeField] private string key;
        [SerializeField] private GameplayEffectAsset effect;
        [SerializeField] private AnimationMarkerTarget target = AnimationMarkerTarget.Target;

        public AnimationMarker Build()
            => new(time, key, effect != null ? effect.Definition : null, target);
    }

    /// <summary>Cue 区间的可序列化镜像。</summary>
    [Serializable]
    public sealed class AnimationCueRegionAsset
    {
        [SerializeField] private float start;
        [Tooltip("负数表示随动画结束")]
        [SerializeField] private float end = -1f;
        [SerializeField] private GameplayTag cueTag;

        /// <summary>在加载边界解析 Cue 标签；空或未注册的标签跳过并告警。</summary>
        public AnimationCueRegion? Build(SkillAnimationAsset owner)
        {
            GameplayTag tag = cueTag.Resolve();
            if (!tag.IsValid)
            {
                Debug.LogWarning($"[{owner.name}] A cue region has an empty or unregistered cue tag '{cueTag.Name}'; it was skipped.", owner);
                return null;
            }

            return new AnimationCueRegion(start, end, tag);
        }
    }

    /// <summary>
    /// 一段技能动画的授权资产：播哪个片段、多长、时间轴上有哪些时间点与区间。
    ///
    /// 与另外两个授权资产同构，<see cref="Definition"/> 缓存并复用同一实例。
    ///
    /// <b>授权数组不按时间排序</b>：时间轴上拖动只改 <c>time</c>，下标保持稳定，
    /// 图上的端口才不会在拖动时错位。排序在 <see cref="SkillAnimationDefinition"/> 构造时做一次。
    /// </summary>
    [CreateAssetMenu(menuName = "ZFramework GAS/Skill Animation", fileName = "NewSkillAnimation")]
    public sealed class SkillAnimationAsset : ScriptableObject
    {
        [Header("片段")]
        [Tooltip("交给驱动的片段名：旧版 Animation 的片段名 / Animator 的状态名 / 序列帧的序列名")]
        [SerializeField] private string clipName;
        [Tooltip("授权时长（秒），也是时间轴的长度")]
        [SerializeField] private float duration = 1f;
        [SerializeField] private bool loop;

        [Tooltip("仅供时间轴把秒显示成帧，不参与任何运行期计算")]
        [SerializeField] private float frameRate = 30f;

        [Header("时间轴")]
        [SerializeField] private AnimationMarkerAsset[] markers = Array.Empty<AnimationMarkerAsset>();
        [SerializeField] private AnimationCueRegionAsset[] cueRegions = Array.Empty<AnimationCueRegionAsset>();

        [NonSerialized] private SkillAnimationDefinition _definition;

        /// <summary>授权时长（秒）。时间轴面板据此换算像素。</summary>
        public float Duration => duration;

        /// <summary>时间轴显示用的帧率；运行期不使用。</summary>
        public float FrameRate => frameRate;

        /// <summary>构建（并缓存）运行期定义。同一资产始终返回同一实例。</summary>
        public SkillAnimationDefinition Definition => _definition ??= Build();

        private SkillAnimationDefinition Build()
        {
            var builtMarkers = new AnimationMarker[markers.Length];
            for (int i = 0; i < markers.Length; i++)
            {
                builtMarkers[i] = markers[i].Build();
            }

            var builtRegions = new List<AnimationCueRegion>(cueRegions.Length);
            for (int i = 0; i < cueRegions.Length; i++)
            {
                AnimationCueRegion? region = cueRegions[i].Build(this);
                if (region.HasValue)
                    builtRegions.Add(region.Value);
            }

            return new SkillAnimationDefinition(clipName, duration, loop, builtMarkers, builtRegions.ToArray());
        }

        /// <summary>编辑期修改后清掉缓存，避免继续用旧定义。</summary>
        private void OnValidate() => _definition = null;
    }
}
