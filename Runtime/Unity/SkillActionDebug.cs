using System;

namespace ZFramework.GAS.Unity
{
    public static class SkillActionDebug
    {
        public static event Action<SkillExecutionContext, SkillActionDefinition> Boundary;
        internal static void Begin(SkillExecutionContext context, SkillActionDefinition action) => Boundary?.Invoke(context, action);
        public static event Action<SkillExecutionContext, SkillActionDefinition, Exception> ActionFinished;
        internal static void End(SkillExecutionContext context, SkillActionDefinition action, Exception error) => ActionFinished?.Invoke(context, action, error);
        public static event Action<SkillExecutionContext, string, bool> BranchSelected;
        internal static void Branch(SkillExecutionContext context, string id, bool taken) => BranchSelected?.Invoke(context, id, taken);
        public static event Action<SkillExecutionContext, TargetQueryBuffer> TargetsSelected;
        internal static void Query(SkillExecutionContext context, TargetQueryBuffer buffer) => TargetsSelected?.Invoke(context, buffer);
    }
}
