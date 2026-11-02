using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [CreateAssetMenu(menuName = "ZFramework GAS/Ability Set")]
    public sealed class AbilitySetAsset : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public GameplayAbilityAsset skill;
            [Min(1)] public int level = 1;
            public SkillParameterOverride[] parameters = Array.Empty<SkillParameterOverride>();
        }
        public Entry[] abilities = Array.Empty<Entry>();
        public void GrantTo(SkillRunner runner)
        {
            if (runner == null) throw new ArgumentNullException(nameof(runner));
            runner.GrantSet(this);
        }
        public void RevokeFrom(SkillRunner runner)
        { if (runner == null) throw new ArgumentNullException(nameof(runner)); runner.RevokeSource(this); }
    }
}
