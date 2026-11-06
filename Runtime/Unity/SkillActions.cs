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
        private readonly HashSet<string> _entryIds;
        private SkillParameter[] _parameters;
        private SkillParameter[] Parameters => _parameters ?? (_owner switch {
            GameplayAbilityAsset skill => skill.parameters,
            SkillActionModuleAsset module => module.parameters?.parameters,
            _ => null
        });
        private SkillParameter Parameter(string id)
        {
            foreach (var p in Parameters ?? Array.Empty<SkillParameter>()) if (p.Id == id) return p;
            if (_parameters == null && _owner is GameplayAbilityAsset skill && skill.parameterSet != null)
                foreach (var p in skill.parameterSet.parameters) if (p.Id == id) return p;
            throw new InvalidOperationException("缺少参数: " + id);
        }
        public SkillActionBuildContext(ScriptableObject owner = null) { _owner = owner; }
        /// <param name="entryIds">多个入口（例如时间轴上的多个事件）共用的动作 ID 集合，用于跨入口查重。</param>
        public SkillActionBuildContext(ScriptableObject owner,HashSet<string> entryIds) { _owner=owner;_entryIds=entryIds; }
        public void Number(SkillNumber number, float min = float.MinValue, float max = float.MaxValue, bool integer = false)
        {
            if (!string.IsNullOrEmpty(number.parameterId)) {
                if (_owner is not GameplayAbilityAsset && Parameters == null) return;
                var p = Parameter(number.parameterId);
                if (p.kind == SkillParameterKind.Bool) throw new InvalidOperationException("数值绑定不能使用 Bool。");
                if (p.levels == null || p.levels.Length == 0) Number(new SkillNumber(p.defaultValue * number.coefficient), min, max, integer);
                else foreach (var v in p.levels) Number(new SkillNumber(v * number.coefficient), min, max, integer);
                return;
            }
            float value = number.value;
            if (!float.IsFinite(value) || value < min || value > max || integer && value != Mathf.Floor(value))
                throw new InvalidOperationException("动作数值超出允许范围。");
        }
        internal void RequireKind(string id, SkillParameterKind kind)
        {
            if (!string.IsNullOrEmpty(id) && (_owner is GameplayAbilityAsset || Parameters != null) && Parameter(id).kind != kind)
                throw new InvalidOperationException($"参数 {id} 必须是 {kind}。");
        }
        internal void CheckBinding(ModuleParameterBinding binding, SkillParameter[] locals)
        {
            var local = Array.Find(locals, p => p.Id == binding.moduleParameterId)
                ?? throw new InvalidOperationException("模块参数不存在: " + binding.moduleParameterId);
            if (string.IsNullOrEmpty(binding.callerParameterId)) throw new InvalidOperationException("未指定调用方参数。");
            RequireKind(binding.callerParameterId, local.kind);
        }
        public void Query(TargetQuery query)
        {
            if (query == null || query.maxTargets < 1 || query.maxTargets > 256) throw new InvalidOperationException("目标查询容量必须为 1–256。");
            Number(query.radius, 0); Number(query.width, 0); Number(query.height, 0); Number(query.angle, 0, 360);
        }
        public void DependsOn(ScriptableObject asset) => DefinitionSource.DependsOn(_owner, asset);
        private readonly HashSet<SkillActionModuleAsset> _modules = new();
        private int _depth;
        private HashSet<string> _scopeIds;
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
            if (actions == null || actions.Length == 0) return Array.Empty<SkillActionDefinition>();
            if (++_depth > 8) { _depth--; throw new InvalidOperationException("动作／模块嵌套不能超过八层。"); }
            try
            {
                var built = new SkillActionDefinition[actions.Length];
                if(_depth==1 || _scopeIds==null) _scopeIds=_depth==1 && _entryIds!=null?_entryIds:new HashSet<string>();
                var ids = _scopeIds;
                for (int i = 0; i < actions.Length; i++)
                {
                    var action = actions[i] ?? throw new InvalidOperationException("动作列表包含空项。");
                    if (string.IsNullOrEmpty(action.Id) || !ids.Add(action.Id)) throw new SkillActionException(action.Id, "动作 ID 缺失或重复。");
                    try { built[i] = action.Build(this); }
                    catch (SkillActionException) { throw; }
                    catch (Exception exception) { throw new SkillActionException(action.Id, exception.Message, exception); }
                }
                return built;
            }
            finally { _depth--; }
        }
        public SkillActionDefinition[] Scope(SkillAction[] actions, SkillParameter[] parameters)
        {
            var previous = _parameters; _parameters = parameters;
            try { return Build(actions); } finally { _parameters = previous; }
        }
        public SkillActionDefinition[] Module(SkillActionModuleAsset module)
        {
            if (module == null) throw new InvalidOperationException("缺少动作模块。");
            DependsOn(module); DependsOn(module.parameters);
            if (!_modules.Add(module)) throw new InvalidOperationException($"模块循环引用：{module.name}");
            var parentIds=_scopeIds; _scopeIds=new HashSet<string>();
            var parameters = _parameters; _parameters = module.parameters?.parameters ?? Array.Empty<SkillParameter>();
            try { return Build(module.actions); }
            finally { _parameters = parameters; _scopeIds=parentIds; _modules.Remove(module); }
        }
    }

    [Serializable]
    [System.ComponentModel.DisplayName("施加效果")]
    public sealed class ApplyEffectAction : SkillAction
    {
        private static void Apply(SkillExecutionContext context, GameplayEffectDefinition effect, AbilitySystemComponent target)
        { if (target == null) throw new InvalidOperationException("效果目标已失效。"); target.Effects.Apply(context.Effect(effect)); }
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
                    { if (!context.CanContinue) break; Apply(context, effect, buffer.Units[i]); }
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
        public SkillSpawnPosition position;
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
                        buffer.Units[i].Events.Send(new GameplayEvent(eventTag, context.SourceIdentity, data, context.Values,eventPosition));
                }
                finally { context.ReturnBuffer(buffer); }
            });
        }
    }



    [Serializable]
    public struct ModuleParameterBinding
    {
        public string moduleParameterId;
        public string callerParameterId;
    }
    [Serializable]
    [System.ComponentModel.DisplayName("调用共享模块")]
    public sealed class CallModuleAction : ParameterScopeAction
    {
        [FlowField("模块")] public SkillActionModuleAsset module;
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            SkillActionDefinition[] actions;
            try { actions = builder.Module(module); }
            catch (Exception error) { throw new SkillActionException(Id, "模块 " + (module != null ? module.name : "<缺失>") + ": " + error.Message, error); }
            // Resolve level tables at cast start, never hold mutable authoring data in running definitions.
            var parameters = module.parameters == null ? Array.Empty<SkillParameter>() : module.parameters.parameters;
            return Bind(builder, actions, parameters);
        }
    }
    [Serializable]
    [System.ComponentModel.DisplayName("内联参数")]
    public sealed class InlineParameterScopeAction : ParameterScopeAction
    {
        public SkillParameter[] parameters=Array.Empty<SkillParameter>();
        [SerializeReference, FlowBranch("动作")] public SkillAction[] actions=Array.Empty<SkillAction>();
        public override SkillActionDefinition Build(SkillActionBuildContext builder)=> Bind(builder, builder.Scope(actions, parameters), parameters);
    }
    [Serializable]
    public abstract class ParameterScopeAction : SkillAction
    {
        public ModuleParameterBinding[] bindings = Array.Empty<ModuleParameterBinding>();
        public static SkillParameterValues ResolveParameters(EffectEvaluationContext caller, SkillParameter[] defaults, ModuleParameterBinding[] bindings)
        {
            if (defaults == null || defaults.Length == 0) return SkillParameterValues.Empty;
            var values = new Dictionary<string, SkillParameterValue>(defaults.Length);
            SkillParameterSetAsset.AddTo(values, defaults, caller.Level);
            foreach (var binding in bindings)
                values[binding.moduleParameterId] = caller.Parameters.Get(binding.callerParameterId);
            return new SkillParameterValues(values);
        }
        protected SkillActionDefinition Bind(SkillActionBuildContext builder, SkillActionDefinition[] actions, SkillParameter[] parameters)
        {
            foreach (var binding in bindings) builder.CheckBinding(binding, parameters);
            return new ModuleDefinition(this, actions, parameters);
        }
    }

    internal sealed class ModuleDefinition : SkillActionDefinition
    {
        internal readonly SkillActionDefinition[] Actions;
        private readonly ModuleParameterBinding[] _bindings;
        private readonly SkillParameter[] _defaults;
#if UNITY_EDITOR
        internal readonly bool IsExternalCall;
#endif
        internal ModuleDefinition(ParameterScopeAction call, SkillActionDefinition[] actions, SkillParameter[] parameters)
            : base(call.Id, string.IsNullOrEmpty(call.displayName) ? "调用动作模块" : call.displayName)
        {
            Actions = actions;
#if UNITY_EDITOR
            IsExternalCall = call is CallModuleAction;
#endif
            _bindings = (ModuleParameterBinding[])call.bindings.Clone();
            _defaults = Array.ConvertAll(parameters, p => p.Snapshot());
        }
        internal SkillParameterValues Resolve(EffectEvaluationContext caller)
            => ParameterScopeAction.ResolveParameters(caller, _defaults, _bindings);
        public override void Execute(SkillExecutionContext context) => context.ExecuteModule(this);
    }
}
