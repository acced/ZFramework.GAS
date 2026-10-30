namespace ZFramework.GAS.Samples.BasicCombat
{
    /// <summary>示例用到的全部标签。生成器会把它们写进项目的标签配置并重新生成注册表。</summary>
    public static class SampleTags
    {
        public const string AbilitySlash = "Ability.Slash";
        public const string AbilityBolt = "Ability.Bolt";
        public const string AbilityWarcry = "Ability.Warcry";
        public const string AbilitySanctuary = "Ability.Sanctuary";
        public const string AbilitySummon = "Ability.Summon";
        public const string CooldownSlash = "Cooldown.Sample.Slash";
        public const string CooldownBolt = "Cooldown.Sample.Bolt";
        public const string CooldownWarcry = "Cooldown.Sample.Warcry";
        public const string CooldownSanctuary = "Cooldown.Sample.Sanctuary";
        public const string CooldownSummon = "Cooldown.Sample.Summon";
        public const string SuperArmor = "State.SuperArmor";
        public const string Channeling = "State.Channeling";
        public const string ComboWindow = "Window.Combo";
        public const string CueSwordTrail = "Cue.Sword.Trail";
        public const string CueBoltCharge = "Cue.Bolt.Charge";
        public const string CueWarcryAura = "Cue.Warcry.Aura";
        public const string CueSanctuary = "Cue.Sanctuary.Circle";
        public const string CueSummon = "Cue.Summon.Circle";
        public const string CueHit = "Cue.Hit";
        public const string CueGroundTest = "Cue.Ground.Sigil";

        public static readonly string[] All =
        {
            AbilitySlash, AbilityBolt, AbilityWarcry, AbilitySanctuary, AbilitySummon,
            CooldownSlash, CooldownBolt, CooldownWarcry, CooldownSanctuary, CooldownSummon,
            SuperArmor, Channeling, ComboWindow,
            CueSwordTrail, CueBoltCharge, CueWarcryAura, CueSanctuary, CueSummon, CueHit, CueGroundTest,
        };
    }
}
