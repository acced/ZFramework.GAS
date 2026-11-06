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
        [SerializeField, HideInInspector] private string id;
        public string Id => id;
        [Tooltip("相对授权时长的秒数")]
        [SerializeField] private float time;
        [Tooltip("回调据此分辨是哪一下，可留空")]
        [SerializeField] private string key;

        [SerializeReference] public SkillAction[] actions = Array.Empty<SkillAction>();
        public AnimationMarker Build(ScriptableObject owner = null) => Build(owner,new SkillActionBuildContext(owner));
        internal AnimationMarker Build(ScriptableObject owner,SkillActionBuildContext builder)
        {
            return new(time, key, id, builder.Build(actions));
        }
    }

    /// <summary>Cue 区间的可序列化镜像。</summary>
    [Serializable]
    public sealed class AnimationCueRegionAsset
    {
        [SerializeField, HideInInspector] private string id;
        public string Id => id;
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
    /// 编辑器按元素稳定 ID 绑定端口，数组可以安全排序；运行期在定义构造时排序一次。
    /// </summary>
    [CreateAssetMenu(menuName = "ZFramework GAS/Skill Animation", fileName = "NewSkillAnimation")]
    public sealed class SkillAnimationAsset : ScriptableObject
    {
        public AnimationSection[] sections = Array.Empty<AnimationSection>();
        [SerializeReference] public SkillRuntimeTrack[] runtimeTracks = Array.Empty<SkillRuntimeTrack>();
        [Header("片段")]
        [Tooltip("交给驱动的片段名：旧版 Animation 的片段名 / Animator 的状态名 / 序列帧的序列名")]
        [SerializeField] private string clipName;
        [Tooltip("授权时长（秒），也是时间轴的长度")]
        [SerializeField] private float duration = 1f;
        [SerializeField] private bool loop;

        [Tooltip("从施放前的动画过渡进来的秒数（时间轴时间，随攻速缩放）；0 为硬切")]
        [Min(0)] [SerializeField] private float blendIn = .12f;
        [Tooltip("结束或被打断后过渡回施放前动画的秒数；0 表示驱动不切换状态，交给 Animator 自己的转换")]
        [Min(0)] [SerializeField] private float blendOut = .18f;

        [SerializeField,BlendPreset(nameof(blendInCustom))] private SkillBlendCurve blendInCurve;
        [SerializeField,BlendPreset(nameof(blendOutCustom))] private SkillBlendCurve blendOutCurve;
        [SerializeField] private SkillBlendMode blendInMode, blendOutMode;
        [SerializeField,HideInInspector] private AnimationCurve blendInCustom = AnimationCurve.Linear(0,0,1,1), blendOutCustom = AnimationCurve.Linear(0,0,1,1);
        public AvatarMask mask;
        public BoneWeightMask boneWeights;
        public bool additive;
        [Range(0, 1)] public float layerWeight = 1;
        public SkillBlend BlendInSpec => new SkillBlend(blendIn, blendInCurve, blendInMode, blendInCustom);
        public SkillBlend BlendOutSpec => new SkillBlend(blendOut, blendOutCurve, blendOutMode, blendOutCustom);

        [Tooltip("仅供时间轴把秒显示成帧，不参与任何运行期计算")]
        [SerializeField] private float frameRate = 30f;

        [Header("时间轴")]
        [SerializeField] private AnimationMarkerAsset[] markers = Array.Empty<AnimationMarkerAsset>();
        [SerializeField] private AnimationCueRegionAsset[] cueRegions = Array.Empty<AnimationCueRegionAsset>();
        [SerializeField] private AnimationTagWindowAsset[] tagWindows = Array.Empty<AnimationTagWindowAsset>();

        [NonSerialized] private SkillAnimationDefinition _definition;

        /// <summary>授权时长（秒）。时间轴面板据此换算像素。</summary>
        public float Duration => duration;

        /// <summary>时间轴显示用的帧率；运行期不使用。</summary>
        public float FrameRate => frameRate;
        public System.Collections.Generic.IReadOnlyList<AnimationMarkerAsset> Markers => markers;
        public string ClipName => clipName;
        public bool Loop => loop;
        public float BlendIn => blendIn;
        public float BlendOut => blendOut;

        /// <summary>构建（并缓存）运行期定义。同一资产始终返回同一实例。</summary>
        public SkillAnimationDefinition Definition => _definition ??= Build();

        private SkillAnimationDefinition Build()
        {
            var builtMarkers = new AnimationMarker[markers.Length];
            var actions=new SkillActionBuildContext(this,new HashSet<string>());
            for (int i = 0; i < markers.Length; i++)
            {
                builtMarkers[i] = markers[i].Build(this,actions);
            }

            var builtRegions = new List<AnimationCueRegion>(cueRegions.Length);
            for (int i = 0; i < cueRegions.Length; i++)
            {
                AnimationCueRegion? region = cueRegions[i].Build(this);
                if (region.HasValue)
                    builtRegions.Add(region.Value);
            }

            var builtWindows = new AnimationTagWindow[tagWindows.Length];
            for (int i = 0; i < tagWindows.Length; i++) builtWindows[i] = tagWindows[i].Build();
            DefinitionSource.DependsOn(this,boneWeights);
            var definition = new SkillAnimationDefinition(clipName, duration, loop, builtMarkers, builtRegions.ToArray(), builtWindows,
                blendIn, blendOut, Array.ConvertAll(sections, section => section.Build()),
                Array.ConvertAll(runtimeTracks, track => track.Build()), BlendInSpec, BlendOutSpec, mask, additive, layerWeight, boneWeights?.Build());
            DefinitionSource.Register(definition, this);
            return definition;
        }

        /// <summary>编辑期修改后清掉缓存，避免继续用旧定义。</summary>
        public void InvalidateDefinition() { _definition = null; DefinitionSource.InvalidateDependents(this); }
        private void OnValidate() => InvalidateDefinition();
    }
}
