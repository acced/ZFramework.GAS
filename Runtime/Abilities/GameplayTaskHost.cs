using System;
using System.Collections.Generic;

namespace ZFramework.GAS
{
    /// <summary>Explicit owner for detached work. The origin spec is provenance, not lifetime ownership.</summary>
    public sealed class GameplayTaskHost
    {
        private readonly List<GameplayAbilityTask> _tasks = new();
        private readonly List<(GameplayAbilityTask task, long run)> _frame = new();
        private bool _clearing, _ticking;
        public GameplayAbilityTask CurrentTask { get; private set; }
        public int Count => _tasks.Count;
        public GameplayAbilityTask this[int index] => _tasks[index];
        public void Start(GameplayAbilitySpec origin, GameplayAbilityTask task)
            => Start(origin, task, origin?.ActivationVersion ?? 0);
        public void Start(GameplayAbilitySpec origin, GameplayAbilityTask task, int activationVersion)
        {
            if (_clearing) throw new InvalidOperationException("Task host is shutting down.");
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (task.IsActive) throw new InvalidOperationException("Task already has a host.");
            CurrentTask = task; _tasks.Add(task);
            task.Begin(origin, activationVersion);
        }
        public void Tick(float deltaTime)
        {
            if (_ticking) throw new InvalidOperationException("GameplayTaskHost.Tick cannot be called reentrantly. Advance it once from the host loop.");
            _ticking = true;
            try
            {
                CurrentTask = null;
                for (int i = _tasks.Count - 1; i >= 0; i--) _frame.Add((_tasks[i], _tasks[i].RunVersion));
                foreach (var item in _frame)
                {
                    var task = item.task;
                    if (task.RunVersion != item.run) continue;
                    CurrentTask = task; if(task.IsActive)task.Advance(deltaTime);
                    if (task.RunVersion == item.run && !task.IsActive) _tasks.Remove(task);
                }
                CurrentTask = null;
            }
            finally { _frame.Clear(); _ticking = false; }
        }
        public bool Cancel(GameplayAbilityTask task)
        {
            if (!_tasks.Remove(task)) return false;
            task.CancelInternal(); return true;
        }
        public void Clear()
        {
            if (_clearing) return;
            _clearing = true;
            try
            {
                while (_tasks.Count > 0)
                {
                    var task = _tasks[_tasks.Count - 1]; _tasks.RemoveAt(_tasks.Count - 1);
                    task.CancelInternal();
                }
            }
            finally { _clearing = false; }
        }
    }
}
