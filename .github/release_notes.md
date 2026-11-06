## Не выпущено

- Smaller host messages with special enemies: an enemy's look is sent after it appears or changes and then once every 2 s, not in every send. The name, max HP, kills and lobby are sent on change and once every 2 s. All players need the same version.
- For developers: network numbers at the end of a battle in `modkit.log` (`сеть за … с`: bytes per second sent and received, the longest line, ping average, maximum and spread), the same in `Lab.CoopStatus`. `tools/lab.py`: the `perf` scenario (200 enemies, all spells maxed, 60 s), `--json`, exit code 1 on any failure, adb from `ADB` or `PATH`. The `device-test` skill for agents.
- For developers: the version comes only from the git tag: `vX.Y.Z` for co-op, `tools-X.Y.Z` for the three developer mods (one version for all three). The repository keeps `0.0.0-dev` in `mod.json` and `.csproj`; `build.sh` takes the version from `VERSION`, else from `git describe`, and writes it only into `mod.json` inside the archive. A release is published by pushing the tag. A build with uncommitted changes gets `-dirty.<time>`, so `tools/phones.py` installs it without bumping the version.

**По-русски:**

- Меньше сообщений хоста при особых врагах: вид врага уходит после появления или изменения и затем раз в 2 с, а не в каждой отправке. Имя, здоровье максимум, убийства и лобби — при изменении и раз в 2 с. Нужна одна версия у всех игроков.
- Для разработчиков: сетевые цифры в итоге боя в `modkit.log` (`сеть за … с`: байт в секунду на отправку и приём, самая длинная строка, пинг: среднее, максимум, разброс), то же в `Lab.CoopStatus`. `tools/lab.py`: сценарий `perf` (200 врагов, все магии на максимум, 60 с), `--json`, код выхода 1 при любом провале, adb из `ADB` или `PATH`. Скилл `device-test` для агентов.
- Для разработчиков: версия берётся только из метки git: `vX.Y.Z` у «Кооператива», `tools-X.Y.Z` у трёх модов разработчика (одна версия на все три). В репозитории в `mod.json` и `.csproj` стоит `0.0.0-dev`; `build.sh` берёт версию из `VERSION`, иначе из `git describe`, и пишет её только в `mod.json` внутри архива. Релиз выходит по пушу метки. Сборка с незакоммиченными правками получает `-dirty.<время>`, поэтому `tools/phones.py` ставит её без повышения версии.

## Co-op 1.7.8

- **Shared chests.** On the area screen the host picks «Сундуки: общие» (shared, the default) or «Сундуки: у каждого свои» (separate). With shared chests only the host's game places them: a guest gets the same chest at the same spot, and the guest's own chests are removed. A chest goes to one player only: the host takes it at once, a guest who reaches it asks the host, and the host gives it to the first request. The others' chest disappears.
- Chests from an artifact or ability choice (for example, three chests from an artifact) stay with the player who chose.
- All players need 1.7.8 for shared chests.
- For developers: `bash build.sh` runs the tests in parallel, about 80 s instead of 5 minutes. `coop_debug` 0.1.7: `set` accepts a Vector3 as `(x, y, z)`.

**По-русски:**

- **Общие сундуки.** На экране локации хост выбирает «Сундуки: общие» (по умолчанию) или «Сундуки: у каждого свои». С общими сундуками их кладёт только игра хоста: у гостя такой сундук лежит на том же месте, а свои сундуки его игры убираются. Сундук достаётся одному игроку: хост берёт его сразу, гость, дойдя до сундука, просит его у хоста, и хост отдаёт сундук первому, кто попросил. У остальных сундук пропадает.
- Сундуки от выбора артефакта или умения (например, три сундука от артефакта) остаются у того, кто выбирал.
- Общие сундуки работают, когда у всех игроков 1.7.8.
- Для разработчиков: `bash build.sh` гоняет тесты параллельно, около 80 с вместо 5 минут. `coop_debug` 0.1.7: `set` принимает Vector3 в виде `(x, y, z)`.

## Co-op 1.7.4

- **The friend's upgraded and combined spells look the same as in their game.** The game takes a projectile's size, area, speed, duration and count from the hero that casts it, so a friend's repeated spell used to get your upgrades: a Flame Storm or a Meteor with the friend's +20% size was smaller and shorter at your end. Now the friend's game sends these values once a second, and your game uses them for each repeat of the friend's spell. Damage is still computed from your hero.
- **Spells without a target point are no longer lost.** Electric Shock, Blizzard, Chain Lightning and other spells the game creates at (0, 0) and aims by itself didn't reach a friend who was more than 40 units from the map centre. Now they are repeated at the friend too.
- **A shorter log.** One-off investigation logs are removed: enemy audit every 30 s, battle clock fields, obelisk coroutines, special enemy dumps, Electric Zone and Armageddon frame logs. The battle totals (shared enemies, magic, frames) remain.
- Both players need 1.7.4 for the friend's spells to look the same.

**По-русски:**

- **Прокачанные и комбинированные магии друга выглядят как у него.** Размер, площадь, скорость, время и число снарядов игра берёт у героя, который кастует, поэтому повтор магии друга получал ваши улучшения: Flame Storm или Метеорит с +20% размера у друга выходили у вас меньше и короче. Теперь игра друга раз в секунду присылает эти значения, и ваша игра подставляет их в каждый повтор его магии. Урон, как и раньше, считается по вашему герою.
- **Магии без точки цели больше не теряются.** Электричество, Метель, Chain Lightning и другие магии, которые игра создаёт в (0, 0) и наводит сама, не доходили до друга, если он стоял дальше 40 единиц от центра карты. Теперь они повторяются и у него.
- **Журнал короче.** Убраны разовые журналы расследований: сверка врагов раз в 30 с, поля часов боя, корутины обелиска, дампы особых врагов, кадры Электрозоны и Армагеддона. Итоги боя (общие враги, магия, кадры) остались.
- Чтобы магии друга выглядели одинаково, версия 1.7.4 нужна обоим игрокам.

## Co-op 1.7.0

- **Lobby.** The host picks the area, difficulty and mode (normal or Infinite Mode); each player still picks their own character. On a guest's area screen the host's choice is shown, the area arrows are hidden, and the enter buttons stay hidden until the host starts. A guest waiting on that screen enters the battle automatically, through the same button the host pressed. The host's area screen shows how many guests are ready. A guest enters the host's area even if they haven't unlocked it.
- **Shared enemies are on by default.** When updating from 1.6 the mod turns the setting on once; after that the player's choice is kept. Shared pause was already on by default.

**По-русски:**

- **Лобби.** Локацию, сложность и режим (обычный или Infinite Mode) выбирает хост, персонажа каждый выбирает сам. У гостя на экране локации стоит выбор хоста, стрелки скрыты, а кнопки входа появляются только после старта хоста. Гость, ждавший на этом экране, входит в бой сам, той же кнопкой, что нажал хост. У хоста на экране локации видно, сколько гостей готовы. Гость входит в локацию хоста, даже если у себя её не открыл.
- **Общие враги включены по умолчанию.** При обновлении с 1.6 мод один раз включает настройку сам, дальше выбор игрока сохраняется. Общая пауза и так была включена по умолчанию.

## Co-op 1.6.116

- **Shared enemies: big bosses appear at the guest too.** On Terra at 5:00 the host got three huge, tough bosses, but the guest didn't. Normal enemy copies used up the guest's boss preforms, and the game couldn't create a boss on a normal mob's preform. Now normal copies take their own preforms, and the host's bosses appear at the guest together with the host. If an enemy copy still fails to be created, the mod retries in 2 s (previously never).
- **Enemy audit log.** Every 30 s of battle time the host and guest log how many enemies of each type and kind (normal, special, epic, transition) were created at the host, reached the guest and appeared there. The log also records every start of the battle clock: if the guest's time runs in two-second steps again, the log will show where it came from.

**По-русски:**

- **Общие враги: большие боссы появляются и у гостя.** На Terra в 5:00 у хоста выходили три огромных живучих босса, а у гостя их не было. Обычные копии врагов расходовали у гостя заготовки боссов, и игра не могла создать босса на заготовке обычного моба. Теперь обычные копии берут свои заготовки, а боссы хоста появляются у гостя вместе с хостом. Если копия врага всё же не создалась, мод через 2 с пробует снова (раньше — никогда).
- **Журнал сверки врагов.** Раз в 30 с времени боя хост и гость пишут в журнал, сколько врагов каждого типа и вида (обычные, особые, эпические, переход) создано у хоста, дошло до гостя и появилось у него. Ещё в журнал попадает каждый запуск часов боя: если у гостя время снова пойдёт по две секунды, по журналу будет видно, откуда.

## Co-op 1.6.110

- **Shared enemies: the artifact merchant appears at the guest too.** After the first boss the guest's battle progression stopped: enemies stopped getting stronger, and the artifact merchant who came to the host didn't come to the guest. Now after each host obelisk the guest's battle progresses further, and the merchant appears for both.

**По-русски:**

- **Общие враги: продавец артефактов появляется и у гостя.** После первого босса ход боя у гостя останавливался: враги переставали усиливаться, и продавец артефактов, который приходил к хосту, к гостю не приходил. Теперь после каждого обелиска хоста ход боя у гостя идёт дальше, и продавец появляется у обоих.

## Co-op 1.6.108

- **Shared enemies: the Terra special obelisk is host-only.** The stone obelisk that lies next to the heroes at the start of a battle on Terra is removed at the guest. When the host picks it up, the guest immediately gets the same «Obelisk» artifact choice window. The following obelisks (after a boss) are normal ones, as at the host. Previously the guest had this obelisk too, and on the host's pickup the guest got a choice of different artifacts and also pulled all the mana on the field.
- **Shared enemies: the guest's level no longer overtakes the host's after an obelisk.** When the host picked up an obelisk (after a boss or the Terra special one), the guest's area changed twice, and the guest got a double area-change reward: on Terra right after the start the guest was level 18 and the host level 13, and the guest had more skill choices. Now the guest's area changes once, as at the host.

**По-русски:**

- **Общие враги: особый обелиск Terra — только у хоста.** Каменный обелиск, который на Terra лежит рядом с героями в начале боя, у гостя убирается. Когда хост его поднимает, гость сразу получает то же окно выбора артефакта «Obelisk». Следующие обелиски (после босса) — обычные, как у хоста. Раньше этот обелиск был и у гостя, а при подборе хостом гость получал выбор из других артефактов и вдобавок притягивал к себе всю ману с поля.
- **Общие враги: уровень гостя больше не обгоняет хоста после обелиска.** Когда хост поднимал обелиск (после босса или особый на Terra), у гостя область менялась дважды, и он получал за смену области двойную награду: на Terra сразу после старта у гостя был 18-й уровень, у хоста 13-й, и выборов умений у гостя было больше. Теперь область у гостя меняется один раз, как у хоста.

## Co-op 1.6.102

- **Battle clocks align without pauses.** If players' clocks drift apart, the lagging player catches up by speeding up, and the player ahead no longer pauses with «Ждём … (часы)» (Waiting… (clock)), «перед боссом» (before the boss) or at battle start. The speed-up is ×1.25 to ×2 depending on the lag; in the 30 seconds before a boss wave it starts earlier and is stronger. Pause remains only for skill choice and while a friend hasn't entered the battle.
- **The speed-up actually catches up now.** The game sets the battle speed itself every frame, and the mod's previous speed-up was immediately overwritten. Also, on a weak phone the game slows time at low frame rates, which is why the guest's clock fell behind. Now the mod speeds up through the game speed bonus and lifts this slowdown while catching up: a 10-second lag is caught up in about 40 seconds.

**По-русски:**

- **Часы боя выравниваются без пауз.** Если часы игроков разошлись, отстающий догоняет ускорением, а тот, кто впереди, больше не встаёт на паузу «Ждём … (часы)», «перед боссом» или в начале боя. Ускорение — от ×1,25 до ×2 по величине отставания, за 30 секунд до босс-волны — раньше и сильнее. Пауза осталась только на выбор умения и пока друг не вошёл в бой.
- **Ускорение теперь действительно догоняет.** Игра каждый кадр сама выставляет скорость боя, и прежнее ускорение мода тут же стиралось. Кроме того, на слабом телефоне игра замедляет время при низкой частоте кадров — из-за этого часы гостя и отставали. Теперь мод ускоряет через бонус скорости игры и на время догона снимает это замедление: отставание в 10 секунд догоняется примерно за 40 секунд.

## Co-op 1.6.98

- **Shared enemies: an enemy copy at the guest dies immediately.** When an enemy dies at the host, its copy at the guest dies without a hit calculation (hit effects and statuses). The guest still gets the kill, as before.

**По-русски:**

- **Общие враги: копия врага у гостя умирает сразу.** Когда враг погиб у хоста, его копия у гостя умирает без расчёта удара (эффектов попадания и статусов). Убийство гостю засчитывается, как и раньше.

## Co-op 1.6.97

- **Shared enemies: enemy copies at the guest are created faster.** The mod sets up a new copy (health, speed, size) directly in the game's memory, not through slow field lookup. On a weak phone copy setup became about 40% faster, and creating a whole copy 10–25% faster.

**По-русски:**

- **Общие враги: копии врагов у гостя создаются быстрее.** Мод настраивает новую копию (здоровье, скорость, размер) напрямую в памяти игры, а не через медленный поиск полей. На слабом телефоне настройка копии стала примерно на 40% быстрее, а создание копии целиком — на 10–25%.

## Co-op 1.6.96

- **Shared enemies: the guest has the same enemies nearby as the host.** If the host has more enemies than fit in one send, the guest gets those closest to the friends, not the first ones in the game's list. Copies are created starting from the ones closest to the guest's hero; if copies are waiting to be created right on screen, twice as many are created per frame.
- **Shared enemies: less load at the guest.** Off-screen copies are repositioned every 3 frames, and copy speed is zeroed spread out over frames rather than all at once every 0.5 s (the "cleanup" frame reached 240 ms). The enemy preform pool isn't re-read before each copy; a copy's health and "is alive" are read directly from the game's memory.
- **Shared enemies: copies aren't wasted.** After a mass death at the host (Armageddon) copies at the guest are removed faster instead of standing for seconds. The kill queue no longer clogs with uncreated and already dead copies. If the game gave the object of a copy that died at the guest to a new copy, the old copy is killed, not the new one (previously the new one vanished and was recreated 2 s later).

**По-русски:**

- **Общие враги: у гостя рядом с ним те же враги, что у хоста.** Если врагов у хоста больше, чем помещается в одну отправку, гостю уходят ближайшие к друзьям, а не первые по списку игры. Копии создаются начиная с ближайших к герою гостя; если копии ждут создания прямо на экране, их создаётся вдвое больше за кадр.
- **Общие враги: меньше нагрузки у гостя.** Копии за экраном переставляются раз в 3 кадра, скорость копий гасится вразброс по кадрам, а не всем сразу раз в 0,5 с (кадр «чистки» доходил до 240 мс). Пул заготовок врагов не перечитывается перед каждой копией, здоровье и «жива ли» копии читаются напрямую из памяти игры.
- **Общие враги: копии не пропадают зря.** После массовой смерти у хоста (Армагеддон) копии у гостя убираются быстрее, а не стоят секундами. Очередь убийств больше не забивается несозданными и уже погибшими копиями. Если объект погибшей у гостя копии игра отдала новой, убивается старая копия, а не новая (раньше новая пропадала и через 2 с создавалась снова).

## Co-op 1.6.88

- **Shared enemies: special mobs at the guest are the same as at the host.** Transition mobs (after picking up the power-up sphere), epic and special mobs of the host reach the guest as special copies: with the same strength, the horned marker above the mob and the pink outline. Previously the guest saw them as normal mobs. The guest's transition mobs go to the host with their strength (about 10,000 health, resistances, size), not as normal ones, and appear near the heroes, not in the corner of the map. Each of the guest's transition mobs reaches the host once: previously 15 mobs turned into 128 at the host. The guest removes their own transition mobs that the game didn't remove, so no real ones stay next to the copies.
- **Shared enemies aren't recreated on every skill choice.** While the host picks a skill, shared enemies at the guest stay instead of being turned off and created again.
- **Boss gathering: the host's arena right away.** If the host's state with the boss arena reached the guest only after the move, the guest switches to the host's arena immediately. Previously for half a second the arena border stood around the guest.

**По-русски:**

- **Общие враги: особые мобы у гостя такие же, как у хоста.** Мобы перехода (после подбора сферы усиления), эпические и особые мобы хоста приходят гостю особыми копиями: с той же силой, рогатой меткой над мобом и розовой обводкой. Раньше у гостя они выглядели обычными мобами. Мобы перехода гостя уходят хосту с силой (около 10 000 здоровья, сопротивления, размер), а не как обычные, и появляются рядом с героями, а не в углу карты. Каждый моб перехода гостя попадает к хосту один раз: раньше 15 мобов превращались у хоста в 128. Свои мобы перехода, которые игра не убрала, гость убирает сам, чтобы рядом с копиями не оставались настоящие.
- **Общие враги не пересоздаются на каждом выборе умения.** Пока хост выбирает умение, общие враги у гостя остаются, а не выключаются и создаются заново.
- **Сбор на босса: арена хоста — сразу.** Если состояние хоста с ареной босса дошло до гостя уже после переноса, гость сразу переходит на арену хоста. Раньше полсекунды граница арены стояла вокруг гостя.

## Co-op 1.6.64

- **Shared enemies: the guest gathers at the host for the boss wave.** If the guest is far from the host when a boss wave starts, the mod moves the guest to the host right away. The guest's boss arena border is set the same as the host's, so the damage for leaving the arena doesn't hit them. When the host picks up the obelisk, all mana on the field is pulled to the guest, and 2 s later the guest returns to where they stood. Turned off with the «Общие враги: на босс-волну гость вдали переносится к хосту» (Shared enemies: on a boss wave a far guest is moved to the host) setting.

- **Shared enemies: a guest far from the host no longer wanders an empty map.** If the guest moved further than 15 from the host (the «Общие враги: гость дальше этого от хоста» setting, 0 turns it off), the guest's own enemies spawn again and go for them. The host doesn't see them; the guest kills them alone and gets experience for them. When the guest returns to the host, new own enemies stop spawning; those that came along stay until the guest finishes them off. The number of enemies per batch is the current one for the wave, not the one from the start of the battle. After a boss wave the guest kept its leftovers: the guest's boss doesn't die, the mod removes it on the host's obelisk. Because of them the game switched to "boss only" mode and stopped its spawning. Now outside a boss wave the mod sets these values as at the host, and far from the host restarts the stopped spawning. During a boss wave (at the guest or the host) the guest's own spawning is off even far from the host: the mod used to hide the wave's mobs as the guest's boss, and they vanished in plain sight while the wave lagged.

**По-русски:**

- **Общие враги: на босс-волну гость собирается к хосту.** Если гость на начале босс-волны далеко от хоста, мод сразу переносит его к хосту. Граница арены босса у гостя ставится такая же, как у хоста, поэтому урон за выход с арены его не бьёт. Когда хост поднимает обелиск, к гостю притягивается вся мана с поля, и через 2 с он возвращается туда, где стоял. Выключается настройкой «Общие враги: на босс-волну гость вдали переносится к хосту».

- **Общие враги: гость вдали от хоста больше не гуляет по пустой карте.** Если гость отошёл от хоста дальше 15 (настройка «Общие враги: гость дальше этого от хоста», 0 — выключить), у него снова появляются свои враги и идут на него. Хост их не видит, гость убивает их сам и получает за них опыт. Когда гость возвращается к хосту, новые свои враги перестают появляться; пришедшие с ним остаются, пока он их не добьёт. Число врагов в пачке берётся текущее, по волне, а не с начала боя. После босс-волны у гостя оставались её хвосты: босс гостя не умирает, его убирает мод по обелиску хоста. Из-за них игра включала режим «только босс» и останавливала свой спавн. Теперь вне босс-волны мод выставляет эти значения как у хоста, а вдали от хоста снова запускает остановленный спавн. Во время босс-волны (у гостя или у хоста) свой спавн гостя погашен и вдали от хоста: мобов волны мод прятал как босса гостя, и они исчезали на глазах, а волна лагала.

## Co-op 1.6.58

- **Shared pause holds however long a friend takes to choose.** Previously, if the host (or a guest) stayed in the choice window for more than a minute, the pause was lifted for the others, they played alone and got ahead on the battle clock. Now the pause holds the whole time the friend is choosing and connected. If there's no connection with the friend for 3 s (crashed, froze), the pause is lifted by itself, as before.
- **The connection doesn't hoard memory without limit.** The client's queues are bounded: outgoing events (256) and incoming messages. If the connection hangs so that events don't go out, or the game stops processing incoming messages for a long time, the client reconnects by itself instead of hoarding memory. After a long pause the game doesn't replay friends' old coordinates.
- **Server:** the frequent-join counter no longer grows from one-off connections from different addresses; the player number in a forwarded state isn't reset to 0 if the same player is reconnecting at that moment; a connection cut off mid-message doesn't pass the fragment on as a whole message.
- Standalone server `coop_server.py`: a slow player who doesn't read data is disconnected after 10 s and doesn't slow down the others.

**По-русски:**

- **Общая пауза держится, сколько бы друг ни выбирал.** Раньше, если хост (или гость) стоял в окне выбора дольше минуты, у остальных пауза снималась, они играли одни и уходили вперёд по часам боя. Теперь пауза держится всё время, пока друг выбирает и он на связи. Если от друга 3 с нет связи (вылетел, завис), пауза снимается сама, как и раньше.
- **Связь не копит память без предела.** У клиента ограничены очереди: отправка событий (256) и приём сообщений. Если связь зависла так, что события не уходят, или игра надолго перестала разбирать входящие, клиент переподключается сам, а не копит память. После долгой паузы игра не перебирает старые координаты друзей.
- **Сервер:** счётчик частых входов в комнату больше не растёт от разовых подключений с разных адресов; номер игрока в пересылаемом состоянии не сбивается в 0, если тот же игрок в этот момент переподключается; оборванное посреди сообщения соединение не передаёт обрывок как целое сообщение.
- Отдельный сервер `coop_server.py`: медленный игрок, который не читает данные, отключается через 10 с и не тормозит остальных.

## Co-op 1.6.54

- **Ally health under its model.** Each ally in battle has a health bar under it, the same as your own hero's (a copy of the game's native bar).
- **Arrow to an ally off screen.** If an ally goes off screen, a small arrow pointing towards it appears at the edge (the same one as on the location selection screen, in the player's color). Next to it is a name plate filled translucent green by the ally's health: it takes damage, the fill shrinks. Back on screen, the arrow and plate disappear. While paused (skill choice) the markers are hidden.
- The friend list on the left ("Player · health · kills") is removed: health is now visible on the ally markers.

**По-русски:**

- **Здоровье союзника под его моделью.** Под каждым союзником в бою — такая же полоска здоровья, как у своего героя (копия родной полоски игры).
- **Стрелка к союзнику за краем экрана.** Если союзник ушёл за пределы экрана, у края появляется маленькая стрелка в его сторону (та же, что на экране выбора локации, в цвет игрока). Рядом — плашка с именем, заполненная полупрозрачным зелёным по здоровью союзника: получил урон — заливка убывает. Вернулся на экран — стрелка и плашка пропадают. На паузе (выбор умения) метки скрыты.
- Список друзей слева («Игрок · здоровье · убийства») убран: здоровье теперь видно на метках союзников.

## Co-op 1.6.47

- **Less load when an obelisk is picked up.** When the host picks up an obelisk, hundreds of items are pulled to the guest's hero. Previously the mod's debug interceptors fired on each one, and on a weak phone the game froze for up to 7 s, and once crashed. The interceptors are removed, and the pulling is faster.
- **The first boss's obelisk no longer stays at the guest.** At 3:00 the game gave the guest its own obelisk, and the mod couldn't remove it from the map: the obelisk lay there until the next boss and at 6:00 was picked up along with the mana. Now the game doesn't give the guest its own obelisk from the first boss on (previously only from the second). Obelisks are also no longer pulled to the hero along with the mana.
- **The battle starts for everyone together.** Whoever entered the battle first, host or guest, waits on pause with «Ждём «Имя» — ещё не в бою…» (Waiting for "Name", not in battle yet…) until every player in the room enters. Players with no connection for 3 s aren't waited for, and the wait is at most 2 minutes. Once everyone is in, the wait is off until the end of the battle: if someone later goes back to the menu, the others don't pause.

**По-русски:**

- **Меньше нагрузки при подборе обелиска.** Когда хост поднимает обелиск, у гостя к герою притягиваются сотни предметов. Раньше на каждый срабатывали отладочные перехватчики мода, и на слабом телефоне игра подвисала до 7 с, а один раз вылетела. Перехватчики убраны, притягивание ускорено.
- **Обелиск первого босса больше не остаётся у гостя.** На 3:00 игра клала гостю его собственный обелиск, и мод не мог убрать его с карты: обелиск лежал до следующего босса, а на 6:00 подбирался вместе с маной. Теперь игра не кладёт гостю собственный обелиск уже на первом боссе (раньше — только начиная со второго). Обелиски также больше не притягиваются к герою вместе с маной.
- **Бой начинается у всех вместе.** Кто первым вошёл в бой — хост или гость, — стоит на паузе с надписью «Ждём «Имя» — ещё не в бою…», пока не войдут все игроки из комнаты. Не ждём того, от кого 3 с нет связи, и не дольше 2 минут. Когда все вошли, ожидание до конца боя выключено: если кто-то потом выйдет в меню, остальные на паузу не встанут.

## Co-op 1.6.44

- **Shared enemies: boss and obelisk follow the host for everyone.** The host picked up the obelisk: at that moment the guests' boss wave ends, their clocks align with the host, the area changes, and the guests get what a pickup would give: a legendary artifact choice and pulled-in mana. The guest has no own obelisk on the map, the boss wave skull above the clock disappears, and there's no more waiting for the end of the guest's own boss wave intro (previously up to a minute).
- The «Кооп» button moved to the top center: on the location selection screen it covered the arrow.
- **The host is always first.** «Создать игру» explicitly creates a room: the host gets slot 1, and «Игра создана» (Game created) appears only after the server responds. A guest who tapped «Подключиться» before the host waits and joins the host's room by itself.
- **More reliable reconnection.** A quick «Отключиться» (Disconnect) → «Подключиться» no longer leaves the old connection running next to the new one. After a disconnect the host returns to its room instead of creating a new one.
- **Turning the mod off mid-battle** returns the game to its normal state: lifts the shared pause and clock speed-up, restores own enemy spawning, removes copies of the host's enemies.
- **Server protection:** the number of connections and the room join rate are limited; a connection that doesn't join a room is closed after 30 s.
- The standalone server `coop_server.py` behaves the same as the embedded one (reconnection, version check, limits).
- **Less stutter with shared enemies:** game field lookup and enemy addresses are cached, enemy copies are killed without iterating over all enemies for each one. The log gets, per battle, how many ms of a frame each part of the mod takes.
- A failure in a handler (enemy spawn, projectile, frame) no longer turns off the whole mod: the log gets the first errors, then a count. Before risky game calls there are marks for the crash log (`lastrun.txt`).
- The mod won't start with incompatible ModKit 2.x libraries and says why.

**По-русски:**

- **Общие враги: босс и обелиск у всех вслед за хостом.** Хост поднял обелиск — у гостей в тот же момент кончается босс-волна, часы встают вровень с хостом, область меняется, а гости получают то же, что дал бы подбор: выбор легендарного артефакта и притянутую ману. Своего обелиска у гостя на карте нет, череп босс-волны над часами пропадает, и ждать конца своего вступления босс-волны (раньше до минуты) больше не нужно.
- Кнопка «Кооп» перенесена вверх по центру: на экране выбора локации она закрывала стрелку.
- **Хост всегда первый.** «Создать игру» явно создаёт комнату: хост получает слот 1, а «Игра создана» появляется только после ответа сервера. Гость, нажавший «Подключиться» раньше хоста, ждёт и сам входит в его комнату.
- **Переподключение надёжнее.** Быстрое «Отключиться → Подключиться» больше не оставляет старое соединение работать рядом с новым. После обрыва хост возвращается в свою комнату, а не создаёт новую.
- **Выключение мода посреди боя** возвращает игру в обычное состояние: снимает общую паузу и ускорение часов, возвращает свой спавн врагов, убирает копии врагов хоста.
- **Защита сервера:** ограничено число подключений и частота входов в комнату; соединение, которое не входит в комнату, закрывается через 30 с.
- Отдельный сервер `coop_server.py` ведёт себя так же, как встроенный (переподключение, проверка версии, ограничения).
- **Меньше подтормаживаний с общими врагами:** поиск полей игры и адресов врагов кешируется, копии врагов убиваются без перебора всех врагов на каждую. В журнал за бой — сколько мс кадра уходит на каждую часть мода.
- Сбой в обработчике (спавн врага, снаряд, кадр) больше не выключает мод целиком: в журнал — первые ошибки, дальше счёт. Перед опасными вызовами игры — метки для журнала падений (`lastrun.txt`).
- Мод не запустится с несовместимыми библиотеками ModKit 2.x и напишет причину.

Update the mod for every player: download **coop.zip** and replace the old one.

Обновите мод у всех игроков: скачайте **coop.zip** и замените старый.
