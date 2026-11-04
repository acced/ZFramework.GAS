using UnityEngine;

namespace ZFramework.GAS.Tests
{
    public sealed class HandoffEntryProbe : StateMachineBehaviour
    {
        public static int Entries;
        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex) => Entries++;
    }
}
