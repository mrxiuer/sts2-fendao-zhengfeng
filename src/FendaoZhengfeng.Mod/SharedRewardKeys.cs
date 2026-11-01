using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;

namespace FendaoZhengfeng.Mod;

// Bind before native BeginRewardsSet can replay an already buffered remote selection.
[HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.BeginRewardsSet))]
internal static class SharedRewardKeys
{
    static void Prefix(RewardsSetSynchronizer __instance, RewardsSet set)
    {
        if (RaceSession.Current?.Engine.State.Synchronous != true) return;
        var state = AccessTools.Method(typeof(RewardsSetSynchronizer), "GetRewardStateForPlayer").Invoke(__instance, [set.Player])!;
        int setId = (int)AccessTools.Field(state.GetType(), "nextId").GetValue(state)!;
        string owner = set.Player.NetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string key = RaceRuntime.RoomKey + "|shared:" + owner + ":" + setId;
        for (int index = 0; index < set.Rewards.Count; index++)
            RaceRuntime.BindRewardKey(set.Rewards[index], key + ":" + index);
    }
}
