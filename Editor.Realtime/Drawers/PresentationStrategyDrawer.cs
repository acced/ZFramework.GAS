using UnityEditor;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    [CustomPropertyDrawer(typeof(AnimationBackend), true), CustomPropertyDrawer(typeof(SkillRuntimeTrack), true)]
    public sealed class PresentationStrategyDrawer : StrategyDrawer { }
}
