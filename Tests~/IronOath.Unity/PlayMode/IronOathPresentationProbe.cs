using IronOath.Combat;
using IronOath.Presentation;
using UnityEngine;

namespace IronOath.UnityTests
{
    // Test-only receiver on an inactive world prefab. The shipping sample does not depend on it.
    public sealed class IronOathPresentationProbe : MonoBehaviour, IIronOathBattlePresentation
    {
        public BattleUnit Unit { get; private set; }
        public int Events { get; private set; }
        public void Bind(BattleUnit unit) => Unit = unit;
        public void Present(in BattleEvent battleEvent) => Events++;
    }
}
