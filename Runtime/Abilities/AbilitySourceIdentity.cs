namespace ZFramework.GAS
{
    /// <summary>Identity of one incarnation of a pooled ASC. Never resolves to its replacement.</summary>
    public sealed class AbilitySourceIdentity
    {
        private readonly AbilitySystemComponent _owner;
        private readonly int _generation;
        internal AbilitySourceIdentity(AbilitySystemComponent owner) { _owner = owner; _generation = owner.Generation; }
        public AbilitySystemComponent Owner => _owner.Generation == _generation ? _owner : null;
    }
}
