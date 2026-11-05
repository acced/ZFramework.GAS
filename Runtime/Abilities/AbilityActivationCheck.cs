using GameplayTags;

namespace ZFramework.GAS
{
    public readonly struct AbilityActivationCheck
    {
        public readonly AbilityActivationResult Result;
        public readonly GameplayTag Tag;
        public readonly AttributeId Attribute;
        public readonly float Amount;
        public readonly string Message;
        public bool Succeeded => Result == AbilityActivationResult.Activated;
        public AbilityActivationCheck(AbilityActivationResult result,string message=null,GameplayTag tag=default,AttributeId attribute=default,float amount=0)
        {Result=result;Message=message;Tag=tag;Attribute=attribute;Amount=amount;}
    }
}
