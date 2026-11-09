using System;
using System.Collections.Generic;
using GameplayTags;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class FlowFieldAttribute : Attribute
    {
        public string Label { get; }
        public string Child { get; }
        public FlowFieldAttribute(string label, string child = null) { Label = label; Child = child; }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class FlowBranchAttribute : Attribute
    {
        public string Label { get; }
        public FlowBranchAttribute(string label) => Label = label;
    }

    [Serializable]
    public abstract class SkillAction
    {
        [SerializeField, HideInInspector] private string id = Guid.NewGuid().ToString("N");
        public string Id => id;
        public string displayName;
        public abstract SkillActionDefinition Build(SkillActionBuildContext builder);
        // A normal branch inherits its caller's lifetime. A detached one cannot start ability-owned tasks.
        public virtual bool IsDetachedBranch(string field) => false;
        protected SkillActionDefinition[] BuildBranch(SkillActionBuildContext builder, string field, SkillAction[] children)
            => IsDetachedBranch(field) ? builder.BuildDetached(children) : builder.Build(children);
        protected SkillActionDefinition Define(string label, Action<SkillExecutionContext> execute) => new DelegateSkillAction(id,
                string.IsNullOrEmpty(displayName) ? label : displayName, execute);
    }

    public abstract class SkillActionDefinition
    {
        public string Id { get; }
        public string Label { get; }
        protected SkillActionDefinition(string id, string label) { Id = id; Label = label; }
        public abstract void Execute(SkillExecutionContext context);
    }
    internal sealed class DelegateSkillAction : SkillActionDefinition
    {
        private readonly Action<SkillExecutionContext> _execute;
        public DelegateSkillAction(string id, string label, Action<SkillExecutionContext> execute)
            : base(id, label) { _execute = execute; }
        public override void Execute(SkillExecutionContext context) => _execute(context);
    }
    public class SkillActionException : InvalidOperationException
    {
        public string ActionId { get; }
        public SkillActionException(string id, string message, Exception inner = null) : base(message, inner) { ActionId = id; }
    }
    public sealed class SkillOwnershipException : SkillActionException
    { public SkillOwnershipException(string id) : base(id,"独立任务回调或取消分支不能启动技能所属任务；请选择 Scene 归属或同步动作。") {} }
    public sealed class SkillActionBuildContext
    {
        private readonly ScriptableObject _owner;
        private readonly HashSet<string> _ids;
        public SkillActionBuildContext(ScriptableObject owner = null, HashSet<string> entryIds = null)
        { _owner = owner; _ids = entryIds ?? new HashSet<string>(); }
        private SkillParameter Parameter(string id)
        {
            foreach (var p in ((GameplayAbilityAsset)_owner).parameters) if (p.Id == id) return p;
            throw new InvalidOperationException("缺少参数: " + id);
        }
        public void Number(SkillNumber number, float min = float.MinValue, float max = float.MaxValue, bool integer = false)
        {
            if (!string.IsNullOrEmpty(number.parameterId))
            {
                if (_owner is not GameplayAbilityAsset) return;
                var p = Parameter(number.parameterId);
                if (p.kind == SkillParameterKind.Bool) throw new InvalidOperationException("数值绑定不能使用 Bool。");
                if (p.levels == null || p.levels.Length == 0) Number(new SkillNumber(p.defaultValue * number.coefficient), min, max, integer);
                else foreach (var value in p.levels) Number(new SkillNumber(value * number.coefficient), min, max, integer);
                return;
            }
            float value = number.value;
            if (!float.IsFinite(value) || value < min || value > max || integer && value != Mathf.Floor(value))
                throw new InvalidOperationException("动作数值超出允许范围。");
        }
        internal void RequireKind(string id, SkillParameterKind kind)
        {
            if (!string.IsNullOrEmpty(id) && _owner is GameplayAbilityAsset && Parameter(id).kind != kind)
                throw new InvalidOperationException($"参数 {id} 必须是 {kind}。");
        }
        public void Query(TargetQuery query)
        {
            if (query == null || query.maxTargets < 1 || query.maxTargets > 256) throw new InvalidOperationException("目标查询容量必须为 1–256。");
            if (query.kind is TargetQueryKind.Hitbox or TargetQueryKind.Project && string.IsNullOrWhiteSpace(query.sourceKey))
                throw new InvalidOperationException("请选择碰撞体槽位或填写项目目标来源键。");
            if (TargetQuery.IsVolume(query.kind)) Number(query.radius, 0);
            if (query.kind == TargetQueryKind.Line) { Number(query.width, 0); Number(query.height, 0); }
            if (query.kind == TargetQueryKind.Sector) Number(query.angle, 0, 360);
        }
        public void DependsOn(ScriptableObject asset) => DefinitionSource.DependsOn(_owner, asset);
        private bool _allowsAbilityTasks = true;
        public void RequireAbilityTask(string id)
        { if (!_allowsAbilityTasks) throw new SkillOwnershipException(id); }
        public SkillActionDefinition[] BuildDetached(SkillAction[] actions)
        {
            bool previous = _allowsAbilityTasks; _allowsAbilityTasks = false;
            try { return Build(actions); } finally { _allowsAbilityTasks = previous; }
        }
        public SkillActionDefinition[] Build(SkillAction[] actions)
        {
            var built = new SkillActionDefinition[actions.Length];
            for (int i = 0; i < actions.Length; i++)
            {
                var action = actions[i] ?? throw new InvalidOperationException("动作列表包含空项。");
                if (string.IsNullOrEmpty(action.Id) || !_ids.Add(action.Id)) throw new SkillActionException(action.Id, "动作 ID 缺失或重复。");
                try { built[i] = action.Build(this); }
                catch (SkillActionException) { throw; }
                catch (Exception exception) { throw new SkillActionException(action.Id, exception.Message, exception); }
            }
            return built;
        }
    }

    [Serializable]
    [System.ComponentModel.DisplayName("提交消耗与冷却")]
    public sealed class CommitAbilityAction : SkillAction
    {
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            builder.RequireAbilityTask(Id);
            return Define("提交消耗与冷却", context =>
            {
                var owner = context.Ability.Owner;
                if (!owner.CommitAbility(context.Ability, context.Version).Succeeded && context.IsCastActive)
                    owner.EndAbility(context.Ability);
            });
        }
    }

    [Serializable]
    [System.ComponentModel.DisplayName("施加效果")]
    public sealed class ApplyEffectAction : SkillAction
    {
        [FlowField("效果")] public GameplayEffectAsset effect;
        [FlowField("选敌", nameof(TargetQuery.kind))] public TargetQuery targets = new TargetQuery();
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            if (effect == null) throw new InvalidOperationException("缺少效果资产。");
            builder.Query(targets); builder.DependsOn(effect);
            return FromDefinition(Id, effect.Definition, targets.Build());
        }
        public static SkillActionDefinition FromDefinition(string id, GameplayEffectDefinition effect, TargetQueryDefinition query)
            => new DelegateSkillAction(id, "施加效果", context =>
            {
                var buffer = context.RentBuffer();
                try
                {
                    context.Services.Searcher.Query(query, context, buffer);
                    GasAssert.That(!buffer.Saturated, "目标查询缓冲区已满（256 个碰撞体），请收窄 LayerMask 或范围。");
                    for (int i = 0; i < buffer.Count; i++)
                    { if (!context.CanContinue) break; if (buffer.IsCurrent(i)) buffer.Units[i].Effects.Apply(context.Effect(effect)); }
                }
                finally { context.ReturnBuffer(buffer); }
            });
    }

    public enum SkillComparison { GreaterOrEqual, Greater, LessOrEqual, Less, Equal, NotEqual }
    [Serializable]
    [System.ComponentModel.DisplayName("条件分支")]
    public sealed class ConditionalAction : SkillAction
    {
        public static bool Compare(SkillComparison op,float a,float b) => op switch { SkillComparison.Greater => a > b, SkillComparison.GreaterOrEqual => a >= b,
                    SkillComparison.Less => a < b, SkillComparison.LessOrEqual => a <= b,
                    SkillComparison.Equal => a == b, _ => a != b };
        [FlowField("需要目标")] public bool requireTarget;
        [SkillParameterReference] public string booleanParameterId;
        public bool testTarget;
        [FlowField("必需标签")] public GameplayTag[] requiredTags = Array.Empty<GameplayTag>();
        public GameplayTag[] blockedTags = Array.Empty<GameplayTag>();
        public bool compareAttribute;
        [AttributeIdField] public int attribute;
        public SkillComparison comparison;
        public SkillNumber value = new SkillNumber(0);
        [SerializeReference, FlowBranch("满足")] public SkillAction[] whenTrue = Array.Empty<SkillAction>();
        [SerializeReference, FlowBranch("不满足")] public SkillAction[] whenFalse = Array.Empty<SkillAction>();
        public GameplayTagRequirements BuildTagRequirements() =>
            GameplayTagRequirements.Create(TagAssetUtility.ToSet(blockedTags).AsSpan(), TagAssetUtility.ToSet(requiredTags).AsSpan());
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            builder.RequireKind(booleanParameterId, SkillParameterKind.Bool);
            if (compareAttribute) builder.Number(value);
            var requirements = BuildTagRequirements();
            bool needTarget = requireTarget, target = testTarget, compare = compareAttribute;
            var id = new AttributeId(attribute); var op = comparison; var number = value;
            string booleanId = booleanParameterId;
            string branchId = Id;
            var yes = builder.Build(whenTrue); var no = builder.Build(whenFalse);
            bool Test(SkillExecutionContext context)
            {
                if (!string.IsNullOrEmpty(booleanId) && !context.Values.Parameters.Get(booleanId).Boolean) return false;
                if (needTarget && context.Target == null) return false;
                var unit = target ? context.Target : context.Source;
                if (unit == null || !requirements.Matches(unit.OwnedTags)) return false;
                if (!compare) return true;
                float a = unit.Attributes.GetCurrent(id), b = number.Resolve(context.Values);
                return Compare(op,a,b);
            }
            return Define("条件分支", context => {bool taken=Test(context); SkillActionDebug.Branch(context,branchId,taken);context.Execute(taken ? yes : no);});
        }
    }

    [Serializable]
    [System.ComponentModel.DisplayName("发送玩法事件")]
    public sealed class SendGameplayEventAction : SkillAction
    {
        [FlowField("事件")] public GameplayTag tag;
        public TargetQuery targets = new TargetQuery { kind = TargetQueryKind.Self };
        public bool includePosition;
        public SkillOrigin position;
        public SkillParameterKind payloadKind;
        public SkillNumber payload = new SkillNumber(0);
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            builder.Query(targets); builder.Number(payload);
            var tags = TagAssetUtility.ToSet(new[] { tag });
            if (tags.Count == 0) throw new InvalidOperationException("缺少有效的 Gameplay 事件标签。");
            var eventTag = tags[0]; var query = targets.Build(); var number = payload; var kind = payloadKind;bool located=includePosition;var origin=position;
            return Define("发送 Gameplay 事件", context =>
            {
                System.Numerics.Vector3? eventPosition=null;
                if(located){var p=context.OriginAt(origin);eventPosition=new System.Numerics.Vector3(p.x,p.y,p.z);}
                float value = number.Resolve(context.Values);
                var data = SkillParameterValue.FromNumber(kind, value);
                var buffer = context.RentBuffer();
                try
                {
                    context.Services.Searcher.Query(query, context, buffer);
                    GasAssert.That(!buffer.Saturated, "事件目标查询缓冲已满，请收窄范围或碰撞层。");
                    for (int i = 0; i < buffer.Count && context.CanContinue; i++)
                        if (buffer.IsCurrent(i)) buffer.Units[i].Events.Send(new GameplayEvent(eventTag, context.SourceIdentity, data, context.Values,eventPosition));
                }
                finally { context.ReturnBuffer(buffer); }
            });
        }
    }



    [Serializable]
    [System.ComponentModel.DisplayName("选择目标")]
    public sealed class SelectTargetsAction : SkillAction
    {
        [FlowField("目标", nameof(TargetQuery.kind))] public TargetQuery targets = new();
        [SerializeReference, FlowBranch("每个目标")] public SkillAction[] actions = Array.Empty<SkillAction>();
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            builder.Query(targets);
            var query = targets.Build(); var branch = builder.Build(actions);
            return Define("选择目标", context =>
            {
                var buffer = context.RentBuffer();
                try
                {
                    context.Services.Searcher.Query(query, context, buffer);
                    GasAssert.That(!buffer.Saturated, "目标查询缓冲区已满。");
                    for (int i = 0; i < buffer.Count && context.CanContinue; i++)
                        if (buffer.IsCurrent(i)) context.Selected(buffer.Units[i], buffer.Transforms[i], buffer.Points[i]).Execute(branch);
                }
                finally { context.ReturnBuffer(buffer); }
            });
        }
    }
}
