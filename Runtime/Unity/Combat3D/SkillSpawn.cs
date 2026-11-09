using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZFramework.GAS.Unity
{
    /// <summary>经 <see cref="ISkillObjectService"/> 生成对象：施放已结束时立即归还，并调用 <see cref="ISkillSpawnInitializer"/>。</summary>
    public static class SkillSpawn
    {
        public static GameObject Create(SkillExecutionContext context, GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            var instance = context.Services.Objects.Spawn(prefab, position, rotation, parent);
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
    }
}
