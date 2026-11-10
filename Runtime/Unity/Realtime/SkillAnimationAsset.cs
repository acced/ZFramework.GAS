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

        public AnimationCueRegion Build() => new(start, end, cueTag.Resolve());
    }

    /// <summary>
    /// 一段技能动画的授权资产：播哪个片段、多长、时间轴上有哪些时间点与区间。
    ///
    /// 与另外两个授权资产同构，<see cref="Definition"/> 缓存并复用同一实例。
    ///
    /// 编辑器按元素稳定 ID 绑定端口，数组可以安全排序；运行期在定义构造时排序一次。
    /// </summary>
    [CreateAssetMenu(menuName = "ZFramework GAS/Skill Animation", fileName = "NewSkillAnimation")]
    public sealed class SkillAnimationAsset : ScriptableObject, IDefinitionAsset
    {
        public SkillHitWindow[] hitWindows = Array.Empty<SkillHitWindow>();
        [SerializeReference] public SkillRuntimeTrack[] runtimeTracks = Array.Empty<SkillRuntimeTrack>();
        [Header("片段")]
        [Tooltip("动画资源，由角色绑定的驱动播放。留空时仅执行技能时间轴。")]
        [SerializeField] private UnityEngine.Object clip;
        [Tooltip("授权时长（秒），也是时间轴的长度")]
        [SerializeField] private float duration = 1f;
        [SerializeField] private bool loop;

        [Tooltip("从当前姿势线性过渡到技能动画的秒数；0 为硬切")]
        [Min(0)] [SerializeField] private float blendIn = .12f;
        [Tooltip("结束或被打断后过渡回施放前动画的秒数；0 为立即移除技能动画叠加")]
        [Min(0)] [SerializeField] private float blendOut = .18f;

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
        public UnityEngine.Object Clip => clip;
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

            var builtRegions = new AnimationCueRegion[cueRegions.Length];
            for (int i = 0; i < cueRegions.Length; i++) builtRegions[i] = cueRegions[i].Build();

            var builtWindows = new AnimationTagWindow[tagWindows.Length];
            for (int i = 0; i < tagWindows.Length; i++) builtWindows[i] = tagWindows[i].Build();
            var tracks = new RuntimeTrackDefinition[runtimeTracks.Length + hitWindows.Length];
            for (int i = 0; i < runtimeTracks.Length; i++) tracks[i] = runtimeTracks[i].Build();
            for (int i = 0; i < hitWindows.Length; i++) tracks[runtimeTracks.Length + i] = hitWindows[i].Build(actions);
            var definition = new SkillAnimationDefinition(clip, duration, loop, builtMarkers, builtRegions, builtWindows,
                blendIn, blendOut, tracks);
            DefinitionSource.Register(definition, this);
            return definition;
        }

        /// <summary>编辑期修改后清掉缓存，避免继续用旧定义。</summary>
        public void InvalidateDefinition() { _definition = null; DefinitionSource.InvalidateDependents(this); }
        private void OnValidate() => InvalidateDefinition();
    }
}
