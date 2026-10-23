using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace FendaoZhengfeng.Mod;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const string Id = "FendaoZhengfeng";
    public static void Initialize()
    {
        RaceConfig.Register();
        new Harmony(Id).PatchAll(typeof(ModEntry).Assembly);
        Godot.Callable.From(() =>
        {
            if (MegaCrit.Sts2.Core.Nodes.NGame.Instance is { } game) { RaceUi.Install(game); NativeVerification.MaybeRun(game); }
        }).CallDeferred();
        Godot.GD.Print(L10n.T("[分道争锋] 已加载开发预览版 0.5.1，配置位于模组配置中的独立分道争锋条目。"));
    }
}
