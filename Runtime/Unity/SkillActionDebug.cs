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
        /// <summary>扩展上报的命名时机（时间轴事件、回合阶段……）：稳定 ID 与显示名。调试器据此记录与断点。</summary>
        public static event Action<SkillExecutionContext, string, string> PointReached;
        public static void Point(SkillExecutionContext context, string id, string label) => PointReached?.Invoke(context, id, label);
        public static event Action<SkillExecutionContext, TargetQueryBuffer> TargetsSelected;
        internal static void Query(SkillExecutionContext context, TargetQueryBuffer buffer) => TargetsSelected?.Invoke(context, buffer);
    }
}
