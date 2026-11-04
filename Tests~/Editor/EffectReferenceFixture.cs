using UnityEngine;
using ZFramework.GAS.Unity;
namespace ZFramework.GAS.Editor.Tests
{
    // A consumer outside the GAS asset types supported by the skill graph.
    public sealed class EffectReferenceFixture : ScriptableObject { public GameplayEffectAsset effect; public GameplayAbilityAsset skill; }
}
