using System;
using System.ComponentModel;
using GameplayTags;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    public interface ISkillTargetSelection
    {
        IDisposable Request(SkillExecutionContext context, Action<AbilitySystemComponent, Transform> selected);
    }

    public enum SkillWaitKind { Delay, GameplayEvent, Target }

    [Serializable, DisplayName("等待")]
    public sealed class WaitSkillAction : SkillAction
    {
        [FlowField("等待")] public SkillWaitKind kind;
        public SkillNumber timeout = new(1f);
        public GameplayTag eventTag;
        [SerializeReference, FlowBranch("成功")] public SkillAction[] onSuccess = Array.Empty<SkillAction>();
        [SerializeReference, FlowBranch("超时")] public SkillAction[] onTimeout = Array.Empty<SkillAction>();
        [SerializeReference, FlowBranch("取消")] public SkillAction[] onCancel = Array.Empty<SkillAction>();

        public override bool IsDetachedBranch(string field) => field == nameof(onCancel);

        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            builder.RequireAbilityTask(Id);
            builder.Number(timeout, float.Epsilon);
            SkillWaitKind wait = kind;
            SkillNumber duration = timeout;
            GameplayTag tag = eventTag.Resolve();
            string id = Id;
            SkillActionDefinition[] success = builder.Build(onSuccess), timedOut = builder.Build(onTimeout);
            SkillActionDefinition[] cancelled = BuildBranch(builder, nameof(onCancel), onCancel);
            if (wait == SkillWaitKind.GameplayEvent && !tag.IsValid) throw new InvalidOperationException("等待事件缺少有效标签。");

            return Define("等待 · " + wait, ctx =>
                ctx.Start(new SkillWaitTask(ctx, id, wait, duration.Resolve(ctx.Values), tag, success, timedOut, cancelled),
                    SkillTaskOwnership.Ability));
        }
    }

    internal sealed class SkillWaitTask : SkillActionTask
    {
        private readonly SkillWaitKind _kind;
        private readonly GameplayTag _tag;
        private readonly SkillActionDefinition[] _success, _timeout, _cancel;
        private readonly Action<GameplayEvent> _onEvent;
        private readonly Action<AbilitySystemComponent, Transform> _onTarget;
        private float _remaining;
        private GameplayEventChannel _events;
        private IDisposable _request;
        private bool _ready;
        private SkillExecutionContext _result;

        public override string DiagnosticId => ActionId;
        public override string State => _kind == SkillWaitKind.Delay ? "延时中" : _kind == SkillWaitKind.Target ? "等待选择目标" : "等待 Gameplay 事件";

        public SkillWaitTask(SkillExecutionContext context, string id, SkillWaitKind kind, float timeout, GameplayTag tag,
            SkillActionDefinition[] success, SkillActionDefinition[] timedOut, SkillActionDefinition[] cancel) : base(context, id)
        {
            _kind = kind; _remaining = timeout; _tag = tag; _success = success; _timeout = timedOut; _cancel = cancel;
            _result = Execution;
            if (kind == SkillWaitKind.GameplayEvent) _onEvent = Receive;
            else if (kind == SkillWaitKind.Target) _onTarget = SelectTarget;
        }

        protected override void BeginAction()
        {
            if (_kind == SkillWaitKind.GameplayEvent)
            {
                _events = Execution.Source.Events;
                _events.Subscribe(_tag, _onEvent);
            }
            else if (_kind == SkillWaitKind.Target)
            {
                var selection = Execution.Services.TargetSelection
                    ?? throw new InvalidOperationException("等待目标选择需在 SkillUnitServices 中注入 TargetSelection。");
                IDisposable req = selection.Request(Execution, _onTarget);
                if (IsActive) _request = req; else req?.Dispose();
            }
        }

        private void SelectTarget(AbilitySystemComponent unit, Transform transform)
        {
            if (IsActive && !_ready && unit != null) { _result = Execution.WithTarget(unit, transform); _ready = true; }
        }

        private void Receive(GameplayEvent message)
        {
            if (IsActive && !_ready) { _result = Execution.WithEvent(message); _ready = true; }
        }

        protected override void Tick(float deltaTime)
        {
            _remaining -= deltaTime;
            if (_remaining > 0f && !_ready) return;
            SkillActionDefinition[] next = _ready || _kind == SkillWaitKind.Delay ? _success : _timeout;
            _result.RefreshAsyncTargets();
            Complete();
            _result.Execute(next);
        }

        protected override void EndAction(bool cancelled)
        {
            if (_events != null) { _events.Unsubscribe(_tag, _onEvent); _events = null; }
            IDisposable req = _request; _request = null;
            if (cancelled) Execution.Cleanup(_cancel, req); else req?.Dispose();
        }
    }
}
