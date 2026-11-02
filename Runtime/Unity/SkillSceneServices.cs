using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZFramework.GAS.Unity
{
    public interface ISkillObjectService
    {
        GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent);
        void Release(GameObject instance);
    }
    public interface ISkillSpawnInitializer
    {
        void InitializeSpawn(SkillExecutionContext context);
    }
    public sealed class InstantiateSkillObjectService : ISkillObjectService
    {
        public static readonly InstantiateSkillObjectService Instance = new InstantiateSkillObjectService();
        public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent)
            => UnityEngine.Object.Instantiate(prefab, position, rotation, parent);
        public void Release(GameObject instance) { if (instance != null) UnityEngine.Object.Destroy(instance); }
    }

    public interface ISkillTaskState
    {
        SkillExecutionContext Execution { get; }
        string ActionId { get; }
        string State { get; }
    }


}
