using System;
using IronOath.Combat;
using Spine.Unity;
using UnityEngine;
using ZFramework.GAS.Unity.Spine;

namespace IronOath.Presentation
{
    /// <summary>
    /// Optional cosmetic receiver for the existing Spine adapter. Combat is already resolved by GAS;
    /// native Spine events never deal damage, spend AP, or grant loot a second time.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    public sealed class IronOathSpinePresentation : MonoBehaviour, IIronOathBattlePresentation
    {
        [Serializable]
        public sealed class SkillClip
        {
            public BattleSkillId skill;
            public AnimationReferenceAsset animation;
            [Min(.05f)] public float duration = .6f;
        }

        [SerializeField] private SkeletonAnimation skeleton;
        [SerializeField] private SkillClip[] skillClips = Array.Empty<SkillClip>();
        [SerializeField] private AnimationReferenceAsset hit;
        [SerializeField] private AnimationReferenceAsset armorBreak;
        [SerializeField] private AnimationReferenceAsset defeat;
        [SerializeField, Min(1)] private int skillTrack = 1;
        [SerializeField, Min(0)] private float blend = .08f;

        private BattleUnit _unit;
        private SpineAnimationDriver _driver;
        private float _elapsed, _duration;
        private bool _playing, _holdFinalPose;

        public void Bind(BattleUnit unit)
        {
            Release();
            _unit = unit;
            EnsureDriver();
        }

        private void OnEnable() => EnsureDriver();

        private void EnsureDriver()
        {
            if (_driver != null || _unit == null || !isActiveAndEnabled) return;
            if (skeleton == null) skeleton = GetComponentInChildren<SkeletonAnimation>();
            if (skeleton == null) return;
            skeleton.Initialize(false);
            if (skeleton.AnimationState != null)
                _driver = new SpineAnimationDriver(skeleton, Math.Max(1, skillTrack));
        }

        public void Present(in BattleEvent battleEvent)
        {
            if (_unit == null || _driver == null) return;
            if (battleEvent.TargetId == _unit.Id)
            {
                if (battleEvent.Kind == BattleEventKind.Defeated) { Play(defeat, .7f, true); return; }
                if (battleEvent.Kind == BattleEventKind.ArmorBroken) { Play(armorBreak, .5f, false); return; }
                if (battleEvent.Kind == BattleEventKind.Hit) { Play(hit, .22f, false); return; }
            }
            if (battleEvent.SourceId != _unit.Id || battleEvent.Kind != BattleEventKind.Action || skillClips == null) return;
            for (int i = 0; i < skillClips.Length; i++)
            {
                SkillClip clip = skillClips[i];
                if (clip != null && clip.skill == battleEvent.SkillId)
                { Play(clip.animation, clip.duration, false); return; }
            }
        }

        private void Play(AnimationReferenceAsset clip, float duration, bool hold)
        {
            if (clip == null || clip.Animation == null || !float.IsFinite(duration)) return;
            _elapsed = 0;
            _duration = Mathf.Max(.05f, duration);
            _holdFinalPose = hold;
            _playing = true;
            _driver.Play(clip, Mathf.Max(0, blend));
            _driver.Advance(0);
        }

        private void Update()
        {
            if (!_playing || _driver == null) return;
            _elapsed += Time.unscaledDeltaTime;
            _driver.Advance(Mathf.Clamp01(_elapsed / _duration));
            if (_elapsed < _duration) return;
            _playing = false;
            if (!_holdFinalPose) _driver.Stop(Mathf.Max(0, blend));
        }

        private void OnDisable() => Release();
        private void OnDestroy() => Release();
        private void Release()
        {
            _playing = false;
            _driver?.Dispose();
            _driver = null;
        }
    }
}
