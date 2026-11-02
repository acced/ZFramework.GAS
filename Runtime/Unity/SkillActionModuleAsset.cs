using System;
using System.Collections.Generic;
using GameplayTags;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [CreateAssetMenu(menuName = "ZFramework GAS/Skill Action Module")]
    public sealed class SkillActionModuleAsset : ScriptableObject
    {
        public SkillParameterSetAsset parameters;
        [SerializeReference] public SkillAction[] actions = Array.Empty<SkillAction>();
        public SkillActionDefinition[] Build() => new SkillActionBuildContext(this).Module(this);
        private void OnValidate() => DefinitionSource.InvalidateDependents(this);
    }
}

