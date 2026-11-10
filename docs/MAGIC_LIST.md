# Spells, traits, combinations and ultimates

English | [Русский](MAGIC_LIST.ru.md)

Extracted from the game tables on 2026-11-09: TextAssets `eng_/rus_Dictionary_Ability`, `_MagicCom`, `_Class` in
`assets/bin/Data/data.unity3d` (APK pulled from the phone on 2026-10-30). Names are the game's English strings.

How the game handles them:

- **Spell**: 21 spells, IDs 1–21 in the Ability table (type `마법`). The level is the number of entries with that ID in
  `Z_Database.List_AllyAbilityLearn`.
- **Trait** (type `특성`): a spell variant offered by the `Canvas_AbilityCh` window when the spell reaches the level in
  the `특성A레벨`/`특성B레벨` columns (4 and 7 for Magic Bolt, the last level for the others). IDs are
  361 + 9·(spell − 1) + 1…6, the spell is the `파생기술ID` column. A picked trait also goes to `List_AllyAbilityLearn`.
- **Combination** (MagicCom table): unlocked when both parts are learned. The game turns it on by itself on the hero
  recalc. Parts are traits; 18, 26–28 and 40 use passives (31 Intelligence, 35 Arcane Effuse, 36 Concentration).
  60 Deus Ex Machina has no parts.
- **Ultimate**: an upgrade of a combination. The `Obj_CanvasUltimate` window offers it when the combination is active
  and the hero's school matches the `궁극기 조건` column. `V_Ability_Manager.Button_Ultimate_Decision` writes the
  combination ID into the first zero slot of `Z_Database.List_ApplyUltimateON` and adds it to the `List_Book_Ultimate`
  book (saved to the profile). Rows with condition 0 have no ultimate; some of them carry a Korean placeholder name.

The `Canvas_MagicUpgrade` window (upgrade of a chosen spell, `V_Artifact_Manager.Button_MagicUpgrade_Decision`) is not
listed: it has no table of its own, and what exactly it changes was not examined.

Co-op check: `python tools/lab.py magic --kinds spells,traits,combos,ults` (`coop_lab` lab, methods `Learn`, `Combo`,
`Ultimate`).

## Spells

| ID | Spell | Max level |
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

## Traits (66)

| ID | Spell | Trait |
|---|---|---|
| 362 | Magic Bolt | Magic Arrow / Магическая стрела |
| 363 | Magic Bolt | Fireworks / Петарда |
| 364 | Magic Bolt | Fission / Раскол |
| 365 | Magic Bolt | Chain Casting / Непрерывная атака |
| 366 | Magic Bolt | Fire at Will / Лихорадка |
| 367 | Magic Bolt | Doppelganger / Двойник |
| 371 | Fireball | Big Bang / Большой взрыв |
| 372 | Fireball | Toxic Gas / Ядовитый газ |
| 373 | Fireball | Constellation / Созвездие |
| 380 | Spirit | Magic Missile / Заколдованная ракета |
| 381 | Spirit | Magic Blade / Магический клинок |
| 382 | Spirit | Familiar / Фамильяр |
| 389 | Satellite | Anti-Gravity / Антигравитация |
| 390 | Satellite | Will o’ the Wisp / Блуждающие огоньки |
| 391 | Satellite | Nuclear Fusion / Термоядерная реакция |
| 398 | Frost Nova | Absolute Zero / Абсолютный ноль |
| 399 | Frost Nova | Ice Age / Ледниковый период |
| 400 | Frost Nova | Earthquake / Землетрясение |
| 407 | Shield | Barrier / Барьер |
| 408 | Shield | Reconstruct / Переустановка |
| 409 | Shield | Destruction Field / Поле разрушения |
| 416 | Thunderstorm | Thunderclap / Гром среди ясного неба |
| 417 | Thunderstorm | Lightning Explosion / Грозовой взрыв |
| 418 | Thunderstorm | Judgment / Суд |
| 425 | Electric Zone | Electricity Blast / Эмиссия заряда |
| 426 | Electric Zone | EMP / ЭМИ |
| 427 | Electric Zone | Fire Aura / Огненная аура |
| 434 | Tsunami | Pole / Полюс |
| 435 | Tsunami | Siren’s Song / Песнь сирены |
| 436 | Tsunami | Great Eruption / Великое извержение |
| 443 | Meteor | Volcanic Eruption / Извержение вулкана |
| 444 | Meteor | Meteor Shower / Метеоритный дождь |
| 445 | Meteor | Asteroid / Астероид |
| 452 | Cloaking | Mirage / Мираж |
| 453 | Cloaking | Space Warp / Искажение пространства |
| 454 | Cloaking | Soul Harvest / Жатва душ |
| 461 | Cyclone | Extreme Weather / Штормовое предупреждение |
| 462 | Cyclone | Electric Orb / Электрическая сфера |
| 463 | Cyclone | Void / Космические пустоты |
| 470 | Electric Shock | Chain Lightning / Цепная молния |
| 471 | Electric Shock | High Output / Высокая производительность |
| 472 | Electric Shock | Superconductivity / Сверхпроводимость |
| 479 | Armageddon | Apocalypse / Апокалипсис |
| 480 | Armageddon | Rapture / Вознесение |
| 481 | Armageddon | Doom Prophet / Пророчество Судного дня |
| 488 | Incineration | Flamestrike / Пламенный удар |
| 489 | Incineration | Pyromaniac / Пироман |
| 490 | Incineration | Cerberus Flame / Пламя Цербера |
| 497 | Energy Bolt | Charged Particle / Заряженная частица |
| 498 | Energy Bolt | Spark / Искра |
| 499 | Energy Bolt | Flare / Вспышка |
| 506 | Blizzard | Sub-Zero / Лютый холод |
| 507 | Blizzard | Shooting Star / Падающая звезда |
| 508 | Blizzard | Conflagration / Пожар |
| 515 | Arcane Ray | Reflect / Отражение |
| 516 | Arcane Ray | Disintegration Ray / Разрушительный луч |
| 517 | Arcane Ray | Electromagnetic Burst / Электромагнитное излучение |
| 524 | Magic Circle | Unleash Mana / Магическое высвобождение |
| 525 | Magic Circle | Material Synthesis / Пластичность материи |
| 526 | Magic Circle | Great Magic Circle / Большой магический круг |
| 533 | Lava Zone | Melt / Плавление |
| 534 | Lava Zone | Crater / Кратер |
| 535 | Lava Zone | Magma Eruption / Излияние магмы |
| 542 | Flash Shock | Wipeout / Уничтожение |
| 543 | Flash Shock | Ragnarok / Рагнарок |
| 544 | Flash Shock | Satan's Nails / Когти дьявола |

## Combinations (63)

| ID | Combination | Parts |
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

## Ultimates (28)

| Combination | Ultimate | School |
|---|---|---|
| 1 Empyrean Wrath | Stormbringer / Штормбрингер | 4 Shaman |
| 2 Telekinetic Sword | Aura Blade / Аура Блейд | 7 Summoner |
| 3 Crazed Spray | Tempester / Темпестер | 13 Alchemist |
| 5 War Climate | Ground Zero / Граунд Зеро | 5 Warlock |
| 6 Demon Equation | Nemesis / Немезис | 11 Pyromancer |
| 7 Frenzy | Berserk / Берсерк | 22 Battlemage |
| 10 Vortex | Darkness / Тьма | 10 Druid |
| 14 Neutron Generation | Omega / Омега | 10 Druid |
| 16 Plasma Ray | Quantum Laser / Квантовый лазер | 9 Occultist |
| 17 Lightning Bolts | Death Storm / Дес Шторм | 4 Shaman |
| 19 Inferno | Inquisitor / Инквизитор | 23 Warlord |
| 23 Great Flood | Mega Tsunami / Мега-цунами | 17 Arbiter |
| 24 Snowflake Blossom | Zero Nova / Нулевая нова | 3 Cryomancer |
| 29 Origin Explosion | Blitz / Блиц | 13 Alchemist |
| 32 Hyperion | Abaddon / Абаддон | 2 Astronomer |
| 33 Avatar | Equilibrium / Эквилибриум | 1 Wizard |
| 34 Heart of Lightning | Arc / Арка | 12 Sorcerer |
| 35 Great Impact | Giant Impact / Гигантский удар | 5 Warlock |
| 37 Meisner Effect | Lockdown / Локдаун | 16 Electromancer |
| 38 Energy Bombardment | Buster Beam / Бастер Бим | 9 Occultist |
| 40 Teleport | Hallucination / Галлюцинация | 25 not in table |
| 43 Mana Spear | Valkyrie / Валькирия | 7 Summoner |
| 44 Gate | Ascension / Асенсия | 22 Battlemage |
| 45 Magnetic Field | Core / Ядро | 2 Astronomer |
| 51 Genesis | Cosmos / Космос | 21 Mage |
| 55 Hellfire | Brimstone / Бримстоун | 23 Warlord |
| 56 Age of the Sun | Overload / Оверлорд | 15 Witch |
| 57 Destroyer | Terminator / Терминатор | 16 Electromancer |
