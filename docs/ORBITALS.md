# Shield, Satellites, Spirits: spells without projectiles

English | [Русский](ORBITALS.ru.md)

Goal: show them on the friend's ally. Forwarding projectiles (`Bullet_Create`) doesn't cover them: they are objects that hover or circle around the hero.
Data: hero probe (`coop_probe` 0.4.0–0.5.0, 2026-10-22), a hero with all 21 spells leveled up (granted by a cheat).

## How it works on the hero (`C_Battle_AllyUnit`)

**Satellites** (spell 4)
- Container `Obj_UnitSatelliteMid` ("Unit_SatelliteMid", 14 children `Center_SatelliteNN` → `Unit_SatelliteNN`); the second ring is `Obj_UnitSatelliteMidBack` (`…b`).
- Arrays: `Array_Obj_UnitSatellitesA/B` (GameObject[14]), `Array_Transform_ObjSatellitesA/B`, `Array_Transform_Center_UnitSatellitesA/B`, flags `Array_IsSatelliteA_P/B_P` (bool[14]).
- `UnitApply_SatelliteNum` of them are on (8 at level 8): the first N flags are `True`, the objects are active and stand on a circle of **radius 2.25** around the hero at equal spacing.
- Rotation is the `Play_SatelliteMid_Rotation` coroutine (variants `_AntiGravity01/02`, `_Com02_A/B`, `_NuclearFusion`, `RotationBack` exist); speed `UnitApply_SatelliteSpeed` (0.03). The layout is done by `ApplySatelliteGroup` from `StartInformation_B`.
- Damage is `UnitApply_SatelliteDamage`, size is `UnitApply_SatelliteSize`.

**Shield** (spell 6)
- Object `Obj_Shield` ("1100", 1 child) on the hero; flag `IsShield_P`; coroutines `Play_Shield`, `Play_Shield_Com01`.
- When on: the object is active, the sprite `SpriteRenderer_Shield` = "Shield2_03"; charges `UnitApply_ShieldMaxNum` (2), `ShieldDelay` is the recharge, `ShieldFirstON`.
- Without the spell the shield sprite is empty (the coroutine sets it).

**Spirits** (spell 3)
- Container `Obj_UnitSpiritMid` (15 children), `List_ObjSpirit` / `List_Transform_ObjSpirit` / `List_SpriteRenderer_Spirit` (index 0 is `null`, spirits 1..14), flags `List_IsSpirit_P`.
- `UnitApply_SpiritNum` (7) are on: flags 1..7 are `True`. The "UnitSpirit1MotionA" sprite is set on the inactive ones too.
- They fire the `BulletID_Spirit` (19) projectile from their objects (`Unit_Spirit01`…), forwarded since Co-op 1.3.2.

**Doppelgangers** (Doppelganger 367, Avatar)
- `Obj_UnitDoppelganger01..09` ("Sprite_UnitDoppelgangerNN", children of the hero object), `Transform_ObjUnitDoppelgangerNN`,
  `SpriteRenderer_DoppelgangerNN`, flag `IsDoppelganger_P`. The 9th is taken by the friend's ally (`modkit_companions`).
- When on, a doppelganger is active, stands 0.5–1.5 units from the hero, its sprite is the hero's frames
  (`AUnit1Motion1…`), white with alpha 0.3–0.4. It shoots by itself (`Play_Doppelganger_Attack`) through `Bullet_Create`,
  so the bullets are forwarded like any other.
- After 1.9.0 co-op sends a `dp` array in `orb`: triples (x, y from the hero, alpha) of the active doppelgangers, except
  the one taken by an ally. The receiver puts copies of its own `Obj_UnitDoppelganger01` at the ally and takes the frame
  from the ally itself 10 times a second.

## Satellite traits (2026-11-10)

Measured with `coop_lab` `Orbs` on a phone, level 5:

| Variant | Ring A | Ring B | Sprite |
|---|---|---|---|
| Satellite, Anti-Gravity (389) | 5, radius 2.25, size 0.92 | — | `UnitSpirit4Motion…` |
| Will o’ the Wisp (390) and its combos | 5, radius 1.6, size 0.62 | 5, radius 2.7, size 0.62 | `UnitSpirit6Motion…` |
| Nuclear Fusion (391) and its combos | 1, radius 2.76, size 0.61 | up to 5, radius 2.7 | `UnitSpirit7Motion…` (a big fireball) |
| Soul Blade (62) spirits | | | `UnitSpirit8Motion…` |

The frames are flipped by the satellite's own script `C_Battle_AllyUnit_Satellite` (`OnEnable` → `Play_Satellite_Motion`).
On a copy it runs by itself and uses the receiver's plain look, so before the fix a friend saw small white satellites at
radius 2.25 instead of fireballs. Now `orb` carries the radius and size of each ring (`ra`, `rb`, `ka`, `kb`), the
script's coroutines on a copy are stopped, and Satellite and Spirit copies get the sprite sent by the friend (`isa`,
`isb`, `isp`).

## Pitfalls
- You can't read `Renderer.enabled`: `ICall UnityEngine.Renderer::get_enabled was not resolved` (in this Unity build).
- `UnityEngine.Object.Instantiate(obj)` works but returns a `UnityEngine.Object` wrapper, which lacks `GameObject` methods (`SetActive` → `MissingMethodException`). You need a `GameObject` wrapper by address: `new GameObject(ptr)` (`Activator.CreateInstance(typeof(GameObject), ptr)`).

## Plan for showing them on the ally
1. The friend sends: number of satellites (A and B), ring rotation angle, number of spirits, whether the shield is on.
2. The receiver makes copies of their own template objects (`Instantiate`) around the ally: satellites on a 2.25 circle at the same angle, spirits nearby, the shield on the ally.
3. Copy experiment (`coop_probe` 0.5.1): copies are created and activated, the game doesn't move them. Confirmed.
4. Done in Co-op 1.4.0 (`mod/coop/Orbitals.cs`): the `orb` field in the state (`sa`, `sb`, `sp`, `sh`, `aa`, `ab`), 4 times per second; the angle is extrapolated from the estimated rotation speed. Since the version after 1.7.8 `orb` goes on a change of the look (counts, pictures, shield, zone flashing, magic circle) and at least 10 times a second, not in every state; no orbits — `"orb": null` once a second; without the field the receiver keeps the previous values.

## Findings from the 1.4.5 logs (2026-10-23)
- The Shield sprite is drawn by `SpriteRenderer_Shield`, and it is **not** inside `Obj_Shield` ("1100" has no renderer: `GetComponentInChildren` is empty on both the hero and the copy). Shield on means `IsShield_P` plus a non-empty sprite (`Shield2_01…04` change with charges). Since 1.4.6 `SpriteRenderer_Shield.gameObject` is copied, with offset and scale taken from the friend.
- Satellite and Spirit sprites are animation frames (`UnitSpirit4MotionA/B/C` for a Satellite, `UnitSpirit1MotionA…D` for a Spirit) and change constantly; the copy's own sprite is left alone.
- `Obj_ElectricZone` flashes (isn't active all the time) when `IsElectricZone_P = True`: "present" comes from the flag, "visible" from `activeSelf` at each send.
