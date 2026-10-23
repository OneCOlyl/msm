# Щит, Спутники, Духи — заклинания без снарядов

Цель: показать их у союзника-друга. Пересылка снарядов (`Bullet_Create`) их не покрывает: это объекты, которые висят или крутятся вокруг героя.
Данные — разведка героя (`coop_probe` 0.4.0–0.5.0, 2026-10-22), герой с прокачанными всеми 21 заклинаниями (выданы читом).

## Как устроено у героя (`C_Battle_AllyUnit`)

**Спутники** (заклинание 4)
- Контейнер `Obj_UnitSatelliteMid` («Unit_SatelliteMid», 14 детей `Center_SatelliteNN` → `Unit_SatelliteNN`), второй круг — `Obj_UnitSatelliteMidBack` (`…b`).
- Массивы: `Array_Obj_UnitSatellitesA/B` (GameObject[14]), `Array_Transform_ObjSatellitesA/B`, `Array_Transform_Center_UnitSatellitesA/B`, флаги `Array_IsSatelliteA_P/B_P` (bool[14]).
- Включено `UnitApply_SatelliteNum` штук (8 при уровне 8): первые N флагов `True`, объекты активны, стоят по кругу **радиусом 2,25** вокруг героя с равным шагом.
- Вращение — корутина `Play_SatelliteMid_Rotation` (есть варианты `_AntiGravity01/02`, `_Com02_A/B`, `_NuclearFusion`, `RotationBack`); скорость `UnitApply_SatelliteSpeed` (0,03). Раскладку делает `ApplySatelliteGroup` из `StartInformation_B`.
- Урон — `UnitApply_SatelliteDamage`, размер — `UnitApply_SatelliteSize`.

**Щит** (заклинание 6)
- Объект `Obj_Shield` («1100», 1 ребёнок) на герое; флаг `IsShield_P`; корутины `Play_Shield`, `Play_Shield_Com01`.
- Включённый: объект активен, картинка `SpriteRenderer_Shield` = «Shield2_03»; заряды `UnitApply_ShieldMaxNum` (2), `ShieldDelay` — перезарядка, `ShieldFirstON`.
- Пока заклинания нет, картинка щита пустая (спрайт задаёт корутина).

**Духи** (заклинание 3)
- Контейнер `Obj_UnitSpiritMid` (15 детей), `List_ObjSpirit` / `List_Transform_ObjSpirit` / `List_SpriteRenderer_Spirit` (индекс 0 — `null`, духи 1..14), флаги `List_IsSpirit_P`.
- Включено `UnitApply_SpiritNum` (7): флаги 1..7 `True`. Картинка «UnitSpirit1MotionA» есть и у выключенных.
- Стреляют снарядом `BulletID_Spirit` (19) из своих объектов (`Unit_Spirit01`…) — пересылается с «Кооператива» 1.3.2.

**Двойники** — `Obj_UnitDoppelganger01..09`; 9-й занимает союзник-друг (`modkit_companions`).

## Грабли
- `Renderer.enabled` читать нельзя: `ICall UnityEngine.Renderer::get_enabled was not resolved` (в этой сборке Unity).
- `UnityEngine.Object.Instantiate(obj)` работает, но возвращает обёртку `UnityEngine.Object` — у неё нет методов `GameObject` (`SetActive` → `MissingMethodException`). Нужна обёртка `GameObject` по адресу: `new GameObject(ptr)` (`Activator.CreateInstance(typeof(GameObject), ptr)`).

## План показа у союзника
1. Друг передаёт: число спутников (A и B), угол поворота круга, число духов, включён ли щит.
2. У получателя — копии его собственных объектов-шаблонов (`Instantiate`) вокруг союзника: спутники по кругу 2,25 с тем же углом, духи рядом, щит на союзнике.
3. Опыт копий (`coop_probe` 0.5.1): копии создаются, включаются, игра их не двигает — подтверждено.
4. Сделано в «Кооперативе» 1.4.0 (`mod/coop/Orbitals.cs`): поле `orb` в состоянии (`sa`, `sb`, `sp`, `sh`, `aa`, `ab`), 4 раза в секунду; угол экстраполируется по оценённой скорости вращения.

## Уточнения по журналам 1.4.5 (2026-10-23)
- Картинку Щита рисует `SpriteRenderer_Shield`, и он **не** внутри `Obj_Shield` («1100» — без рендера: `GetComponentInChildren` пуст у героя и у копии). Включён Щит — `IsShield_P` и непустая картинка (`Shield2_01…04` меняются с зарядами). С 1.4.6 копируется `SpriteRenderer_Shield.gameObject`, смещение и масштаб — от друга.
- Картинки Спутников и Духов — кадры анимации (`UnitSpirit4MotionA/B/C` у Спутника, `UnitSpirit1MotionA…D` у Духа), меняются постоянно; свою картинку у копии не трогаем.
- `Obj_ElectricZone` вспыхивает (активен не всё время) при `IsElectricZone_P = True`: «есть» — по флагу, «видна» — `activeSelf` при каждой отправке.
