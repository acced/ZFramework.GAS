using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZFramework.GAS.Unity
{
    public enum SkillTaskOwnership { [InspectorName("打断后继续（场景）")] Scene, [InspectorName("随施放取消（技能）")] Ability }
    public enum SkillSpawnPosition { Caster, Target, TargetPoint, HitPoint, EventPosition, LaunchPoint }

    public abstract class SkillActionTask : GameplayAbilityTask, ISkillTaskState
    {
        public SkillExecutionContext Execution { get; }
        public string ActionId { get; }
        public abstract string State { get; }
        protected SkillActionTask(SkillExecutionContext context, string id) { Execution = context.ForTask(); ActionId = id; }
        internal void Detach() => Execution.DetachTask();
        protected sealed override void OnBegin()
        {
            Execution.Retain(); // Until OnEnd.
            Execution.Retain(); // BeginAction may complete and immediately recast.
            try { BeginAction(); } finally { Execution.Release(); }
        }
        protected virtual void BeginAction() { }
        protected sealed override void OnTick(float deltaTime)
        {
            Execution.Retain(); // Complete() may precede the last callback in this tick.
            try { Execution.RefreshAsyncTargets(); Tick(deltaTime); } finally { Execution.Release(); }
        }
        protected abstract void Tick(float deltaTime);
        protected sealed override void OnEnd(bool cancelled)
        { try { EndAction(cancelled); } finally { Execution.Release(); } }
        protected virtual void EndAction(bool cancelled) { }
    }

    internal static class SkillSpawn
    {
        public static GameObject Create(SkillExecutionContext context, GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            var instance = context.Services.Objects.Spawn(prefab, position, rotation, parent);
            if (instance == null) throw new InvalidOperationException("对象生成服务返回空对象。");
            if (!context.CanContinue) { context.Services.Objects.Release(instance); return null; }
            var scene = context.Services.Scene;
            if (!scene.IsValid() && context.Services.Origin != null) scene = context.Services.Origin.gameObject.scene;
            if (parent == null && scene.IsValid() && instance.scene != scene) SceneManager.MoveGameObjectToScene(instance, scene);
            var initializers = instance.GetComponentsInChildren<ISkillSpawnInitializer>(true);
            foreach (var initializer in initializers)
            {
                initializer.InitializeSpawn(context);
                if (!context.CanContinue) { context.Services.Objects.Release(instance); return null; }
            }
            return instance;
        }
        public static void Positive(float value, string name)
        { if (!(value > 0) || float.IsInfinity(value)) throw new InvalidOperationException(name + "必须大于零。"); }
    }
}
