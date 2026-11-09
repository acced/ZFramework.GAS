using UnityEditor;
using ZFramework.GAS.Editor;

namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    internal static class BasicCombatActionPreview
    {
        [InitializeOnLoadMethod]
        private static void Register() => SkillActionPreview.Register<ShowTargetLinkAction>(
            (action, frame) => frame.Link(action.from, action.duration.Resolve(frame.Values)));
    }
}
