using BaseLib.Config;
namespace FendaoZhengfeng.Mod;
public sealed partial class RaceConfig
{
    [ConfigIgnore] public static bool Effect_exhaustion { get => EnabledIds().Contains("exhaustion"); set => SetEffect("exhaustion", value); }
    [ConfigIgnore] public static bool Effect_imbalance { get => EnabledIds().Contains("imbalance"); set => SetEffect("imbalance", value); }
    [ConfigIgnore] public static bool Effect_iron_curtain { get => EnabledIds().Contains("iron_curtain"); set => SetEffect("iron_curtain", value); }
    [ConfigIgnore] public static bool Effect_timidity { get => EnabledIds().Contains("timidity"); set => SetEffect("timidity", value); }
    [ConfigIgnore] public static bool Effect_fortune_dice { get => EnabledIds().Contains("fortune_dice"); set => SetEffect("fortune_dice", value); }
    [ConfigIgnore] public static bool Effect_clutter { get => EnabledIds().Contains("clutter"); set => SetEffect("clutter", value); }
    [ConfigIgnore] public static bool Effect_abet { get => EnabledIds().Contains("abet"); set => SetEffect("abet", value); }
    [ConfigIgnore] public static bool Effect_carapace { get => EnabledIds().Contains("carapace"); set => SetEffect("carapace", value); }
    [ConfigIgnore] public static bool Effect_overgrowth { get => EnabledIds().Contains("overgrowth"); set => SetEffect("overgrowth", value); }
    [ConfigIgnore] public static bool Effect_barbs { get => EnabledIds().Contains("barbs"); set => SetEffect("barbs", value); }
    [ConfigIgnore] public static bool Effect_hard_shell { get => EnabledIds().Contains("hard_shell"); set => SetEffect("hard_shell", value); }
    [ConfigIgnore] public static bool Effect_ritual { get => EnabledIds().Contains("ritual"); set => SetEffect("ritual", value); }
    [ConfigIgnore] public static bool Effect_all_in { get => EnabledIds().Contains("all_in"); set => SetEffect("all_in", value); }
    [ConfigIgnore] public static bool Effect_tomorrow { get => EnabledIds().Contains("tomorrow"); set => SetEffect("tomorrow", value); }
    [ConfigIgnore] public static bool Effect_greedy_pocket { get => EnabledIds().Contains("greedy_pocket"); set => SetEffect("greedy_pocket", value); }
    [ConfigIgnore] public static bool Effect_bitter_sweet { get => EnabledIds().Contains("bitter_sweet"); set => SetEffect("bitter_sweet", value); }
    [ConfigIgnore] public static bool Effect_burn_bridges { get => EnabledIds().Contains("burn_bridges"); set => SetEffect("burn_bridges", value); }
    [ConfigIgnore] public static bool Effect_borrowed_time { get => EnabledIds().Contains("borrowed_time"); set => SetEffect("borrowed_time", value); }
    [ConfigIgnore] public static bool Effect_bloom_decay { get => EnabledIds().Contains("bloom_decay"); set => SetEffect("bloom_decay", value); }
    [ConfigIgnore] public static bool Effect_shared_fortune { get => EnabledIds().Contains("shared_fortune"); set => SetEffect("shared_fortune", value); }
    [ConfigIgnore] public static bool Effect_adversity_profit { get => EnabledIds().Contains("adversity_profit"); set => SetEffect("adversity_profit", value); }
    [ConfigIgnore] public static bool Effect_war_feeds_war { get => EnabledIds().Contains("war_feeds_war"); set => SetEffect("war_feeds_war", value); }
    [ConfigIgnore] public static bool Effect_catch_up { get => EnabledIds().Contains("catch_up"); set => SetEffect("catch_up", value); }
    [ConfigIgnore] public static bool Effect_shared_misery { get => EnabledIds().Contains("shared_misery"); set => SetEffect("shared_misery", value); }
    [ConfigIgnore] public static bool Effect_retaliation { get => EnabledIds().Contains("retaliation"); set => SetEffect("retaliation", value); }
    [ConfigIgnore] public static bool Effect_siphon { get => EnabledIds().Contains("siphon"); set => SetEffect("siphon", value); }
    [ConfigIgnore] public static bool Effect_battle_hardened { get => EnabledIds().Contains("battle_hardened"); set => SetEffect("battle_hardened", value); }
    [ConfigIgnore] public static bool Effect_death_defiance { get => EnabledIds().Contains("death_defiance"); set => SetEffect("death_defiance", value); }
    [ConfigIgnore] public static bool Effect_scapegoat { get => EnabledIds().Contains("scapegoat"); set => SetEffect("scapegoat", value); }
}
