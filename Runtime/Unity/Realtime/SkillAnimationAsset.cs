using GameplayTags;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [Serializable]
    public sealed class AnimationMarkerAsset
    {
        [SerializeField, HideInInspector] private string id;
        public string Id => id;
        [SerializeField] private float time;
        [SerializeField] private string key;
        [SerializeReference] public SkillAction[] actions = Array.Empty<SkillAction>();

        public AnimationMarker Build(ScriptableObject owner = null) => Build(owner, new SkillActionBuildContext(owner));
        internal AnimationMarker Build(ScriptableObject owner, SkillActionBuildContext builder) => new(time, key, id, builder.Build(actions));
    }

    [Serializable]
    public sealed class AnimationCueRegionAsset
    {
        [SerializeField, HideInInspector] private string id;
        public string Id => id;
        [SerializeField] private float start;
        [SerializeField] private float end = -1f;
        [SerializeField] private GameplayTag cueTag;

        public AnimationCueRegion Build() => new(start, end, cueTag.Resolve());
    }

    [CreateAssetMenu(menuName = "ZFramework GAS/Skill Animation", fileName = "NewSkillAnimation")]
    public sealed class SkillAnimationAsset : ScriptableObject, IDefinitionAsset
    {
        public SkillHitWindow[] hitWindows = Array.Empty<SkillHitWindow>();
        [SerializeReference] public SkillRuntimeTrack[] runtimeTracks = Array.Empty<SkillRuntimeTrack>();

        [Header("片段")]
        [SerializeField] private UnityEngine.Object clip;
        [SerializeField] private float duration = 1f;
        [SerializeField] private bool loop;
        [Min(0), SerializeField] private float blendIn = .12f;
        [Min(0), SerializeField] private float blendOut = .18f;
        [SerializeField] private float frameRate = 30f;

        [Header("时间轴")]
        [SerializeField] private AnimationMarkerAsset[] markers = Array.Empty<AnimationMarkerAsset>();
        [SerializeField] private AnimationCueRegionAsset[] cueRegions = Array.Empty<AnimationCueRegionAsset>();
        [SerializeField] private AnimationTagWindowAsset[] tagWindows = Array.Empty<AnimationTagWindowAsset>();

        [NonSerialized] private SkillAnimationDefinition _definition;

        public float Duration => duration;
        public float FrameRate => frameRate;
        public IReadOnlyList<AnimationMarkerAsset> Markers => markers;
        public UnityEngine.Object Clip => clip;
        public bool Loop => loop;
        public float BlendIn => blendIn;
        public float BlendOut => blendOut;
        public SkillAnimationDefinition Definition => _definition ??= Build();

        private SkillAnimationDefinition Build()
        {
            var actions = new SkillActionBuildContext(this, new HashSet<string>());
            var builtMarkers = markers.Length == 0 ? Array.Empty<AnimationMarker>() : new AnimationMarker[markers.Length];
            for (int i = 0; i < markers.Length; i++) builtMarkers[i] = markers[i].Build(this, actions);

            var builtRegions = cueRegions.Length == 0 ? Array.Empty<AnimationCueRegion>() : new AnimationCueRegion[cueRegions.Length];
            for (int i = 0; i < cueRegions.Length; i++) builtRegions[i] = cueRegions[i].Build();

            var builtWindows = tagWindows.Length == 0 ? Array.Empty<AnimationTagWindow>() : new AnimationTagWindow[tagWindows.Length];
            for (int i = 0; i < tagWindows.Length; i++) builtWindows[i] = tagWindows[i].Build();

            var tracks = runtimeTracks.Length + hitWindows.Length == 0
                ? Array.Empty<RuntimeTrackDefinition>()
                : new RuntimeTrackDefinition[runtimeTracks.Length + hitWindows.Length];
            for (int i = 0; i < runtimeTracks.Length; i++)
                tracks[i] = (runtimeTracks[i] ?? throw new InvalidOperationException(name + ": 扩展轨道包含空项。")).Build();
            for (int i = 0; i < hitWindows.Length; i++) tracks[runtimeTracks.Length + i] = hitWindows[i].Build(actions);

            var def = new SkillAnimationDefinition(clip, duration, loop, builtMarkers, builtRegions, builtWindows, blendIn, blendOut, tracks);
            DefinitionSource.Register(def, this);
            return def;
        }

        public void InvalidateDefinition() { _definition = null; DefinitionSource.InvalidateDependents(this); }
        private void OnValidate() => InvalidateDefinition();
    }
}
