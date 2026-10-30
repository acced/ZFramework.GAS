using System.Linq;
using GameplayTags;
using ZFramework.GAS.Samples.BasicCombat;

namespace ZFramework.GAS.Editor.Tests
{
    internal static class TestTags
    {
        // 标签注册表在域内只初始化一次，所有测试使用同一份声明，避免依赖执行顺序。
        public static void Initialize() => GameplayTagManager.Initialize(SampleTags.All.Concat(new[]
        { "Test.Cooldown", "Test.Window", "Test.Other", "Test.Cue", "Test.VFX", "Test.VFX.Ground" })
            .Select(name => new GameplayTagRegistration(name)).ToArray());
    }
}
