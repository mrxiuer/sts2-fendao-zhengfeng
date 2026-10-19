using BaseLib.Config;
using FendaoZhengfeng.Core;
using Godot;

namespace FendaoZhengfeng.Mod;

public sealed partial class RaceConfig : ModConfig
{
    [ConfigIgnore] public static RaceConfig Instance { get; private set; } = null!;
    public static bool ModeEnabled { get; set; } = true;
    public static bool DebugMode { get; set; }
    public static string DisabledEffects { get; set; } = "";
    public RaceConfig() : base("FendaoZhengfeng.cfg") { }
    internal static void Register()
    {
        Instance = new RaceConfig(); Instance.Load();
        ModConfigRegistry.Register(ModEntry.Id, Instance);
    }
    internal static HashSet<string> EnabledIds() => Catalog.All.Select(e => e.Id).Except(DisabledEffects.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToHashSet();
    internal static bool DebugVisible => RaceSession.Current?.Engine.State.DebugMode ?? DebugMode;
    internal static void SetEffect(string id, bool enabled)
    {
        var disabled = DisabledEffects.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (enabled) disabled.Remove(id); else disabled.Add(id);
        DisabledEffects = string.Join(',', disabled.Order());
    }
    public override void SetupConfigUI(Control optionContainer)
    {
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 12); optionContainer.AddChild(list);
        AddToggle(list, nameof(ModeEnabled), "开启分道争锋模式", "多人新局以房主配置为准，所有玩家都须安装并启用本模组。配置更改在下一局生效。");
        AddToggle(list, nameof(DebugMode), "调试模式", "开启后显示顶部测试入口；正式联机不提供注入效果、金币等测试操作。");
        var heading = CreateRawLabelControl("效果列表", 32); heading.CustomMinimumSize = new Vector2(0, 52); list.AddChild(heading);
        var hint = CreateRawLabelControl("勾选后可进入候选池；取消后不会抽到。禁用优先于保证位。", 23); hint.CustomMinimumSize = new Vector2(0, 56); hint.FitContent = true; list.AddChild(hint);
        foreach (var effect in Catalog.All)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 84) };
            row.AddThemeConstantOverride("separation", 18);
            row.AddChild(new TextureRect { Texture = EffectIcons.Get(effect.Id), CustomMinimumSize = new Vector2(64, 64), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
            string kind = effect.Kind switch { EffectKind.Curse => "诅咒 · 对手", EffectKind.Contract => "双刃契约 · 自己", _ => "增益 · 自己" };
            var text = CreateRawLabelControl(effect.Name + " · " + kind + " · " + RaceUi.TierName(effect.Tier) + "\n" + effect.Description, 23);
            text.CustomMinimumSize = new Vector2(650, 95); text.FitContent = true; text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(text);
            row.AddChild(CreateRawTickboxControl(typeof(RaceConfig).GetProperty("Effect_" + effect.Id)!));
            list.AddChild(row);
        }
    }
    private void AddToggle(Control parent, string property, string title, string description)
    {
        var row = new HBoxContainer();
        var label = CreateRawLabelControl(title + "\n" + description, 25);
        label.CustomMinimumSize = new Vector2(750, 130); label.FitContent = true; label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(label); row.AddChild(CreateRawTickboxControl(typeof(RaceConfig).GetProperty(property)!)); parent.AddChild(row);
    }
}

[HarmonyLib.HarmonyPatch(typeof(BaseLib.Config.ModConfig), nameof(BaseLib.Config.ModConfig.Save))]
internal static class VerificationConfigSavePatch
{
    static bool Prefix(BaseLib.Config.ModConfig __instance) => __instance is not RaceConfig || System.Environment.GetEnvironmentVariable("FDZF_NATIVE_VERIFY") != "1";
}

[HarmonyLib.HarmonyPatch(typeof(BaseLib.Config.UI.NModConfigSubmenu), "GetModTitle")]
internal static class ConfigTitlePatch
{
    static bool Prefix(BaseLib.Config.ModConfig config, ref string __result)
    { if (config is not RaceConfig) return true; __result = "分道争锋"; return false; }
}
