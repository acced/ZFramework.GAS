using System;
using System.Collections.Generic;

namespace ZFramework.GAS.Unity
{
    /// <summary>Optional editor notification for requesting a pause at the next world-frame boundary.</summary>
    public static class SkillActionDebug
    {
        public static Action<SkillExecutionContext, SkillActionDefinition> Boundary;
    }
}
