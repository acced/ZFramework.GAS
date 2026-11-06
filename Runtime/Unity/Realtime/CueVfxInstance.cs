using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Unity
{
    /// <summary>运行时与预览共享的实例创建、挂点和停止策略。每个实例单独拥有粒子及自定义表现状态。</summary>
    public sealed class CueVfxInstance : IDisposable
    {
        private readonly CueVfxBinding _binding;
        private readonly bool _preview;
        private readonly ParticleSystem[] _particles;
        private readonly CueVfxBehaviour[] _behaviours;
        private float _elapsed, _tail;
        private bool _stopping;
        public GameObject Root { get; private set; }

        public CueVfxInstance(CueVfxBinding binding, Transform owner, bool preview)
        {
            _binding = binding; _preview = preview;
            var anchor = binding.FindAnchor(owner);
            if (anchor == null) throw new InvalidOperationException($"Cue '{binding.cueTag.Name}' 的挂点不存在：{binding.anchorPath}");
            if (binding.prefab == null) throw new InvalidOperationException($"Cue '{binding.cueTag.Name}' 未设置特效 Prefab。");
            Root = new GameObject("CueVFX · " + binding.cueTag.Name);
            try
            {
                // 先挂入角色所在场景（包含 PreviewScene），再决定是否脱离跟随。
                Root.transform.SetParent(anchor, false);
                Root.transform.localPosition = binding.position;
                Root.transform.localRotation = Quaternion.Euler(binding.rotation);
                if (!binding.follow) Root.transform.SetParent(null, true);
                Root.SetActive(false);
                var effect = Object.Instantiate(binding.prefab, Root.transform, false);
                effect.SetActive(true);
                _particles = effect.GetComponentsInChildren<ParticleSystem>(true);
                _behaviours = effect.GetComponentsInChildren<CueVfxBehaviour>(true);
                foreach (var ps in _particles)
                {
                    var main = ps.main;
                    main.playOnAwake = false;
                    main.stopAction = ParticleSystemStopAction.None;
                    if (preview) { ps.useAutoRandomSeed = false; ps.randomSeed = 17; }
                }
                if (preview)
                {
                    foreach (var transform in Root.GetComponentsInChildren<Transform>(true)) transform.gameObject.hideFlags = HideFlags.HideAndDontSave;
                    foreach (var behaviour in effect.GetComponentsInChildren<MonoBehaviour>(true)) if (behaviour != null) behaviour.enabled = false;
                    foreach (var animator in effect.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                    foreach (var animation in effect.GetComponentsInChildren<Animation>(true)) animation.enabled = false;
                }
                foreach (var behaviour in _behaviours) { behaviour.Initialize(owner); behaviour.OnCueStart(); }
                Root.SetActive(true);
                foreach (var ps in _particles)
                {
                    if (preview) ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                    else ps.Play(false);
                }
            }
            catch { Dispose(); throw; }
        }

        /// <returns>是否仍需要保留实例。</returns>
        public bool Tick(float deltaTime)
        {
            if (Root == null) return false;
            _elapsed += deltaTime;
            foreach (var behaviour in _behaviours) if (behaviour != null) behaviour.Sample(_elapsed, deltaTime, false);
            if (!_stopping) return true;
            _tail += deltaTime;
            if (_tail < Mathf.Max(.01f, _binding.tailSeconds))
            {
                if (_particles.Length == 0) return true;
                foreach (var ps in _particles) if (ps != null && ps.IsAlive(false)) return true;
            }
            Dispose(); return false;
        }

        public void Stop()
        {
            if (Root == null || _stopping) return;
            _stopping = true;
            foreach (var behaviour in _behaviours) if (behaviour != null) behaviour.OnCueStop();
            if (_binding.stopMode == CueVfxStopMode.Immediate) { Dispose(); return; }
            foreach (var ps in _particles) if (ps != null) ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
        }

        /// <summary>从零采样，支持任意前后拖动。停止发射模式在区间结束后继续模拟已有粒子。</summary>
        public void SamplePreview(float elapsed, float duration)
        {
            if (Root == null) return;
            elapsed = Mathf.Max(0, elapsed);
            bool stopped = elapsed >= duration;
            if (_stopping != stopped)
            {
                foreach (var behaviour in _behaviours)
                    if (behaviour != null) { if (stopped) behaviour.OnCueStop(); else behaviour.OnCueStart(); }
                _stopping = stopped;
            }
            foreach (var behaviour in _behaviours) if (behaviour != null) behaviour.Sample(elapsed, 0, true);
            foreach (var ps in _particles)
            {
                if (ps == null) continue;
                ps.Simulate(Mathf.Min(elapsed, duration), false, true, true);
                if (elapsed >= duration)
                {
                    ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                    ps.Simulate(elapsed - duration, false, false, true);
                }
            }
        }

        public void Dispose()
        {
            if (Root == null) return;
            Root.SetActive(false);
            if (_preview || !Application.isPlaying) Object.DestroyImmediate(Root);
            else Object.Destroy(Root);
            Root = null;
        }
    }
}
