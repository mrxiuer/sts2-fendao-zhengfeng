using System.Runtime.CompilerServices;
using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

internal sealed record SavedShopCard(string? Original, string? Modified, int[] Modifiers, int Cost,
    bool OnSale, int? Type, int? Rarity);
internal sealed record SavedShopItem(string? Packet, int Cost);
internal sealed record SavedMerchant(string RoomKey, SavedShopCard[] CharacterCards, SavedShopCard[] ColorlessCards,
    SavedShopItem[] Relics, SavedShopItem[] Potions, bool HasRemoval, bool RemovalUsed, int RemovalCost);

// The native room save has no merchant inventory. Store actual contents, never
// generation recipes: rebuilding from recipes consumes RNG and can restock sold slots.
internal static class MerchantRecovery
{
    internal const string SaveKey = "fdzf_merchant_v1";
    private static readonly ConditionalWeakTable<MerchantRoom, SavedMerchant> pending = new();
    private static string Pack(IPacketSerializable value)
    { var writer = new PacketWriter(); value.Serialize(writer); return Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition); }
    private static T Unpack<T>(string packet) where T : IPacketSerializable, new()
    { var reader = new PacketReader(); reader.Reset(Convert.FromBase64String(packet)); var value = new T(); value.Deserialize(reader); return value; }
    private static int BaseCost(MerchantEntry entry) => (int)AccessTools.Field(typeof(MerchantEntry), "_cost").GetValue(entry)!;
    private static void Cost(MerchantEntry entry, int cost) => AccessTools.Field(typeof(MerchantEntry), "_cost").SetValue(entry, cost);
    internal static SavedMerchant Capture(MerchantRoom room)
    {
        var inventory = room.GetLocalInventory();
        SavedShopCard Card(MerchantCardEntry entry)
        {
            var result = entry.CreationResult;
            return new(result == null ? null : NativeCheckpoint.EncodeCard(result.originalCard),
                result?.HasBeenModified == true ? NativeCheckpoint.EncodeCard(result.Card) : null,
                result?.ModifyingRelics.Select(relic => inventory.Player.Relics.ToList().IndexOf(relic)).ToArray() ?? [],
                BaseCost(entry), entry.IsOnSale,
                (int?)(CardType?)AccessTools.Field(typeof(MerchantCardEntry), "_cardType").GetValue(entry),
                (int?)(CardRarity?)AccessTools.Field(typeof(MerchantCardEntry), "_cardRarity").GetValue(entry));
        }
        return new(RaceRuntime.RoomKey, inventory.CharacterCardEntries.Select(Card).ToArray(), inventory.ColorlessCardEntries.Select(Card).ToArray(),
            inventory.RelicEntries.Select(entry => new SavedShopItem(entry.Model == null ? null : Pack(entry.Model.ToSerializable()), BaseCost(entry))).ToArray(),
            inventory.PotionEntries.Select(entry => new SavedShopItem(entry.Model == null ? null : Pack(entry.Model.ToSerializable(0)), BaseCost(entry))).ToArray(),
            inventory.CardRemovalEntry != null, inventory.CardRemovalEntry?.Used == true,
            inventory.CardRemovalEntry is { } removal ? BaseCost(removal) : 0);
    }
    internal static MerchantRoom FromSave(SavedMerchant save)
    {
        var room = new MerchantRoom(); pending.Add(room, save);
        RaceRuntime.BindRestoredRoom(room, save.RoomKey); return room;
    }
    internal static bool TryRestore(Player player, out MerchantInventory inventory)
    {
        inventory = null!;
        if (player.RunState.CurrentRoom is not MerchantRoom room || !pending.TryGetValue(room, out var save)) return false;
        if (player.RunState is not RunState run || run.Players.Count != 1) throw new InvalidDataException("商店恢复必须使用独立单人进度。");
        inventory = new MerchantInventory(player); var target = inventory;
        void Wire(MerchantEntry entry, int cost)
        {
            Cost(entry, cost);
            entry.PurchaseCompleted += (_, _) => { foreach (var item in target.AllEntries) item.OnMerchantInventoryUpdated(); };
        }
        void Cards(SavedShopCard[] entries, bool colorless)
        {
            var pool = colorless ? ModelDb.CardPool<ColorlessCardPool>() : player.Character.CardPool;
            var cards = pool.GetUnlockedCards(player.UnlockState, ((IRunState)run).CardMultiplayerConstraint).ToArray();
            var list = (List<MerchantCardEntry>)AccessTools.Field(typeof(MerchantInventory), colorless ? "_colorlessCardEntries" : "_characterCardEntries").GetValue(target)!;
            foreach (var saved in entries)
            {
                var entry = saved.Type is { } type ? new MerchantCardEntry(player, target, cards, (CardType)type)
                    : new MerchantCardEntry(player, target, cards, (CardRarity)(saved.Rarity ?? throw new InvalidDataException("缺少商店卡牌筛选条件。")));
                if (saved.Original != null)
                {
                    var result = new CardCreationResult(NativeCheckpoint.DecodeCard(saved.Original, run, player));
                    if (saved.Modified != null)
                    {
                        var modified = NativeCheckpoint.DecodeCard(saved.Modified, run, player);
                        if (saved.Modifiers.Length == 0) result.ModifyCard(modified);
                        foreach (int index in saved.Modifiers)
                        {
                            if (index < 0 || index >= player.Relics.Count) throw new InvalidDataException("商店卡牌的遗物修饰来源不匹配。");
                            result.ModifyCard(modified, player.Relics[index]);
                        }
                    }
                    AccessTools.PropertySetter(typeof(MerchantCardEntry), nameof(MerchantCardEntry.CreationResult)).Invoke(entry, [result]);
                }
                AccessTools.PropertySetter(typeof(MerchantCardEntry), nameof(MerchantCardEntry.IsOnSale)).Invoke(entry, [saved.OnSale]);
                Wire(entry, saved.Cost); list.Add(entry);
            }
        }
        Cards(save.CharacterCards, false); Cards(save.ColorlessCards, true);
        // Relic/potion constructors roll prices or consume relic pools. These
        // native entries have only the base player/cost fields and Model property;
        // hydrate them directly so a restore makes no factory or RNG calls.
        foreach (var saved in save.Relics)
        {
            var entry = (MerchantRelicEntry)RuntimeHelpers.GetUninitializedObject(typeof(MerchantRelicEntry));
            AccessTools.Field(typeof(MerchantEntry), "_player").SetValue(entry, player);
            AccessTools.PropertySetter(typeof(MerchantRelicEntry), nameof(MerchantRelicEntry.Model)).Invoke(entry,
                [saved.Packet == null ? null : RelicModel.FromSerializable(Unpack<SerializableRelic>(saved.Packet))]);
            Wire(entry, saved.Cost); target.AddRelicEntry(entry);
        }
        var potions = (List<MerchantPotionEntry>)AccessTools.Field(typeof(MerchantInventory), "_potionEntries").GetValue(target)!;
        foreach (var saved in save.Potions)
        {
            var entry = (MerchantPotionEntry)RuntimeHelpers.GetUninitializedObject(typeof(MerchantPotionEntry));
            AccessTools.Field(typeof(MerchantEntry), "_player").SetValue(entry, player);
            AccessTools.PropertySetter(typeof(MerchantPotionEntry), nameof(MerchantPotionEntry.Model)).Invoke(entry,
                [saved.Packet == null ? null : PotionModel.FromSerializable(Unpack<SerializablePotion>(saved.Packet))]);
            Wire(entry, saved.Cost); potions.Add(entry);
        }
        if (save.HasRemoval)
        {
            var removal = new MerchantCardRemovalEntry(player);
            if (save.RemovalUsed) removal.SetUsed(); Wire(removal, save.RemovalCost);
            AccessTools.PropertySetter(typeof(MerchantInventory), nameof(MerchantInventory.CardRemovalEntry)).Invoke(target, [removal]);
        }
        pending.Remove(room); return true;
    }
}

[HarmonyPatch(typeof(AbstractRoom), nameof(AbstractRoom.FromSerializable))]
internal static class RestoreMerchantRoomPatch
{
    static bool Prefix(SerializableRoom? serializableRoom, ref AbstractRoom? __result)
    {
        if (!RaceRuntime.Live || serializableRoom is not { RoomType: RoomType.Shop }
            || serializableRoom.EncounterState?.TryGetValue(MerchantRecovery.SaveKey, out var json) != true || json == null) return true;
        __result = MerchantRecovery.FromSave(StateJson.Deserialize<SavedMerchant>(json)); return false;
    }
}

[HarmonyPatch(typeof(MerchantInventory), nameof(MerchantInventory.CreateForNormalMerchant))]
internal static class RestoreMerchantInventoryPatch
{
    static bool Prefix(Player player, ref MerchantInventory __result)
    {
        if (!RaceRuntime.Live || !MerchantRecovery.TryRestore(player, out var inventory)) return true;
        __result = inventory; return false;
    }
}
