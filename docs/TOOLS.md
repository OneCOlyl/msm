# Developer tools

Three ModKit mods for testing Co-op on phones. Players don't need them: the cheat and the test arena change how a
battle goes. They ship together in a release tagged `tools-<number>` (workflow `tools-release`), separate from Co-op
releases.

| Mod | What it does | MS_KIT libraries needed |
|---|---|---|
| `coop_debug` (Co-op: debug) | Console on port 8770: read and change game fields during a battle, call methods, set hooks. | — |
| `coop_cheat` (Cheat: spells) | «ЧИТ» button in battle: levels of spells, traits, passives, artifacts, combinations and synergies, hero level. | `modkit_spells`, `modkit_spells_data`, `modkit_effects`, `modkit_data` |
| `coop_lab` (Co-op: test arena) | «ПОЛИГОН» button in battle: calm arena, any mobs, battle mechanics, Co-op checks from a PC. | `modkit_combat`, `modkit_hero` |

## Installation

1. Download the archives you need from the `tools-<number>` release: `coop_debug.zip`, `coop_cheat.zip`, `coop_lab.zip`.
2. Put them on the phone in `/sdcard/Download/` and install them in Zombix: «Моды» (Mods) → «+».
3. A new ModKit mod starts disabled: enable it in the Mods window and restart the game.

Over USB: `adb push coop_lab.zip /sdcard/Download/`.

## coop_debug: debug console

The console listens on port 8770 on the phone only; a PC reaches it over USB:

```bash
adb forward tcp:8771 tcp:8770
python tools/coop_dbg.py 8771 "get mgr.Time_Sec" "ls hero Circle"
```

Commands: `get`, `ls`, `lsall`, `methods`, `nested`, `set`, `call`, `hook`, `skip`, `iters`, `find`. Path roots: `hero`
(the hero), `mgr` (battle manager), `abil` (ability manager), `db` (`Z_Database`), `T:<assembly>:<type>` and
`U:<Unity type>` (static members), `found` (result of `find`). The full list is in the `help` command.

## coop_cheat: cheat

In battle, the «ЧИТ» button on the right opens a panel:

- tabs: spells, traits, special passives, passives, artifacts, other, research, enchantments, combinations, synergies;
- tapping an entry gives +1 level; in «−1 уровень» mode it removes a level;
- «Уровень героя +1» (hero level +1) opens the magic selection window at once: the cheat gives exactly the mana missing
  for the next level;
- «Все магии — максимум» (all spells to max) raises all 21 spells to their limit with one tap.

A level is given the same way as a choice in the game: the ID goes into the learned list and the hero is recalculated.
If the level did not grow, the cheat writes it directly. The log (`[coop_cheat]`) shows which way worked.

## coop_lab: test arena

In battle, the «ПОЛИГОН» button opens three tabs:

- «Арена» (arena): game spawn, immortality, experience, stopping and shifting the clock, removing or killing enemies;
- «Мобы» (mobs): any enemy type (0–50), epic, special, transition;
- «Механики» (mechanics): clock to the boss, boss wave, obelisk, area change, pulling mana, item by number.

Notes:

- with experience off, magic selection windows don't open, but the cheat's hero level button still works;
- «Сдаться» (give up) and «Выход» (exit) turn immortality off by themselves, otherwise the battle would never end;
- the arena gives spells through `coop_cheat` (`Spell <id> <level>`); without the cheat this command does not work.

The same actions from a PC through the `coop_debug` console: `call T:coop_lab:MagicSurvivalLab.Lab/Current.Spawn 6 10`,
`.Status`, `.CoopStatus`. Scenarios on two phones are in `tools/lab.py` (see [DEVELOPMENT.md](DEVELOPMENT.md#test-arena)).

## Publishing

1. Raise the version of the changed mod in three places: `version` in `mod/<mod>/mod.json`, `<Version>` in
   `mod/<mod>/<mod>.csproj` and `Version = "…"` in the mod's code (`build.sh` checks that they match).
2. Create the tag with the next number and push it:

   ```bash
   git tag tools-2 && git push origin tools-2
   ```

GitHub builds the mods, runs the tests and publishes a release with the three archives. The mod versions go into the
release description.
