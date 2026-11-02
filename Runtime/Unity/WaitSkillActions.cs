using System;
using GameplayTags;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    public interface ISkillTargetSelection
    {
        IDisposable Request(SkillExecutionContext context, Action<AbilitySystemComponent, Transform> selected);
    }
    public enum SkillWaitKind { Delay, GameplayEvent, Target }

    [Serializable]
    public sealed class WaitSkillAction : SkillAction
    {
        public SkillWaitKind kind;
        public SkillNumber timeout = new SkillNumber(1);
        public GameplayTag eventTag;
        [SerializeReference] public SkillAction[] onSuccess = Array.Empty<SkillAction>();
        [SerializeReference] public SkillAction[] onTimeout = Array.Empty<SkillAction>();
        [SerializeReference] public SkillAction[] onCancel = Array.Empty<SkillAction>();
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            builder.RequireAbilityTask(Id);
            var wait = kind; var duration = timeout; var tag = eventTag.Resolve(); var id = Id;
            var success = builder.Build(onSuccess); var timedOut = builder.Build(onTimeout); var cancelled = builder.BuildDetached(onCancel);
            if (wait == SkillWaitKind.GameplayEvent && !tag.IsValid) throw new InvalidOperationException("等待事件缺少有效标签。");
            return Define("等待 · " + wait, (context, deferred) =>
            {
                SkillSpawn.Positive(duration.Resolve(context.Values), "等待时长");
                if (wait == SkillWaitKind.Target && context.Services.TargetSelection == null) throw new InvalidOperationException("缺少目标选择服务。");
                SkillActionBuildContext.Validate(success, context, deferred || wait == SkillWaitKind.Target);
                SkillActionBuildContext.Validate(timedOut, context, deferred);
                SkillActionBuildContext.Validate(cancelled, context, deferred);
            }, context => context.Start(new SkillWaitTask(context, id, wait, duration.Resolve(context.Values), tag, success, timedOut, cancelled), false));
        }
    }
    internal sealed class SkillWaitTask : SkillActionTask
    {
        private readonly SkillWaitKind _kind;
        private float _remaining;
        private readonly GameplayTag _tag;
        private readonly SkillActionDefinition[] _success, _timeout, _cancel;
        private GameplayEventChannel _events;
        private IDisposable _request;
        private bool _ready;
        private SkillExecutionContext _result;
        public override string State => _kind == SkillWaitKind.Delay ? "延时中" : _kind == SkillWaitKind.Target ? "等待选择目标" : "等待 Gameplay 事件";
        public SkillWaitTask(SkillExecutionContext context, string id, SkillWaitKind kind, float timeout, GameplayTag tag,
            SkillActionDefinition[] success, SkillActionDefinition[] timedOut, SkillActionDefinition[] cancel) : base(context, id)
        { _kind = kind; _remaining = timeout; _tag = tag; _success = success; _timeout = timedOut; _cancel = cancel; _result = Execution; }
        protected override void OnBegin()
        {
            if (_kind == SkillWaitKind.GameplayEvent)
            { _events = Execution.Source.Events; _events.Received += Receive; }
            if (_kind == SkillWaitKind.Target)
            {
                var request = Execution.Services.TargetSelection.Request(Execution, (unit, transform) =>
                { if (IsActive && unit != null) { _result = Execution.WithTarget(unit, transform); _ready = true; } });
                if (IsActive) _request = request; else request?.Dispose();
            }
        }
        private void Receive(GameplayEvent message)
        { if (message.Tag == _tag && IsActive) { _result = Execution.WithEvent(message); _ready = true; } }
        protected override void Tick(float dt)
        {
            _remaining -= dt;
            if (_remaining > 0 && !_ready) return;
            var next = _ready || _kind == SkillWaitKind.Delay ? _success : _timeout;
            Complete(); _result.Execute(next);
        }
        protected override void OnEnd(bool cancelled)
        {
            if (_events != null) { _events.Received -= Receive; _events = null; }
            var request = _request; _request = null;
            if (cancelled) Execution.Cleanup(Execution.Error == null ? _cancel : null, request, ActionId);
            else request?.Dispose();
        }
    }
}
