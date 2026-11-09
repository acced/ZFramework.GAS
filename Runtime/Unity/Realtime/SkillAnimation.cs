using GameplayTags;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    public readonly struct AnimationMarker
    {
        public readonly float Time;
        public readonly string Key, Id;
        public readonly IReadOnlyList<SkillActionDefinition> Actions;
        public AnimationMarker(float time, string key = null, string id = null, SkillActionDefinition[] actions = null)
        {
            Time = time; Key = key; Id = id;
            Actions = Array.AsReadOnly(actions == null ? Array.Empty<SkillActionDefinition>() : (SkillActionDefinition[])actions.Clone());
        }
    }

    public readonly struct AnimationCueRegion
    {
        public readonly float Start, End;
        public readonly GameplayTag CueTag;
        public AnimationCueRegion(float start, float end, GameplayTag cueTag)
        { Start = start; End = end; CueTag = cueTag; }
    }

    /// <summary>Shared timeline data; all per-cast state belongs to AnimationTask.</summary>
    public sealed class SkillAnimationDefinition
    {
        private readonly AnimationMarker[] _markers;
        private readonly AnimationCueRegion[] _cues;
        private readonly AnimationTagWindow[] _tags;
        public AnimationClip Clip { get; }
        public float Duration { get; }
        public bool Loop { get; }
        public float BlendIn { get; }
        public float BlendOut { get; }
        public ReadOnlySpan<AnimationMarker> Markers => _markers;
        public ReadOnlySpan<AnimationCueRegion> CueRegions => _cues;
        public ReadOnlySpan<AnimationTagWindow> TagWindows => _tags;
        public IReadOnlyList<RuntimeTrackDefinition> RuntimeTracks { get; }

        public SkillAnimationDefinition(AnimationClip clip, float duration, bool loop = false,
            AnimationMarker[] markers = null, AnimationCueRegion[] cueRegions = null,
            AnimationTagWindow[] tagWindows = null, float blendIn = 0, float blendOut = 0,
            RuntimeTrackDefinition[] runtimeTracks = null)
        {
            Clip = clip; Duration = duration; Loop = loop; BlendIn = blendIn; BlendOut = blendOut;
            _markers = markers == null ? Array.Empty<AnimationMarker>() : (AnimationMarker[])markers.Clone();
            _cues = cueRegions == null ? Array.Empty<AnimationCueRegion>() : (AnimationCueRegion[])cueRegions.Clone();
            _tags = tagWindows == null ? Array.Empty<AnimationTagWindow>() : (AnimationTagWindow[])tagWindows.Clone();
            Array.Sort(_markers, static (a, b) => a.Time.CompareTo(b.Time));
            Array.Sort(_cues, static (a, b) => a.Start.CompareTo(b.Start));
            Array.Sort(_tags, static (a, b) => a.Start.CompareTo(b.Start));
            RuntimeTracks = Array.AsReadOnly(runtimeTracks == null ? Array.Empty<RuntimeTrackDefinition>() : (RuntimeTrackDefinition[])runtimeTracks.Clone());
        }
    }
}
