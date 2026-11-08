# Armageddon (ID 14): how it works in the game

English | [Русский](ARMAGEDDON.ru.md)

Probe `coop_probe` 0.6.0–0.6.1, battles on 23.10.2026 (host, level-5 Armageddon via a cheat).

- Armageddon has no projectiles: `Bullet_Create` isn't called on the explosion, so magic forwarding doesn't see it.
- `C_Battle_Manager.ArmageddonAttack()` is called **once, when the spell is taken** (it starts the cycle), not on every explosion.
- The countdown to the explosion is the static `C_Battle_Manager.Apply_ArmageddonDelayNum` (`Single`); it decreases by ~1 per second
  (level 1: 103 → explosion in ~105 s; after the explosion it restarts from the cooldown value, e.g. 68.6).
- During the explosion `C_Battle_Manager.IsArmageddonContinue = True` (~5 s); the mod detects the start by it.
- Manager coroutines: `Play_Armageddon`, `Play_ArmageddonAttack`, `Play_ArmageddonEffect`, `Play_ArmageddonLand`.

Order of the manager's objects during the explosion (seconds from the start):

| t, s | object | |
|---|---|---|
| 0.00 | `Obj_ArmageddonGround_Front` ("Img_ArmageddonGround_Front", full-screen UI) | on |
| 0.00 | `Obj_ArmageddonGround_Back` ("Sprite_ArmageddonGround_Back", on the hero) | on |
| 1.86 | `Obj_Armageddon` ("Sprite_Armageddon", sprite "Armageddon1") | on |
| 2.09 | `Obj_Armageddon` | off |
| 2.45 | `Obj_ArmageddonLand` ("ArmageddonLandA"), `Obj_ArmageddonGround_Destroy` | on |
| 2.45 | `Obj_ArmageddonGround_Back` | off |
| 2.69 | `Obj_ArmageddonGround_Destroy` | off |
| 3.14 | `Obj_ArmageddonGround_Front` | off |
| 4.78 | `Obj_ArmageddonLand` | off |
| ~5.0 | `IsArmageddonContinue` → False | |

Co-op 1.4.7: the receiver turned objects on by the table; the explosion wasn't visible (frames and transparency are set
by coroutines), and the receiver's enemies vanished (probably `Obj_ArmageddonGround_Destroy` or its collider).
Co-op 1.4.8: the receiver runs `StartCoroutine("Play_ArmageddonEffect")` and `("Play_ArmageddonLand")`;
both sides log the explosion's frame-by-frame breakdown (on, renderer, α, frame, scale) and the enemy count over 0–6 s.

Tested on phones (1.4.8): the replay at the receiver matches the original frame by frame (flash `Armageddon1…8` ~1.6 s,
then Land/Destroy) and **deals damage**: enemies 45 → 10 in 3 s (like the player's own explosion, 49 → 9). Damage is kept
full (the «Урон магии друга» setting doesn't apply to Armageddon), a decision from 23.10.2026; a balance tweak may come later.
