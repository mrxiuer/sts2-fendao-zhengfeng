using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class RecoveryUi
{
    internal const string ButtonName = "FdzfContinueRace";
    private static string LocalHost => System.Environment.GetEnvironmentVariable("FDZF_NATIVE_VERIFY") == "1" ? "1"
        : PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform).ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static void Refresh(NMainMenu menu)
    {
        var button = menu.GetNode<Control>("MainMenuTextButtons").GetNodeOrNull<NMainMenuTextButton>(ButtonName);
        if (button != null) { button.Hide(); button.QueueFree(); }
    }
    internal static Task SaveAndQuit()
    {
        RaceRuntime.ResumeRequested = false;
        RaceRuntime.ResumeFromHostArchive = false;
        return NGame.Instance!.ReturnToMainMenu();
    }

}

[HarmonyPatch(typeof(NPauseMenu), "CloseToMenu")]
internal static class SaveRaceOnQuitPatch
{
    static bool Prefix(ref Task __result)
    {
        if (!RaceRuntime.Live || RaceSession.Current?.IsMock != false) return true;
        if (RaceSession.Current!.IsHost) __result = RecoveryUi.SaveAndQuit();
        else __result = NGame.Instance!.ReturnToMainMenu();
        return false;
    }
}
[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu._Ready))]
internal static class RacePauseButtonsPatch
{
    static void Postfix(NPauseMenu __instance)
    {
        if (!RaceRuntime.Live || RaceSession.Current is not { IsMock: false } session) return;
        __instance.GetNode<Control>("%ButtonContainer/SaveAndQuit").Visible = false;
        __instance.GetNode<Control>("%ButtonContainer/Disconnect").Visible = true;
    }
}
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu.RefreshButtons))]
internal static class ShowRaceContinuePatch
{
    static void Postfix(NMainMenu __instance)
    { try { RecoveryUi.Refresh(__instance); } catch (Exception error) { RaceUi.Error(L10n.T("读取比赛恢复入口失败：") + L10n.T(error.Message)); } }
}
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu.OpenMultiplayerSubmenu), new Type[0])]
internal static class NewRaceFromMenuPatch
{ static void Prefix() { RaceRuntime.ResumeRequested = false; RaceRuntime.ResumeFromHostArchive = false; } }
