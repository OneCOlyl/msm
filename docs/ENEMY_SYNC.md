# Enemy sync: research

English | [Русский](ENEMY_SYNC.ru.md)

Goal: shared enemies for all players ("host's world"). The host plays as usual; the others get copies of the host's
enemies. This document collects what the `mod/coop_probe` probe found out on the device. The probe was removed after the research, its code is in git history.

## Probe 1 (coop_probe 0.1.0, 2026-10-22, two phones)

**How enemies appear**
- Every enemy is created by calling `C_Battle_EnemyUnit.Start_Unit(int PUnitID, float PUnit_SpecialVarA, float PUnit_SpecialVarB)`; normal enemies have `varA = varB = 0`.
- The `C_Battle_Manager.Create_UnitEnemy` coroutine creates batches of `EnemyCreateNum` (2 at the start) about every 0.76 s, on a ring 7–12 units around the hero.
- Each enemy is a new object named by a counter ("10000", "10001"…); object addresses don't repeat. The manager's fields have no enemy pool; the template is probably `AObj_UnitEnemy` (GameObject).
- Types over 110 s: 1, 6, 11, 13 (the type depends on the wave).

**Spawning**
- `Create_UnitEnemy_Stop()` stops the coroutine, but the game restarts spawning itself at every wave step (~every 30 s: `Create_UnitEnemy` + `Create_UnitEnemySpecial`). This method doesn't work for suppressing spawns at the guest.
- A second `StartCoroutine("Create_UnitEnemy")` starts a **second** coroutine (spawning doubles), so don't do that.

**Movement**
- `C_Battle_EnemyUnit` has no `Update`: behavior is coroutines (`Unit_Move`, `Unit_MoveRush`, `Unit_MoveAction`, `Unit_MoveDistance`, `Unit_MoveStop`, `Unit_Rotation`, attacks).
- An enemy is a physics body (`Rigidbody_ObjUnit`, `BoxCollider_ObjUnit`), so neighbors push it around.
- Freeze (`Status_Freeze`) and stun (`Status_Stun`) don't hold an enemy in place: in 4 s it moves ~2 units (~3.5 without the status).
- Speed fields: `UnitApply_MoveSpeed` (0.064 for type 1), `BUnitApply_MoveSpeed`, flags `IsPlayMove`, `IsPlayMoveRush`, `IsFreeze`, `IsStun`, `Unit_IsLive`.

**Other**
- Candidate methods for removal without reward: `Unit_Destroy_Simple`, `Unit_Disappear_Peace`.
- `Create_UnitEnemyBoss(int PUnitEnemyID, int PUnitEnemyNum)` is a regular method (not a coroutine): a candidate for "create an enemy on command".
- Co-op mod 1.2.0: per-frame movement works (55–59 fps); every ~12 s the frame hook takes 10–19 ms, probably garbage collection from per-frame allocations (fixed in 1.2.1).

## Probe 2 (coop_probe 0.2.0): what it checks

| Experiment | Question |
|---|---|
| A1 | Does `Create_UnitEnemyBoss(1, 1)` create a normal enemy synchronously, and where; side effects (a boss wave?) |
| A2 | Does copying `AObj_UnitEnemy` + `GetComponent<C_Battle_EnemyUnit>` + `Start_Unit` work, is it a full enemy |
| B | Does an enemy stay in place if its coroutines are stopped and its body made kinematic, and can it be driven every frame |
| C | What it costs to reposition all enemies in a frame |
| D | Does `Unit_Destroy_Simple` remove new enemies without counting kills |
| E | Does zeroing `EnemyCreateNum`/`_Min`/`_Max` suppress spawning |

## Probe 2: results (2026-10-22, two runs, consistent)

| Experiment | Result |
|---|---|
| A1 | ✅ `Create_UnitEnemyBoss(1, 1)` synchronously creates a normal type-1 enemy (HP 20, size 1.25); the `Start_Unit` hook catches the object during the call; it stands at the hero's position. Side effect: `ApplyBossNum` 0 → 1 (static), restore it after the call. |
| A2 | ❌ `AObj_UnitEnemy` isn't a template but a live enemy ("10045"); the copy has no `C_Battle_EnemyUnit` component. Approach dropped. |
| B | ✅ `StopAllCoroutines()`: the enemy stays in place (0.00 over 3 s; jostling ≤0.15 without kinematics). Driven every frame with "stop + kinematic": deviation 0.000. `isKinematic` alone: the AI walks the enemy away. Puppets can be killed (Unit_IsLive → false). |
| C | ✅ Repositioning all enemies: 41–55 of them take 0.31–0.39 ms with cached Transform references (2.6–4.0 ms with object lookup), so ~1.5 ms for 200 enemies. |
| D | ✅ `Unit_Destroy_Simple()` on the frame after `Start_Unit`: 62–64 of 62–64 removed, kill/money counters unchanged. |
| E | ✅ `EnemyCreateNum = EnemyCreateNum_Min = EnemyCreateNum_Max = 0` → 0 new enemies in 10 s (originally 2 / 0 / 300). |

## Probe 3 (coop_probe 0.3.0, 2026-10-22): confirmation

Same results a third time: an enemy created on command (placed 3.5 units from the hero, coroutines stopped) was killed by
the hero's magic within 2 s; driven puppets: deviation 0.000; with coroutines stopped: ≤0.10; kinematics only: the AI walks
them 3–7 units away; repositioning 53 enemies: 0.44 ms; suppression 62/62; zeroed spawning: 0 new. `ApplyBossNum` is
restored after the call with no consequences.

Along the way (Co-op 1.3.0 in the same battle): repeating the friend's magic works (215 casts, no crashes); spell 6 doesn't
create `Bullet_Create` (objects circling the hero), so forwarding projectiles doesn't cover such spells. Searching for the
nearest enemy by brute force on every projectile made the frame hook take 8–15 ms; in 1.3.1 enemy positions are cached
4 times per second, and replays are rationed (4 casts and 3 ms per frame).

## "Host's world" design

1. Host: hook `After Start_Unit` → network event "enemy N: type, varA, varB, x, y"; every 1/15 s, positions and HP of live enemies (in a batch).
2. Guest: own spawning suppressed (E or D); on the event, create an enemy (A1/A2), stop its coroutines, make the body kinematic (B); every frame, move it smoothly along the host's positions (like allies).
3. Guest's damage to a copy → event to the host → `ICombat.Damage` on the real enemy; death at the host → `Unit_Destroy_Simple` on the guest's copy.

## Stage 1: Co-op 1.5.0 (the «Общие враги» setting, off by default)

Decisions (23.10.2026): the "host's world" model; the guest's damage to the host's enemies goes through the already
working replay of the guest's magic at the host (copies at the guest don't send hits); the first step is the mod itself.

- Host: in the state, up to 10 times per second, `en: [[id, UnitID, x×100, y×100], …]` relative to the host's start
  point (up to 250 enemies); the id is the host's own counter keyed by the enemy object's pointer.
- Guest (`WorldSync.cs`): spawning suppressed with `EnemyCreateNum/_Min/_Max = 0` every 0.5 s (original values restored
  on shutdown) + `Unit_Destroy_Simple` for any own enemy (hook `After Start_Unit` and a sweep every 0.5 s).
  A copy is `Create_UnitEnemyBoss(type, 1)` (up to 4 per frame, `ApplyBossNum` restored), `StopAllCoroutines`,
  `isKinematic`; every frame, the position with delay and interpolation as for an ally. Gone at the host →
  `ICombat.Damage(1e9)` (kill and drops at the guest). Killed at the guest by the guest's magic → not recreated.
- In shared enemies mode an ally stands where the friend is (without the «Дальность призрака» ghost range limit).

Known limitations of stage 1: enemies walk to the host (the host's AI), copies at the guest don't attack from range
(coroutines are stopped), special enemies (`varA/varB` in `Start_Unit`) are created as normal ones; bosses and enemy
projectiles aren't synced separately.

## Special mobs: 1.6.65–1.6.77 (probe 27.10.2026, coop_debug on both phones)

The game creates special mobs with three `C_Battle_Manager` coroutines, all through the same `C_Battle_EnemyUnit.Start_Unit`:

| Coroutine | What it is | Pool |
|---|---|---|
| `Create_UnitEnemySpecial` | 5 s step, the "Super" list (`_List_ApplyUnitEnemyDistribution_Super`), `SpecialVarB`; the list is empty on the first waves | `List_BattleUnitEnemySpecial_Empty` (80) |
| `Create_UnitEnemyEpic` | a group on a circle of 10 points (`_SpawnPositions`), `SpecialVarA/B`; manager fields `CreateEpicEnemyID/Num/Delay` | `List_BattleUnitEnemyEpic_Empty` (15) |
| `Play_Create_UnitEnemy_Transition` | the picked-up mob power-up (15 mobs, varA 1 or UnitCreateID 3, red outline) | normal |

Before 1.6.65 the mod only recognized "transition"; the rest went to the guest as normal mobs of the same type; a guest far
from the host kept their own special and transition mobs, so each player had their own.

1.6.65: a step of these coroutines (hook `MoveNext` before/after) tags where an enemy came from. The host sends the guest
the tag and size (elements 12–13 of the enemy record); the guest always removes their own special and epic mobs (except
during their own boss wave), and forwards transition mobs from their own pickup to the host, even when far from it (in the
`dmg` event: the `so` array with a tag for each enemy in `s`). The log gets one field snapshot per kind at the host
(«особый моб хоста: …», host's special mob) and at the guest's copy («особая копия у гостя (особый): …», special copy at
the guest), to compare the look.

1.6.66: the game creates a transition mob with `Start_Unit(type, 0, 0)` and `UnitCreateID 1`, and sets
`Unit_SpecialVarA = 1` and `UnitCreateID = 3` later (in the hook it's "created 1", a second later 3), so the host re-reads
these fields in the first three sends of an enemy, and the guest, if an enemy record became special later, repeats
`Start_Unit` on the copy with the special kind. Previously most transition mobs were lost this way (log 1.6.17: of the
host's 128 special mobs with varA 1, 15 were sent).
Since the version after 1.7.8 a special enemy's look (varA, varB and 12 look fields) is not in every send: in the first
three sends after it appears or changes, then once every 2 s, a few enemies per send. The guest waits up to 1 s for the look of a new copy,
then creates it with the default look and updates it when the look arrives. After a new player joins or anyone
reconnects (the guest sends the `full` event) the host sends the look of all enemies again. The name, max HP, kills and
the lobby fields go the same way: three sends after a change, otherwise once every 2 s.
Enemy send format (about 7 times a second): `en` is a flat array `[id, x, y, …]` (x, y in tenths of a unit from the host's start); `et` is `[id, type, …]` in an enemy's
first three sends and in a full snapshot; `el` is `[[id, varA, varB, 12 look fields], …]`; `es` lists special enemies sent
without the look; `eg` lists dead enemies, each repeated in three sends; `ef: 1` marks a full snapshot. Enemies within
15 units of a guest go in every send, the farther ones every other send, all of them in the full snapshot once a second.
Only a full snapshot means "missing — dead". The clock and the chest list go on change and at least twice a second.
An epic mob (type 6 in the test) is field-wise a normal mob of its type (`UnitCreateID 1`, no outline, varA/varB 0): it is
special because it spawns in a batch on a circle; the copy at the guest is a normal copy of that type.
1.6.67: the game also sets a transition mob's strength after `Start_Unit` (type 16: `UnitApply_HpMax_Bonus` 10000,
`UnitBase_HpMax` 2525 vs 500, resistances 0.5, knockback 0.85, `ApplyMoveSpeedRatioUVar` 0.65, size 1 vs 0.8);
`Start_Unit(16, 1, 0)` with `UnitCreateID 3` gives a mob of normal strength. The guest holds their transition mob for 0.3 s,
reads these fields (`TransitionStats`) and sends them to the host with the mob (`sf` in the `dmg` event); the host applies
them to its mob.
1.6.68–1.6.72: on pause (own or the host's skill choice) the guest's world isn't turned off; the guest's own transition mob
that appeared while the world was off is sent to the host by the sweep. `Unit_Destroy_Simple` doesn't remove a transition
mob at the guest (while it is appearing); a guest's own enemy that outlived it is removed with `Unit_Disappear_Peace`.
1.6.75: the special mob marker (`Obj_MonsterIcon`, `Sprite_MonsterIcon01/02`) and outline exist only on preforms of the
special pool (`List_BattleUnitEnemySpecial_Empty`); preforms of the boss pool and the normal pool don't have them. Special
copies at the guest and the guest's transition mobs at the host are created from a special-pool preform (epic ones from the
epic pool) placed at the head of the boss pool before `Create_UnitEnemyBoss`.
1.6.77: the marker size (`MonsterIcon_Size`) and the outline (`Obj_UnitSprite_Back` with `Sprite_UnitEnemySpecial_Back`)
are set by the transition coroutine, not by `Start_Unit`: the host sends them (elements 14–15 of the enemy record), and the
guest turns them on for the copy.


## Boss preforms: 1.6.115 (phones, 01.11.2026)

The guest creates a copy of any enemy with `Create_UnitEnemyBoss`, which takes a preform from
`List_BattleUnitEnemyBoss_Empty[0]` (and only when the pool has two or more). Real boss preforms have 3 child objects;
normal mob preforms have no children. Before 1.6.115 normal copies used up all 14 boss preforms in the first minutes, the
mod topped up the pool with normal mobs, and at 5:00 `Start_Unit(41)` on such a preform failed inside the game with a
`NullReferenceException` (the interceptor swallows it; the mod only saw «тип 41 не создался», type 41 not created). Now for
a normal copy the mod puts a preform from the normal pool at the head of the boss pool; it learns the boss types from the
guest's own boss wave and from failures (then the broken preform is turned off and returned to the normal pool, retry in
2 s).

How to summon special mobs at the host via `coop_debug` is in docs/device-tests/1.6.116.md.
