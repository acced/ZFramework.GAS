using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor
{
    public interface IAnimationPreviewSampler : IDisposable
    {
        void Sample(float normalizedTime);
    }

    public interface IAnimationClipEditor
    {
        Type AssetType { get; }
        float Duration(Object clip);
        IAnimationPreviewSampler CreatePreview(GameObject instance, Object clip);
    }

    /// <summary>Editor-only resource metadata and isolated pose sampling.</summary>
    public static class AnimationClipEditors
    {
        private static readonly Dictionary<Type, IAnimationClipEditor> Editors = new()
        {
            [typeof(AnimationClip)] = new UnityClipEditor()
        };

        public static void Register(IAnimationClipEditor editor) => Editors.Add(editor.AssetType, editor);

        public static bool TryGet(Object clip, out IAnimationClipEditor editor)
        {
            editor = null;
            return clip != null && Editors.TryGetValue(clip.GetType(), out editor);
        }

        internal static float GetDuration(Object clip)
        {
            if (!TryGet(clip, out var editor))
                throw new ArgumentException("不支持该动画资源，请安装对应的动画适配包。", nameof(clip));
            float duration = editor.Duration(clip);
            if (!float.IsFinite(duration) || duration < 0)
                throw new ArgumentException("源动画时长必须为有限非负数。", nameof(clip));
            return duration;
        }

        private sealed class UnityClipEditor : IAnimationClipEditor
        {
            public Type AssetType => typeof(AnimationClip);
            public float Duration(Object clip) => ((AnimationClip)clip).length;
            public IAnimationPreviewSampler CreatePreview(GameObject instance, Object clip) =>
                new ClipPoseSampler(instance, (AnimationClip)clip);
        }
    }
}
