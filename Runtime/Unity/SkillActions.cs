using System;
using System.Collections.Generic;
using GameplayTags;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
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
        protected SkillActionDefinition Define(string label, Action<SkillExecutionContext, bool> validate,
            Action<SkillExecutionContext> execute) => new DelegateSkillAction(id,
                string.IsNullOrEmpty(displayName) ? label : displayName, validate, execute);
    }

    [Serializable]
    public sealed class RunSpawnerAction : SkillAction
    {
        [SerializeReference] public TaskSpawner spawner;
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            // An arbitrary project spawner can start ability-owned work.
            builder.RequireAbilityTask(Id);
            if(spawner==null)throw new InvalidOperationException("缺少任务执行器。");
            var delegateAction=spawner.Build();
            return Define("执行任务",delegateAction.Validate,delegateAction.Execute);
        }
    }

    public abstract class SkillActionDefinition
    {
        public string Id { get; }
        public string Label { get; }
        protected SkillActionDefinition(string id, string label) { Id = id; Label = label; }
        public abstract void Validate(SkillExecutionContext context, bool deferredTarget = false);
        public abstract void Execute(SkillExecutionContext context);
    }
    internal sealed class DelegateSkillAction : SkillActionDefinition
    {
        private readonly Action<SkillExecutionContext, bool> _validate;
        private readonly Action<SkillExecutionContext> _execute;
        public DelegateSkillAction(string id, string label, Action<SkillExecutionContext, bool> validate,
            Action<SkillExecutionContext> execute) : base(id, label) { _validate = validate; _execute = execute; }
        public override void Validate(SkillExecutionContext context, bool deferredTarget = false)
        {
            try { _validate?.Invoke(context, deferredTarget); }
            catch (SkillActionException) { throw; }
            catch (Exception ex) { throw new SkillActionException(Id, Label + ": " + ex.Message, ex); }
        }
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
        public SkillActionBuildContext(ScriptableObject owner = null) { _owner = owner; }
        internal SkillActionBuildContext(ScriptableObject owner,HashSet<string> entryIds) { _owner=owner;_entryIds=entryIds; }
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
        public SkillActionDefinition[] Module(SkillActionModuleAsset module)
        {
            if (module == null) throw new InvalidOperationException("缺少动作模块。");
            DependsOn(module); DependsOn(module.parameters);
            if (!_modules.Add(module)) throw new InvalidOperationException($"模块循环引用：{module.name}");
            var parentIds=_scopeIds; _scopeIds=new HashSet<string>();
            try { return Build(module.actions); }
            finally { _scopeIds=parentIds; _modules.Remove(module); }
        }
        public static void Validate(IReadOnlyList<SkillActionDefinition> actions, SkillExecutionContext context, bool deferred = false)
        { for (int i = 0; i < actions.Count; i++) actions[i].Validate(context, deferred); }
        internal static void Effect(GameplayEffectDefinition effect, SkillExecutionContext context, AttributeSet target)
            => context.Effect(effect).Validate(target);
    }

    [Serializable]
    public sealed class ApplyEffectAction : SkillAction
    {
        internal static SkillActionDefinition Marker(string id, GameplayEffectDefinition effect, bool self)
            => new DelegateSkillAction(id, "施加标记效果", (context, deferred) =>
            {
                if (!self && !deferred && context.Target == null) throw new InvalidOperationException("缺少固定目标。");
                SkillActionBuildContext.Effect(effect, context, (self ? context.Source : context.Target)?.Attributes);
            }, context => Apply(context, effect, self ? context.Source : context.Target));
        private static void Apply(SkillExecutionContext context, GameplayEffectDefinition effect, AbilitySystemComponent target)
        { if (target == null) throw new InvalidOperationException("效果目标已失效。"); target.Effects.Apply(context.Effect(effect)); }
        public GameplayEffectAsset effect;
        public TargetQuery targets = new TargetQuery();
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            if (effect == null) throw new InvalidOperationException("缺少效果资产。");
            builder.DependsOn(effect);
            return FromDefinition(Id, effect.Definition, targets.Build());
        }
        internal static SkillActionDefinition FromDefinition(string id, GameplayEffectDefinition effect, TargetQueryDefinition query)
            => new DelegateSkillAction(id, "施加效果", (context, deferred) =>
            {
                query.Validate(context, !deferred);
                SkillActionBuildContext.Effect(effect, context, context.Target?.Attributes);
            }, context =>
            {
                var buffer = context.RentBuffer();
                try
                {
                    context.Services.Searcher.Query(query, context, buffer);
                    if (buffer.Saturated) throw new InvalidOperationException("目标查询缓冲区已满（256 个碰撞体），请收窄 LayerMask 或范围。");
                    for (int i = 0; i < buffer.Count; i++)
                    { if (!context.CanContinue) break; Apply(context, effect, buffer.Units[i]); }
                }
                finally { context.ReturnBuffer(buffer); }
            });
    }

    public enum SkillComparison { GreaterOrEqual, Greater, LessOrEqual, Less, Equal, NotEqual }
    [Serializable]
    public sealed class ConditionalAction : SkillAction
    {
        public static bool Compare(SkillComparison op,float a,float b) => op switch { SkillComparison.Greater => a > b, SkillComparison.GreaterOrEqual => a >= b,
                    SkillComparison.Less => a < b, SkillComparison.LessOrEqual => a <= b,
                    SkillComparison.Equal => a == b, _ => a != b };
        public bool requireTarget;
        [SkillParameterReference] public string booleanParameterId;
        public bool testTarget;
        public GameplayTag[] requiredTags = Array.Empty<GameplayTag>();
        public GameplayTag[] blockedTags = Array.Empty<GameplayTag>();
        public bool compareAttribute;
        [AttributeIdField] public int attribute;
        public SkillComparison comparison;
        public SkillNumber value = new SkillNumber(0);
        [SerializeReference, FlowBranch("满足")] public SkillAction[] whenTrue = Array.Empty<SkillAction>();
        [SerializeReference, FlowBranch("不满足")] public SkillAction[] whenFalse = Array.Empty<SkillAction>();
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            var required = TagAssetUtility.ToSet(requiredTags); var blocked = TagAssetUtility.ToSet(blockedTags);
            var requirements = GameplayTagRequirements.Create(blocked.AsSpan(), required.AsSpan());
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
            return Define("条件分支", (context, deferred) =>
            {
                if (!string.IsNullOrEmpty(booleanId) && context.Values.Parameters.Get(booleanId).Kind != SkillParameterKind.Bool)
                    throw new InvalidOperationException("条件开关需要 Bool 参数: " + booleanId);
                if (compare) number.Resolve(context.Values);
                SkillActionBuildContext.Validate(yes, context, true); SkillActionBuildContext.Validate(no, context, true);
                if (!deferred) SkillActionBuildContext.Validate(Test(context) ? yes : no, context);
            }, context => {bool taken=Test(context); SkillActionDebug.Branch(context,branchId,taken);context.Execute(taken ? yes : no);});
        }
    }

    [Serializable]
    public sealed class SendGameplayEventAction : SkillAction
    {
        public GameplayTag tag;
        public TargetQuery targets = new TargetQuery { kind = TargetQueryKind.Self };
        public SkillParameterKind payloadKind;
        public SkillNumber payload = new SkillNumber(0);
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            var tags = TagAssetUtility.ToSet(new[] { tag });
            if (tags.Count == 0) throw new InvalidOperationException("缺少有效的 Gameplay 事件标签。");
            var eventTag = tags[0]; var query = targets.Build(); var number = payload; var kind = payloadKind;
            return Define("发送 Gameplay 事件", (context, deferred) => { query.Validate(context, !deferred); SkillParameterValue.FromNumber(kind, number.Resolve(context.Values)); }, context =>
            {
                float value = number.Resolve(context.Values);
                var data = SkillParameterValue.FromNumber(kind, value);
                var buffer = context.RentBuffer();
                try
                {
                    context.Services.Searcher.Query(query, context, buffer);
                    if (buffer.Saturated) throw new InvalidOperationException("事件目标查询缓冲已满，请收窄范围或碰撞层。");
                    for (int i = 0; i < buffer.Count && context.CanContinue; i++)
                        buffer.Units[i].Events.Send(new GameplayEvent(eventTag, context.SourceIdentity, data, context.Values));
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
    public sealed class CallModuleAction : ParameterScopeAction
    {
        public SkillActionModuleAsset module;
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            SkillActionDefinition[] actions;
            try { actions = builder.Module(module); }
            catch (Exception error) { throw new SkillActionException(Id, "模块 " + (module != null ? module.name : "<缺失>") + ": " + error.Message, error); }
            // Resolve level tables at cast start, never hold mutable authoring data in running definitions.
            var parameters = module.parameters == null ? Array.Empty<SkillParameter>() : module.parameters.parameters;
            return Bind(actions,parameters,module.name);
        }
    }
    [Serializable]
    public sealed class InlineParameterScopeAction : ParameterScopeAction
    {
        public SkillParameter[] parameters=Array.Empty<SkillParameter>();
        [SerializeReference, FlowBranch("动作")] public SkillAction[] actions=Array.Empty<SkillAction>();
        public override SkillActionDefinition Build(SkillActionBuildContext builder)=>Bind(builder.Build(actions),parameters,"内联参数");
    }
    [Serializable]
    public abstract class ParameterScopeAction : SkillAction
    {
        public ModuleParameterBinding[] bindings = Array.Empty<ModuleParameterBinding>();
        protected SkillActionDefinition Bind(SkillActionDefinition[] actions,SkillParameter[] parameters,string moduleName)
        {
            var defaults = new ModuleDefaults(parameters);
            var mapped = (ModuleParameterBinding[])bindings.Clone();
            var cacheKey = new object();
            string callId = Id;
            SkillParameterValues BindNew(SkillExecutionContext context)
            {
                var values = defaults.Resolve(context.Values.Level);
                foreach (var binding in mapped)
                {
                    if (!values.TryGetValue(binding.moduleParameterId, out var expected)) throw new InvalidOperationException("模块参数不存在: " + binding.moduleParameterId);
                    var value = context.Values.Parameters.Get(binding.callerParameterId);
                    if (value.Kind != expected.Kind) throw new InvalidOperationException("模块参数类型不匹配: " + binding.moduleParameterId);
                    values[binding.moduleParameterId] = value;
                }
                return new SkillParameterValues(values);
            }
            Func<SkillExecutionContext, SkillParameterValues> createScope = BindNew;
            SkillExecutionContext Bind(SkillExecutionContext context) => context.ModuleScope(cacheKey, createScope, callId, moduleName);
            return Define("调用动作模块", (context, deferred) =>
            {
                var scope = Bind(context);
                try { SkillActionBuildContext.Validate(actions, scope, deferred); }
                catch (Exception error) { throw new SkillActionException(callId, "模块 " + moduleName + ": " + error.Message, error); }
                finally { context.ReleaseModule(cacheKey, scope); }
            }, context =>
            {
                var scope = Bind(context);
                try { context.ExecuteModule(actions, scope); }
                finally { context.ReleaseModule(cacheKey, scope); }
            });
        }
        private sealed class ModuleDefaults
        {
            private readonly (string id, SkillParameterKind kind, float value, float[] levels)[] _items;
            public ModuleDefaults(SkillParameter[] parameters)
            {
                _items = new (string, SkillParameterKind, float, float[])[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                { var p = parameters[i]; _items[i] = (p.Id, p.kind, p.defaultValue, (float[])p.levels.Clone()); }
            }
            public Dictionary<string, SkillParameterValue> Resolve(int level)
            {
                var values = new Dictionary<string, SkillParameterValue>();
                foreach (var p in _items)
                {
                    float v = p.levels.Length > 0 ? p.levels[Mathf.Clamp(level - 1, 0, p.levels.Length - 1)] : p.value;
                    values.Add(p.id, SkillParameterValue.FromNumber(p.kind, v));
                }
                return values;
            }
        }
    }
}
