# Армагеддон (ID 14): как устроен в игре

Разведка `coop_probe` 0.6.0–0.6.1, бои 23.10.2026 (хост, Армагеддон 5-го уровня через чит).

- Снарядов у Армагеддона нет: `Bullet_Create` при взрыве не вызывается, поэтому пересылка магии его не видит.
- `C_Battle_Manager.ArmageddonAttack()` вызывается **один раз — когда заклинание взято** (запускает цикл), а не на каждом взрыве.
- Отсчёт до взрыва — статическое `C_Battle_Manager.Apply_ArmageddonDelayNum` (`Single`), убывает ~1 в секунду
  (1-й уровень: 103 → взрыв через ~105 с; после взрыва — снова со значения перезарядки, например 68,6).
- Во время взрыва `C_Battle_Manager.IsArmageddonContinue = True` (~5 с) — по нему мод ловит начало.
- Корутины менеджера: `Play_Armageddon`, `Play_ArmageddonAttack`, `Play_ArmageddonEffect`, `Play_ArmageddonLand`.

Порядок объектов менеджера при взрыве (секунды от начала):

| t, с | объект | |
|---|---|---|
| 0,00 | `Obj_ArmageddonGround_Front` («Img_ArmageddonGround_Front», UI на весь экран) | вкл |
| 0,00 | `Obj_ArmageddonGround_Back` («Sprite_ArmageddonGround_Back», на герое) | вкл |
| 1,86 | `Obj_Armageddon` («Sprite_Armageddon», картинка «Armageddon1») | вкл |
| 2,09 | `Obj_Armageddon` | выкл |
| 2,45 | `Obj_ArmageddonLand` («ArmageddonLandA»), `Obj_ArmageddonGround_Destroy` | вкл |
| 2,45 | `Obj_ArmageddonGround_Back` | выкл |
| 2,69 | `Obj_ArmageddonGround_Destroy` | выкл |
| 3,14 | `Obj_ArmageddonGround_Front` | выкл |
| 4,78 | `Obj_ArmageddonLand` | выкл |
| ~5,0 | `IsArmageddonContinue` → False | |

Кооператив 1.4.7: получатель включал объекты по таблице — взрыв не виден (кадры/прозрачность задают корутины),
а враги у получателя пропали (вероятно, `Obj_ArmageddonGround_Destroy` или его коллайдер).
Кооператив 1.4.8: получатель запускает `StartCoroutine("Play_ArmageddonEffect")` и `("Play_ArmageddonLand")`;
обе стороны пишут в журнал раскладку взрыва по кадрам (вкл, рендер, α, кадр, масштаб) и число врагов 0–6 с.

Проверено на телефонах (1.4.8): повтор у получателя совпадает с оригиналом по кадрам (вспышка `Armageddon1…8` ~1,6 с,
затем Land/Destroy), и **наносит урон**: врагов 45 → 10 за 3 с (как у своего взрыва 49 → 9). Урон оставлен полным
(настройка «Урон магии друга, %» на Армагеддон не действует) — решение 23.10.2026, возможна балансная правка позже.
