using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [DisallowMultipleComponent, AddComponentMenu("ZFramework GAS/Skill Animator")]
    public sealed class SkillAnimator : MonoBehaviour
    {
        [SerializeReference] private AnimationBackend backend = new AnimatorBackend();
        private IAnimationDriver _driver;
        public AnimationBackend Backend => backend;
        public IAnimationDriver Driver
        {
            get
            {
                if (_driver != null) return _driver;
                if (backend == null) throw new InvalidOperationException("SkillAnimator 未设置动画后端。");
                if (!backend.Validate(gameObject, out string error)) throw new InvalidOperationException(error);
                return _driver = backend.CreateDriver(gameObject);
            }
        }
        public void SetBackend(AnimationBackend value)
        {
            _driver?.Stop();
            _driver = null;
            backend = value;
        }
        private void OnDisable() => _driver?.Stop();
        private void OnDestroy() { _driver?.Stop(); _driver = null; }
        private void OnValidate() { _driver?.Stop(); _driver = null; }
    }
}
