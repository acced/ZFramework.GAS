using System;
using UnityEngine;

namespace ZFramework.GAS.Editor
{
    internal sealed class ClipPoseSampler : IAnimationPreviewSampler
    {
        private readonly AnimationClip _clip;
        private readonly GameObject _root;
        private readonly Transform[] _bones;
        private readonly Vector3[] _positions, _scales;
        private readonly Quaternion[] _rotations;
        public ClipPoseSampler(GameObject instance, AnimationClip clip)
        {
            _clip = clip;
            var animator = instance.GetComponentInChildren<Animator>(true);
            _root = animator != null ? animator.gameObject : instance;
            _bones = instance.GetComponentsInChildren<Transform>(true);
            _positions = Array.ConvertAll(_bones, bone => bone.localPosition);
            _rotations = Array.ConvertAll(_bones, bone => bone.localRotation);
            _scales = Array.ConvertAll(_bones, bone => bone.localScale);
        }
        public void Sample(float normalizedTime)
        {
            Restore();
            _clip.SampleAnimation(_root, Mathf.Clamp01(normalizedTime) * _clip.length);
        }
        private void Restore()
        {
            for (int i = 0; i < _bones.Length; i++)
            {
                _bones[i].localPosition = _positions[i];
                _bones[i].localRotation = _rotations[i];
                _bones[i].localScale = _scales[i];
            }
        }
        public void Dispose() => Restore();
    }
}
