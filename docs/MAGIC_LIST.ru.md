# Магии, особенности, комбинации и ультимейты

[English](MAGIC_LIST.md) | Русский

Список выгружен из таблиц игры 2026-11-09: TextAsset `eng_/rus_Dictionary_Ability`, `_MagicCom`, `_Class` в
`assets/bin/Data/data.unity3d` (APK с телефона, снят 2026-10-30). Названия — английские и русские из игры.

Как это устроено в игре:

- **Магия** — 21 заклинание, ID 1–21 в таблице Ability (тип `마법`). Уровень — число записей ID в
  `Z_Database.List_AllyAbilityLearn`.
- **Особенность** (тип `특성`) — вариант магии, который предлагает окно `Canvas_AbilityCh`, когда магия доходит до уровня
  из колонок `특성A레벨`/`특성B레벨` (у Пули 4 и 7, у остальных — последний уровень). ID 361 + 9·(магия − 1) + 1…6,
  магия — колонка `파생기술ID`. Выбранная особенность тоже идёт в `List_AllyAbilityLearn`.
- **Комбинация** (таблица MagicCom) — открывается, когда выучены обе составляющие. Игра включает её сама при пересчёте
  героя. Составляющие — особенности, у 18, 26–28 и 40 есть пассивы (31 Intelligence, 35 Arcane Effuse, 36
  Concentration). У 60 Deus Ex Machina составляющих нет.
- **Ультимейт** — усиление комбинации. Окно `Obj_CanvasUltimate` предлагает его, если комбинация есть и школа героя
  совпадает с колонкой `궁극기 조건`. `V_Ability_Manager.Button_Ultimate_Decision` пишет номер комбинации в первую нулевую
  ячейку `Z_Database.List_ApplyUltimateON` и добавляет его в книгу `List_Book_Ultimate` (сохраняется в профиль).
  У строк с условием 0 ультимейта нет, у части там корейская заглушка вместо имени.

Окно `Canvas_MagicUpgrade` (усиление выбранной магии, `V_Artifact_Manager.Button_MagicUpgrade_Decision`) в список не
вошло: своей таблицы у него нет, что именно оно меняет, не разбиралось.

Проверка в кооперативе: `python tools/lab.py magic --kinds spells,traits,combos,ults` (полигон `coop_lab`, методы
`Learn`, `Combo`, `Ultimate`).

## Магии

| ID | Магия | Макс. уровень |
|---|---|---|
| 1 | Magic Bolt / Магическая пуля | 7 |
| 2 | Fireball / Огненный шар | 7 |
| 3 | Spirit / Дух | 7 |
| 4 | Satellite / Спутник | 7 |
| 5 | Frost Nova / Ледяные иглы | 7 |
| 6 | Shield / Щит | 5 |
| 7 | Thunderstorm / Молния | 7 |
| 8 | Electric Zone / Область электротока | 7 |
| 9 | Tsunami / Цунами | 7 |
| 10 | Meteor / Метеорит | 7 |
| 11 | Cloaking / Маскировка | 5 |
| 12 | Cyclone / Вихрь | 7 |
| 13 | Electric Shock / Электрический удар | 7 |
| 14 | Armageddon / Армагеддон | 5 |
| 15 | Incineration / Испепеление | 7 |
| 16 | Energy Bolt / Энергетическая пуля | 7 |
| 17 | Blizzard / Метель | 7 |
| 18 | Arcane Ray / Таинственный луч | 7 |
| 19 | Magic Circle / Магический круг | 5 |
| 20 | Lava Zone / Лавовая область | 7 |
| 21 | Flash Shock / Световая вспышка | 7 |

## Особенности (66)

| ID | Магия | Особенность |
|---|---|---|
| 362 | Magic Bolt (Магическая пуля) | Magic Arrow / Магическая стрела |
| 363 | Magic Bolt (Магическая пуля) | Fireworks / Петарда |
| 364 | Magic Bolt (Магическая пуля) | Fission / Раскол |
| 365 | Magic Bolt (Магическая пуля) | Chain Casting / Непрерывная атака |
| 366 | Magic Bolt (Магическая пуля) | Fire at Will / Лихорадка |
| 367 | Magic Bolt (Магическая пуля) | Doppelganger / Двойник |
| 371 | Fireball (Огненный шар) | Big Bang / Большой взрыв |
| 372 | Fireball (Огненный шар) | Toxic Gas / Ядовитый газ |
| 373 | Fireball (Огненный шар) | Constellation / Созвездие |
| 380 | Spirit (Дух) | Magic Missile / Заколдованная ракета |
| 381 | Spirit (Дух) | Magic Blade / Магический клинок |
| 382 | Spirit (Дух) | Familiar / Фамильяр |
| 389 | Satellite (Спутник) | Anti-Gravity / Антигравитация |
| 390 | Satellite (Спутник) | Will o’ the Wisp / Блуждающие огоньки |
| 391 | Satellite (Спутник) | Nuclear Fusion / Термоядерная реакция |
| 398 | Frost Nova (Ледяные иглы) | Absolute Zero / Абсолютный ноль |
| 399 | Frost Nova (Ледяные иглы) | Ice Age / Ледниковый период |
| 400 | Frost Nova (Ледяные иглы) | Earthquake / Землетрясение |
| 407 | Shield (Щит) | Barrier / Барьер |
| 408 | Shield (Щит) | Reconstruct / Переустановка |
| 409 | Shield (Щит) | Destruction Field / Поле разрушения |
| 416 | Thunderstorm (Молния) | Thunderclap / Гром среди ясного неба |
| 417 | Thunderstorm (Молния) | Lightning Explosion / Грозовой взрыв |
| 418 | Thunderstorm (Молния) | Judgment / Суд |
| 425 | Electric Zone (Область электротока) | Electricity Blast / Эмиссия заряда |
| 426 | Electric Zone (Область электротока) | EMP / ЭМИ |
| 427 | Electric Zone (Область электротока) | Fire Aura / Огненная аура |
| 434 | Tsunami (Цунами) | Pole / Полюс |
| 435 | Tsunami (Цунами) | Siren’s Song / Песнь сирены |
| 436 | Tsunami (Цунами) | Great Eruption / Великое извержение |
| 443 | Meteor (Метеорит) | Volcanic Eruption / Извержение вулкана |
| 444 | Meteor (Метеорит) | Meteor Shower / Метеоритный дождь |
| 445 | Meteor (Метеорит) | Asteroid / Астероид |
| 452 | Cloaking (Маскировка) | Mirage / Мираж |
| 453 | Cloaking (Маскировка) | Space Warp / Искажение пространства |
| 454 | Cloaking (Маскировка) | Soul Harvest / Жатва душ |
| 461 | Cyclone (Вихрь) | Extreme Weather / Штормовое предупреждение |
| 462 | Cyclone (Вихрь) | Electric Orb / Электрическая сфера |
| 463 | Cyclone (Вихрь) | Void / Космические пустоты |
| 470 | Electric Shock (Электрический удар) | Chain Lightning / Цепная молния |
| 471 | Electric Shock (Электрический удар) | High Output / Высокая производительность |
| 472 | Electric Shock (Электрический удар) | Superconductivity / Сверхпроводимость |
| 479 | Armageddon (Армагеддон) | Apocalypse / Апокалипсис |
| 480 | Armageddon (Армагеддон) | Rapture / Вознесение |
| 481 | Armageddon (Армагеддон) | Doom Prophet / Пророчество Судного дня |
| 488 | Incineration (Испепеление) | Flamestrike / Пламенный удар |
| 489 | Incineration (Испепеление) | Pyromaniac / Пироман |
| 490 | Incineration (Испепеление) | Cerberus Flame / Пламя Цербера |
| 497 | Energy Bolt (Энергетическая пуля) | Charged Particle / Заряженная частица |
| 498 | Energy Bolt (Энергетическая пуля) | Spark / Искра |
| 499 | Energy Bolt (Энергетическая пуля) | Flare / Вспышка |
| 506 | Blizzard (Метель) | Sub-Zero / Лютый холод |
| 507 | Blizzard (Метель) | Shooting Star / Падающая звезда |
| 508 | Blizzard (Метель) | Conflagration / Пожар |
| 515 | Arcane Ray (Таинственный луч) | Reflect / Отражение |
| 516 | Arcane Ray (Таинственный луч) | Disintegration Ray / Разрушительный луч |
| 517 | Arcane Ray (Таинственный луч) | Electromagnetic Burst / Электромагнитное излучение |
| 524 | Magic Circle (Магический круг) | Unleash Mana / Магическое высвобождение |
| 525 | Magic Circle (Магический круг) | Material Synthesis / Пластичность материи |
| 526 | Magic Circle (Магический круг) | Great Magic Circle / Большой магический круг |
| 533 | Lava Zone (Лавовая область) | Melt / Плавление |
| 534 | Lava Zone (Лавовая область) | Crater / Кратер |
| 535 | Lava Zone (Лавовая область) | Magma Eruption / Излияние магмы |
| 542 | Flash Shock (Световая вспышка) | Wipeout / Уничтожение |
| 543 | Flash Shock (Световая вспышка) | Ragnarok / Рагнарок |
| 544 | Flash Shock (Световая вспышка) | Satan's Nails / Когти дьявола |

## Комбинации (63)

| ID | Комбинация | Составляющие |
|---|---|---|
| 1 | Empyrean Wrath / Гнев небес | Lightning Explosion (417) + Meteor Shower (444) |
| 2 | Telekinetic Sword / Парящие мечи | Magic Blade (381) + Chain Lightning (470) |
| 3 | Crazed Spray / Световая стрельба | Charged Particle (497) + Familiar (382) |
| 4 | Terra Current / Поток Теры | Electromagnetic Burst (517) + Superconductivity (472) |
| 5 | War Climate / Военный климат | Meteor Shower (444) + Shooting Star (507) |
| 6 | Demon Equation / Уравнение демона | Big Bang (371) + Flare (499) |
| 7 | Frenzy / Безумие | Satan's Nails (544) + Fire at Will (366) |
| 8 | Ghastly Rampage / Пандемониум | Constellation (373) + Will o’ the Wisp (390) |
| 9 | Reaction / Противодействие | Spark (498) + Destruction Field (409) |
| 10 | Vortex / Вихрь | Void (463) + Anti-Gravity (389) |
| 11 | Breath of Fire / Огненное дыхание | Cerberus Flame (490) + Reflect (515) |
| 12 | Ether Blaze / Поджог эфира | Conflagration (508) + Flamestrike (488) |
| 13 | Orbital Strike / Орбитальная бомбардировка | Lightning Explosion (417) + Shooting Star (507) |
| 14 | Neutron Generation / Генерация нейтронов | Electric Orb (462) + Nuclear Fusion (391) |
| 15 | Winter Storm / Зимний шторм | Sub-Zero (506) + Toxic Gas (372) |
| 16 | Plasma Ray / Плазменный луч | Disintegration Ray (516) + Nuclear Fusion (391) |
| 17 | Lightning Bolts / Взрыв молний | Thunderclap (416) + Chain Lightning (470) |
| 18 | Photon Explosion / Квантовый взрыв | Destruction Field (409) + Arcane Effuse (35) |
| 19 | Inferno / Инферно | Flamestrike (488) + Melt (533) |
| 20 | Lightning Calibration / Отладка молний | Electricity Blast (425) + Will o’ the Wisp (390) |
| 21 | Furnace / Доменная печь | Magma Eruption (535) + Volcanic Eruption (443) |
| 22 | Current Overload / Буйство электричества | Chain Lightning (470) + Familiar (382) |
| 23 | Great Flood / Потоп | Siren’s Song (435) + Extreme Weather (461) |
| 24 | Snowflake Blossom / Снежное цветение | Ice Age (399) + Pole (434) |
| 25 | Ruler of Storms / Повелитель тайфуна | Extreme Weather (461) + Fire at Will (366) |
| 26 | Overmind / Сверхразум | Intelligence (31) + Great Magic Circle (526) |
| 27 | Perpetual Engine / Безграничная энергия | Unleash Mana (524) + Intelligence (31) |
| 28 | Gate of Creation / Врата созидания | Intelligence (31) + Material Synthesis (525) |
| 29 | Origin Explosion / Отправной взрыв | Flare (499) + Constellation (373) |
| 30 | Prism Spray / Призменная лихорадка | Reflect (515) + Fire at Will (366) |
| 31 | Great Rift / Большой разлом | Earthquake (400) + Volcanic Eruption (443) |
| 32 | Hyperion / Гиперион | Nuclear Fusion (391) + Asteroid (445) |
| 33 | Avatar / Аватар | Doppelganger (367) + Magic Missile (380) |
| 34 | Heart of Lightning / Сердце грозы | Chain Lightning (470) + Charged Particle (497) |
| 35 | Great Impact / Великое столкновение | Asteroid (445) + Shooting Star (507) |
| 36 | Frozen Heart / Ледяное сердце | Fire Aura (427) + Ice Age (399) |
| 37 | Meisner Effect / Эффект Мейснера | EMP (426) + Superconductivity (472) |
| 38 | Energy Bombardment / Энергетическая насыщенность | Disintegration Ray (516) + Ragnarok (543) |
| 39 | Super Cyclone / Супертайфун | Extreme Weather (461) + Fire Aura (427) |
| 40 | Teleport / Телепорт | Space Warp (453) + Concentration (36) |
| 41 | Flame Storm / Пламенный шторм | Volcanic Eruption (443) + Cerberus Flame (490) |
| 42 | Pyrotechnics / Фейерверк | Fireworks (363) + Flare (499) |
| 43 | Mana Spear / Магическое копье | Magic Missile (380) + Wipeout (542) |
| 44 | Gate / Ворота | Ragnarok (543) + Magic Missile (380) |
| 45 | Magnetic Field / Генерация магнитного поля | Anti-Gravity (389) + Electricity Blast (425) |
| 46 | Black Death / Чёрная смерть | Toxic Gas (372) + Fire at Will (366) |
| 47 | Lightning Breath / Дыхание молнии | Superconductivity (472) + Flamestrike (488) |
| 48 | Phoenix / феникс | Great Eruption (436) + Conflagration (508) |
| 49 | Brandish / Брандиш | Wipeout (542) + Magic Blade (381) |
| 50 | Black Hole / черная дыра | Void (463) + Nuclear Fusion (391) |
| 51 | Genesis / Бытие | Shooting Star (507) + Constellation (373) |
| 52 | Astrape / Астрапе | Judgment (418) + Asteroid (445) |
| 53 | Supernova / сверхновая | Familiar (382) + Flare (499) |
| 54 | Blaster / Бластер | Magic Arrow (362) + Wipeout (542) |
| 55 | Hellfire / Адское пламя | Pyromaniac (489) + Conflagration (508) |
| 56 | Age of the Sun / Эра Солнца | Crater (534) + Nuclear Fusion (391) |
| 57 | Destroyer / Разрушитель | Electricity Blast (425) + Destruction Field (409) |
| 58 | Genocide / Геноцид | Disintegration Ray (516) + Familiar (382) |
| 59 | Exidium / Эксидиум | Wipeout (542) + Rapture (480) |
| 60 | Deus Ex Machina / Деус экс махина | — |
| 61 | Glacium / Глейсиум | Pole (434) + Ice Age (399) |
| 62 | Soul Blade / Соул Блейд | Will o’ the Wisp (390) + Magic Blade (381) |
| 63 | Discharge / Дисчардж | High Output (471) + Lightning Explosion (417) |

## Ультимейты (28)

| Комбинация | Ультимейт | Школа |
|---|---|---|
| 1 Empyrean Wrath | Stormbringer / Штормбрингер | 4 Shaman / Шаман |
| 2 Telekinetic Sword | Aura Blade / Аура Блейд | 7 Summoner / Призыватель |
| 3 Crazed Spray | Tempester / Темпестер | 13 Alchemist / Алхимик |
| 5 War Climate | Ground Zero / Граунд Зеро | 5 Warlock / Чернокнижник |
| 6 Demon Equation | Nemesis / Немезис | 11 Pyromancer / Пиромант |
| 7 Frenzy | Berserk / Берсерк | 22 Battlemage / Боевой маг |
| 10 Vortex | Darkness / Тьма | 10 Druid / Друид |
| 14 Neutron Generation | Omega / Омега | 10 Druid / Друид |
| 16 Plasma Ray | Quantum Laser / Квантовый лазер | 9 Occultist / Мистик |
| 17 Lightning Bolts | Death Storm / Дес Шторм | 4 Shaman / Шаман |
| 19 Inferno | Inquisitor / Инквизитор | 23 Warlord / Полководец |
| 23 Great Flood | Mega Tsunami / Мега-цунами | 17 Arbiter / Заклинатель |
| 24 Snowflake Blossom | Zero Nova / Нулевая нова | 3 Cryomancer / Криомант |
| 29 Origin Explosion | Blitz / Блиц | 13 Alchemist / Алхимик |
| 32 Hyperion | Abaddon / Абаддон | 2 Astronomer / Астроном |
| 33 Avatar | Equilibrium / Эквилибриум | 1 Wizard / Чародей |
| 34 Heart of Lightning | Arc / Арка | 12 Sorcerer / Колдун |
| 35 Great Impact | Giant Impact / Гигантский удар | 5 Warlock / Чернокнижник |
| 37 Meisner Effect | Lockdown / Локдаун | 16 Electromancer / Электромант |
| 38 Energy Bombardment | Buster Beam / Бастер Бим | 9 Occultist / Мистик |
| 40 Teleport | Hallucination / Галлюцинация | 25 нет в таблице |
| 43 Mana Spear | Valkyrie / Валькирия | 7 Summoner / Призыватель |
| 44 Gate | Ascension / Асенсия | 22 Battlemage / Боевой маг |
| 45 Magnetic Field | Core / Ядро | 2 Astronomer / Астроном |
| 51 Genesis | Cosmos / Космос | 21 Mage / Маг |
| 55 Hellfire | Brimstone / Бримстоун | 23 Warlord / Полководец |
| 56 Age of the Sun | Overload / Оверлорд | 15 Witch / Ведьма |
| 57 Destroyer | Terminator / Терминатор | 16 Electromancer / Электромант |
