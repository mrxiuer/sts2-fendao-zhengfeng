# Slay the Spire 2: Separate Paths

A five-act multiplayer race with independent paths, battles, and relic sacrifices. Share the map, advance at your own pace, and be the first to defeat the Heart of the Spire at the end of Act 5.

**0.5.4 local preview; not yet published to Workshop or GitHub.** Adds native cooperative mode and fixes Slime layout drift, Extra Timeline autoplay, and delayed multiplayer reward delivery. The [latest published package](https://github.com/mrxiuer/sts2-fendao-zhengfeng/releases/tag/v0.5.3-preview) is still 0.5.3 and does not contain these changes.

## Requirements and installation

1. In Steam → Properties → Game Versions & Betas, select **public-beta**. The default public branch has incompatible APIs; matching your friends' game version alone is insufficient. The verified API baseline is **v0.111.0 public-beta**. See [API comparison](docs/API-COMPATIBILITY.md).
2. All players must use the same game version and mod version, and install and enable **BaseLib 3.4.7 or later** (tested with 3.4.7). Game files and BaseLib are not bundled.
3. Subscribe to [Separate Paths on Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3825045315), currently visible to the publisher's Steam friends. Remove any manual `mods/FendaoZhengfeng` installation first to avoid duplicate mod IDs.
4. For a manual installation, extract the release's `FendaoZhengfeng` folder into the game's `mods` directory. It contains `FendaoZhengfeng.dll` and `FendaoZhengfeng.json`. Source-code archives are not installation packages.
5. In Mod Configuration → Separate Paths, the host enables the mode and selects allowed effects. Start a normal multiplayer lobby. All participants must enable the mod.

Quit the game before updating. Everyone should update together.

## Language

Text follows the game's language: Chinese locales use Chinese, and English or other locales use English. Effects, configuration, reward choices, status tooltips, recovery, and debug tools are translated. Reopen configuration and choice screens after changing language; existing temporary dialogs may retain their original text. Players may use different interface languages. Effect IDs, save rules, and the network protocol do not depend on translation.

## Cooperative mode

The host enables Synchronous Mode in Mod Configuration. The next run uses native shared map votes, cooperative combats, multiplayer monster scaling, and teammate inspection. Elite and Boss relics can be kept or sacrificed for Boons and self-applied Double-edged Contracts. Boss bonus rewards select a Boon directly. Curses, Adversity Profit, Retaliation, and Shared Misery are excluded. No starting Lizard Tail is granted; normal acquisition and Tail Refill still work.

All players need the same build and a new run. Independent racing retains its original opposing Curse rules.

## Race rules

- Players choose adjacent map nodes independently and fight their own combats with solo enemy HP. Entering the same node does not merge battles.
- Sacrifice the current Elite or Boss relic reward to choose 1 of 3 mixed effects: a Curse for opponents, a Double-edged Contract, or a Boon for yourself. Existing relics and other rewards remain.
- There are 65 configurable effects. Curses stack, apply from the target's next combat, and last until cleansed. Rest Sites cleanse Normal-tier effects; the Merchant does not cleanse mode effects. Boss-tier effects cannot be cleansed at Rest Sites or the Merchant.
- Each player starts with an additional native Lizard Tail. Other native revival sources still work normally.
- Acts 4 and 5 each contain two consecutive Bosses. Only the last Boss of Act 5 ends the race. Act 4/5 monsters gain 20%/40% HP, damage, and Block, added to R Key reset scaling.
- The act's first Boss finisher chooses a Boon or Curse Shield; others choose a cleanse or Curse Shield. These rewards are separate from relic sacrifice.

## Exiting a run

This preview does not offer race continuation or mid-run reconnect. Boons persist across acts except effects with explicit combat limits, charges, or removal conditions. Rules are `1.0.0-design.16`, protocol `wire:6`. See the [verification record](docs/VERIFICATION.md) for the actual Windows native and two-process test coverage.
