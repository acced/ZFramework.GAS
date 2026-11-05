using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZFramework.GAS.Unity
{
    /// <summary>One explicit task host per scene; unloading/disable returns every spawned object.</summary>
    [AddComponentMenu("")]
    public sealed class SceneSkillTaskHost : MonoBehaviour
    {
        private static readonly List<SceneSkillTaskHost> Hosts = new();
        public GameplayTaskHost Tasks { get; } = new GameplayTaskHost();
        public static SceneSkillTaskHost For(Scene scene)
        {
            foreach (var host in Hosts) if (host != null && host.gameObject.scene == scene) return host;
            if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("技能场景已卸载。");
            var go = new GameObject("GAS Scene Tasks");
            SceneManager.MoveGameObjectToScene(go, scene);
            return go.AddComponent<SceneSkillTaskHost>();
        }
        public static void CopyTasks(List<GameplayAbilityTask> results)
        {
            results.Clear();
            foreach (var host in Hosts) if (host != null)
                for (int i = 0; i < host.Tasks.Count; i++) results.Add(host.Tasks[i]);
        }
        private void OnEnable() { if (!Hosts.Contains(this)) Hosts.Add(this); }
        private void FixedUpdate()
        {
            try { Tasks.Tick(Time.fixedDeltaTime); }
            catch (Exception exception) {
                Debug.LogException(exception, this);
                var task = Tasks.CurrentTask;
                if (task != null) {
                    Tasks.Cancel(task);
                    if (task.Ability?.ActivationVersion == task.ActivationVersion) task.Ability.Owner.EndAbility(task.Ability);
                }
            }
        }
        private void OnDisable()
        {
            Hosts.Remove(this);
            try { Tasks.Clear(); } catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }
}

