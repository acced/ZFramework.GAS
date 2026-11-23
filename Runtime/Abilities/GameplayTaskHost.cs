using System;
using System.Buffers;
using System.Collections.Generic;

namespace ZFramework.GAS
{
    /// <summary>Explicit owner for detached work. The origin spec is provenance, not lifetime ownership.</summary>
    public sealed class GameplayTaskHost
    {
        private readonly List<GameplayAbilityTask> _tasks = new();
        private readonly List<(GameplayAbilityTask task, long run)> _frame = new();
        private bool _clearing, _ticking;

        public GameplayAbilityTask CurrentTask { get; internal set; }
        public int Count => _tasks.Count;
        public GameplayAbilityTask this[int index] => _tasks[index];

        public void Start(GameplayAbilitySpec origin, GameplayAbilityTask task)
            => Start(origin, task, origin?.ActivationVersion ?? 0);

        public void Start(GameplayAbilitySpec origin, GameplayAbilityTask task, int activationVersion)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (_clearing) throw new InvalidOperationException("Task host is shutting down.");
            if (task.IsActive) throw new InvalidOperationException("Task already has a host.");
            CurrentTask = task;
            _tasks.Add(task);
            task.Begin(origin, activationVersion);
        }

        public void Tick(float deltaTime)
        {
            if (_ticking) throw new InvalidOperationException("GameplayTaskHost.Tick cannot be called reentrantly.");
            if (_tasks.Count == 0) { CurrentTask = null; return; }
            _ticking = true;
            try
            {
                CurrentTask = null;
                for (int i = _tasks.Count - 1; i >= 0; i--) _frame.Add((_tasks[i], _tasks[i].RunVersion));
                for (int i = 0; i < _frame.Count; i++)
                {
                    var (task, run) = _frame[i];
                    if (task.RunVersion != run) continue;
                    CurrentTask = task;
                    task.Advance(deltaTime);
                    if (task.RunVersion == run && !task.IsActive) _tasks.Remove(task);
                }
                CurrentTask = null;
            }
            finally { _frame.Clear(); _ticking = false; }
        }

        public bool Cancel(GameplayAbilityTask task)
        {
            if (task == null || !_tasks.Remove(task)) return false;
            task.CancelInternal();
            return true;
        }

        internal void Cancel(GameplayAbilitySpec ability, int activationVersion)
        {
            int count = _tasks.Count;
            if (count == 0) return;
            if (count == 1)
            {
                GameplayAbilityTask t = _tasks[0];
                if (ReferenceEquals(t.Ability, ability) && t.ActivationVersion == activationVersion) Cancel(t);
                return;
            }
            var snapshot = ArrayPool<(GameplayAbilityTask task, long run)>.Shared.Rent(count);
            for (int i = 0; i < count; i++) snapshot[i] = (_tasks[i], _tasks[i].RunVersion);
            try
            {
                for (int i = count - 1; i >= 0; i--)
                {
                    var (t, run) = snapshot[i];
                    if (t.RunVersion == run && ReferenceEquals(t.Ability, ability) && t.ActivationVersion == activationVersion)
                        Cancel(t);
                }
            }
            finally { ArrayPool<(GameplayAbilityTask, long)>.Shared.Return(snapshot, clearArray: true); }
        }

        public void Clear()
        {
            if (_clearing) return;
            _clearing = true;
            try
            {
                while (_tasks.Count > 0)
                {
                    int last = _tasks.Count - 1;
                    GameplayAbilityTask t = _tasks[last];
                    _tasks.RemoveAt(last);
                    t.CancelInternal();
                }
                CurrentTask = null;
            }
            finally { _clearing = false; }
        }
    }
}
