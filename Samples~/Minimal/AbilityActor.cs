using System;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Samples
{
    /// <summary>Connect your input/AI to Cast. One actor owns its ASC, runner and optional timeline.</summary>
    public sealed class AbilityActor : MonoBehaviour, IAbilitySystemProvider
    {
        [Serializable]
        public struct InitialAttribute { public int id; public float value; }
        [SerializeField] private InitialAttribute[] attributes = Array.Empty<InitialAttribute>();
        [SerializeField] private GameplayAbilityAsset[] abilities = Array.Empty<GameplayAbilityAsset>();
        [SerializeField] private Animator animator;
        public AbilitySystemComponent AbilitySystem { get; } = new();
        private SkillRunner _runner;
        private SkillTimelinePlayer _timeline;

        private void Awake()
        {
            foreach (var attribute in attributes) AbilitySystem.Attributes.Add(new AttributeId(attribute.id), attribute.value);
        }
        private void OnEnable()
        {
            _runner = new SkillRunner(AbilitySystem, new SkillUnitServices {
                Origin = transform, Searcher = new PhysicsTargetSearcher(), Scene = gameObject.scene
            });
            if (animator != null) _timeline = new SkillTimelinePlayer(_runner, new PlayableAnimationDriver(animator));
            foreach (var ability in abilities) _runner.Grant(ability);
        }
        public AbilityActivationCheck Cast(GameplayAbilityAsset ability, AbilityActor target)
            => _runner.TryCast(ability, new SkillCastTarget(target?.AbilitySystem, target != null ? target.transform : null));
        private void Update() => AbilitySystem.Tick(Time.deltaTime);
        private void OnDisable()
        {
            AbilitySystem.Clear();
            _timeline?.Dispose(); _timeline = null;
        }
    }
}
