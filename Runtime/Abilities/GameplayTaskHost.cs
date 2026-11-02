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
        public int Count => _tasks.Count;
        public GameplayAbilityTask this[int index] => _tasks[index];
        public void Start(GameplayAbilitySpec origin, GameplayAbilityTask task)
        {
            if (_clearing) throw new InvalidOperationException("Task host is shutting down.");
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (task.IsActive) throw new InvalidOperationException("Task already has a host.");
            _tasks.Add(task);
            try { task.Begin(origin); }
            catch { _tasks.Remove(task); task.CancelInternal(); throw; }
        }
        public void Tick(float deltaTime)
        {
            if (_ticking) return;
            _ticking = true;
            List<Exception> errors = null;
            try
            {
                for (int i = _tasks.Count - 1; i >= 0; i--) _frame.Add((_tasks[i], _tasks[i].RunVersion));
                foreach (var item in _frame)
                {
                    var task = item.task;
                    if (task.RunVersion != item.run || !_tasks.Contains(task)) continue;
                    try { task.Advance(deltaTime); }
                    catch (Exception ex)
                    {
                        (errors ??= new List<Exception>()).Add(ex);
                        if (task.RunVersion == item.run && _tasks.Remove(task))
                            try { task.CancelInternal(); } catch (Exception cleanup) { errors.Add(cleanup); }
                    }
                    if (task.RunVersion == item.run && !task.IsActive) _tasks.Remove(task);
                }
            }
            finally { _frame.Clear(); _ticking = false; }
            ThrowErrors(errors);
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
            List<Exception> errors = null;
            try
            {
                while (_tasks.Count > 0)
                {
                    var task = _tasks[_tasks.Count - 1]; _tasks.RemoveAt(_tasks.Count - 1);
                    try { task.CancelInternal(); } catch (Exception ex) { (errors ??= new List<Exception>()).Add(ex); }
                }
            }
            finally { _clearing = false; }
            ThrowErrors(errors);
        }
        private static void ThrowErrors(List<Exception> errors)
        {
            if (errors == null) return;
            if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            throw new AggregateException("Scene task failures", errors);
        }
    }
}
