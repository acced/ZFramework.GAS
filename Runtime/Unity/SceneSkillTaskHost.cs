using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZFramework.GAS.Unity
{
    /// <summary>Optional FixedUpdate host. Add explicitly and inject Tasks into SkillUnitServices.</summary>
    [AddComponentMenu("GAS/Scene Task Host")]
    public sealed class SceneSkillTaskHost : MonoBehaviour
    {
        public GameplayTaskHost Tasks { get; } = new GameplayTaskHost();
#if UNITY_EDITOR
        private static readonly List<SceneSkillTaskHost> Hosts = new();
        public static void CopyTasks(List<GameplayAbilityTask> results)
        {
            results.Clear();
            foreach (var host in Hosts) if (host != null)
                for (int i = 0; i < host.Tasks.Count; i++) results.Add(host.Tasks[i]);
        }
        private void OnEnable() { if (!Hosts.Contains(this)) Hosts.Add(this); }
#endif
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
#if UNITY_EDITOR
            Hosts.Remove(this);
#endif
            try { Tasks.Clear(); } catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }
}

