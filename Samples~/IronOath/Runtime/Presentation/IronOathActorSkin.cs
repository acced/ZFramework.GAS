using System;
using IronOath.Combat;
using UnityEngine;

namespace IronOath.Presentation
{
    /// <summary>Optional prefab hook. Receivers only animate: they must never apply gameplay damage.</summary>
    public interface IIronOathBattlePresentation
    {
        void Bind(BattleUnit unit);
        void Present(in BattleEvent battleEvent);
    }

    [Serializable]
    public sealed class IronOathActorSkin
    {
        [Tooltip("BattleUnit.Definition.Id. Leave empty to use this as the fallback skin.")]
        public string unitDefinitionId;
        [Tooltip("World-space actor (for example SkeletonAnimation). Its root is a foot pivot.")]
        public GameObject prefab;
        [Min(.01f)] public float worldScale = 1;
        [Tooltip("Offset in world units from the board's ground anchor.")]
        public Vector3 footOffset;
        [Tooltip("True when the source art faces right. Enemies are mirrored automatically.")]
        public bool sourceFacesRight = true;
        [Tooltip("Added to row sorting. Give every renderer in a character the same relative ordering.")]
        public int sortingOffset;
    }
}
