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
        var parent = menu.GetNode<Control>("MainMenuTextButtons");
        var button = parent.GetNodeOrNull<NMainMenuTextButton>(ButtonName);
        bool available = File.Exists(RaceRecovery.ArchivePath(LocalHost));
        if (button == null && available)
        {
            var source = parent.GetNode<NMainMenuTextButton>("MultiplayerButton");
            button = (NMainMenuTextButton)source.Duplicate((int)(Node.DuplicateFlags.Scripts | Node.DuplicateFlags.Groups));
            button.Name = ButtonName; parent.AddChild(button); parent.MoveChild(button, source.GetIndex() + 1);
            AccessTools.Field(typeof(NMainMenuTextButton), "_locString").SetValue(button, NativeChoiceUi.Text(L10n.T("继续分道争锋")));
            AccessTools.Method(typeof(NMainMenuTextButton), "RefreshLabel").Invoke(button, null);
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
            {
                try { RaceRecovery.Read(LocalHost); } catch (Exception error) { RaceUi.Error(L10n.T("无法恢复比赛：") + L10n.T(error.Message)); return; }
                var submenu = menu.OpenMultiplayerSubmenu(); RaceRuntime.ResumeRequested = true; RaceRuntime.ResumeFromHostArchive = true;
                submenu.FastHost(GameMode.Standard);
                RaceUi.Notify(L10n.T("已选择恢复比赛：邀请原来的玩家，并选择原来的角色后开始。"));
            }));
        }
        if (button != null) { button.Visible = available; button.SetEnabled(available); }
    }
    internal static async Task SaveAndQuit()
    {
        var session = RaceSession.Current ?? throw new InvalidOperationException(L10n.T("比赛已结束。"));
        try
        {
            await session.Recovery.SaveHost(true);
            if (session.Transport.IsConnected) session.Transport.SendMessage(new RaceMessage { Kind = "recovery_saved_exit" });
            RaceRuntime.ResumeRequested = true;
            RaceRuntime.ResumeFromHostArchive = true;
            await NGame.Instance!.ReturnToMainMenu();
        }
        catch (Exception error) { RaceUi.Error(L10n.T("保存失败，仍留在当前比赛：") + L10n.T(error.Message)); }
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
        __instance.GetNode<Control>("%ButtonContainer/SaveAndQuit").Visible = session.IsHost;
        __instance.GetNode<Control>("%ButtonContainer/Disconnect").Visible = !session.IsHost;
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
