# Replaying a friend's magic: what Bullet_Create takes from the hero

English | [Русский](MAGIC_REPLAY.ru.md)

Co-op replays every projectile of the friend's hero: it catches `C_Battle_AllyUnit.Bullet_Create` (13 arguments) at the
caster and calls the same method at the receiver on the receiver's hero, with the friend's ally as the source
(`mod/coop/Magic.cs`). The game takes the projectile's look from the hero the method is called on, so for the duration of
the call the mod puts the friend's values there. Based on `libil2cpp.so` (Ghidra and an instruction scan) and
measurements on two phones, 2026-11-07.

## Instance fields

Size, area, speed, duration and count (`UnitApply_<Spell>Size/Area/MSpeed/Time/Num…`, 34 fields) are sent in the `fx`
state field and written straight into the hero's memory for the call (since 1.7.4).

A combo changes these fields at once. Telekinetic Sword sets `UnitApply_SpiritBulletMSpeed` to 0.035 instead of 18, and
projectile 56 (`SpiritBulletD_Animation`) waits that many seconds on each of its 8 frames. While `fx` went once a second,
the first casts after picking the combo were replayed with 18 and hung at the friend in the map centre for about two
minutes. After 1.8.0 the mod reads the fields 4 times a second and sends `fx` on change (otherwise once a second).

## Static fields of C_Battle_AllyUnit

The hero has statics that pick an upgrade variant. `Bullet_Create` and the `C_Battle_AllyBullet.BulletAttack` /
`C_Battle_AllyArcaneRay.BulletAttack` it calls read:

| Field | What it changes |
|---|---|
| `ApplyID_Incinerate` | Flame Storm (projectile 65): 0 is two jets, 1 is four |
| `UnitApply_EnergyBulletExplosionArea`, `UnitApply_EnergyBulletTime` | Energy Bullet explosion and duration |
| `ConstellationTime` | Fireball constellation duration |
| `UnitApply_ArcaneRayNumPlus` | Arcane Ray rays |
| `UnitApply_LavaFieldSizeExpandRatioNum` | Lava Field growth (`Start_MeteorLand`) |
| `UnitApply_DetonateDeadSizeRatio` | explosion size (`ExplosionAttack`) |

After 1.8.0 they go at the end of `fx` (41 numbers instead of 34) and are substituted the same way, through the static
properties. The other statics read there are crit (`UnitApply_*_CriRan`), damage, and enemy freeze and stun; replay
damage is computed by the receiver, so the mod leaves them alone.

Later, in projectile coroutines, the game reads `UnitApply_TsunamiFlowSpeed` (`Tsunami_Moving*`), `VolcanoTime`
(`MeteorB_Animation`), `UnitApply_MeteorNum` (`MeteorC_AnimationB`), `UnitApply_TornadoADelay` (`Tornado_Attack`) and
`ApplyID_ElectricZone`. These can't be substituted for the call: at the receiver those parts use the receiver's values.

Static offsets were computed from field order and sizes in the Cpp2IL assembly (`Assembly-CSharp.dll` from the phone) and
checked against known places: `0x1f8…0x238` are `UnitApply_*_CriRan`, `0x26c` is `ApplyID_Incinerate`.

## Projectiles on hero objects

Some `Bullet_Create` branches make the projectile a child of a hero object (`SetParent`, then `localPosition = 0`) or
take the hero's position. At the receiver this was the receiver's own hero: the friend's rays came out of the receiver,
the friend's constellation circled the receiver (measured: projectiles 75–77 at 0.1–0.2 from the own hero and 15 from
the ally).

| Hero field | Projectiles (BulletID) |
|---|---|
| `Obj_UnitArcaneRayMid` | 4, 23, 40, 51, 67, 90, 94, 111, 127 (Arcane Ray and its combos) |
| `Obj_UnitConstellationMid` | 27, 75 (constellation) |
| `Obj_UnitDestroyFieldMid` | 53 |
| `Obj_UnitGravityOrbMid` | 68 |
| `Transform_ObjUnit` (hero position) | 68, 91 |

After 1.8.0 the mod keeps empty anchor objects for each friend's ally (one per field, moved to the ally every frame) and
puts them into the hero's fields while these projectiles are replayed; `Transform_ObjUnit` gets the ally's transform.
Anchors are not destroyed during the battle: projectiles from the game's pools may hang on them.

The game can rotate `Transform_ObjArcaneRayMid` with the `Play_ArcaneRayMid_Rotation(_Com01)` coroutines: +2° every
0.03 s. Plain Arcane Ray doesn't rotate it. The caster's anchor angle goes in the cast as the 14th number, and the
receiver sets it on its anchor before the call; rotation between casts isn't replayed.

## Cast point

The replay pulls the friend's target point to the nearest enemy within 2.5 units, since enemies don't stand at exactly
the same spot for every player. After 1.8.0 a cast at the caster's own position (closer than 1 unit: the
Snowflake Blossom burst around the hero, the Telekinetic Sword sword 0.5 from it) isn't pulled and stays on the
ally.

## Not covered by the replay

- Electric Zone combo zones: `C_Battle_AllyElectricZone.Start_FireRing`, `Start_FrozenHeart`, `Start_ElectricZone_Com01`
  (on `Obj_ElectricZone_Add01..03`), `Start_Destroyer`, `Start_Ultimate37`. `StartInformation_B` (the hero recalc) turns
  them on, not `Bullet_Create`. Co-op carries only the plain `Obj_ElectricZone` (docs/ORBITALS.md).
- Permanent rays `List_ObjArcaneRay_Plasma`: `Start_ArcaneRay_Com01` creates them from `StartInformation_B`, their count
  is `UnitApply_ArcaneRayNumPlus`.

## Checking

- `coop_lab` arena: `Shots` lists live projectiles by kind and number (`b65:4,x32:3`: b is `C_Battle_AllyBullet`,
  r a ray, x an explosion, t a tornado, m `C_Battle_AllyMeteorLand`, z a zone, h `HydraZone`, s `BulletSub`); `ShotsDist`
  adds the average distance to the own hero and to the nearest friend's ally.
- `python tools/lab.py place` runs spells and combos one at a time at the caster with the friend 7 units away: whatever
  the caster has must be at the friend too, at the same distance from the ally.
