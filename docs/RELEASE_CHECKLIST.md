# Release testing on phones

English | [Русский](RELEASE_CHECKLIST.ru.md)

Autotests (`bash build.sh`, CI) check the network, both servers and the mod logic on a PC, without Unity and without
Android. They don't replace testing on real phones: screen layout, working over a VPN, minimizing the game, Android's
behavior when the network changes.

**Rule:** a release is published only with test results from two or more phones. The results are the file
`docs/device-tests/<version>.md` in English (template below) and the same in Russian, `docs/device-tests/<version>.ru.md`.
Without the English file the `release` workflow stops; both files go into the release description.

Build the candidate with the release version: `VERSION=<version> bash build.sh`. Before testing, make sure every phone
runs exactly this build: `modkit.log` must have the line `кооператив <версия> включён` (co-op <version> enabled).

## Required scenarios

Mark each one: ✅ works, ❌ doesn't work (what exactly), — not tested (why).

### Connection

| # | Scenario | Expected |
|---|---|---|
| 1 | 2 players, Anywherelan | Host is slot 1, guest is slot 2; both show «на связи, пинг … мс» (connected, ping … ms) |
| 2 | 3 players | Slots 1–3, everyone has two allies |
| 3 | 4 players, then a fifth | The fifth gets «Комната заполнена» (Room is full) |
| 4 | Another VPN (Tailscale or ZeroTier) | Same as #1 |
| 5 | Guest taps «Подключиться» (Connect) before the host taps «Создать игру» (Create game) | Guest waits and joins the host by itself, slot 2 |
| 6 | Port 8766 is taken on the host's phone | Game is created on a neighboring port, the guest finds it by itself |

### Disconnect and recovery

| # | Scenario | Expected |
|---|---|---|
| 7 | Turn off the guest's Wi-Fi/mobile data for 10–30 s mid-battle and turn it back on | «Связь потеряна, переподключаюсь…» (Connection lost, reconnecting…), then connected again, same slot, no duplicate at the host |
| 8 | Same at the host | Host returns to its room (same code), guests stay in it |
| 9 | Switching Wi-Fi ↔ mobile data | Reconnects without player action |
| 10 | Guest minimizes the game for 30 s and comes back | Reconnects; at the host the guest's ally disappears and appears again |

### Turning the mod off and on

| # | Scenario | Expected |
|---|---|---|
| 11 | Turn the mod off at the guest during a shared pause (host is picking a skill) | The guest's game runs at normal speed |
| 12 | Turn the mod off at the guest with Shared enemies on | Copies of the host's enemies disappear, own enemies spawn again |
| 13 | Turn the mod back on and connect | A new game works as usual |
| 14 | Debug (`coop_debug`) is off | Port 8770 is closed: `adb forward tcp:8771 tcp:8770` + `python tools/coop_dbg.py 8771 "get mgr"` doesn't connect |

### Game

| # | Scenario | Expected |
|---|---|---|
| 15 | Shared pause | While anyone picks a skill, everyone is paused; after the choice, everyone continues |
| 15a | Battle entry: host entered, guest still in the menu (and vice versa) | The one who entered is paused with «Ждём «Имя» — ещё не в бою…» (Waiting for "Name", not in battle yet…); guest entered: everyone runs, clocks almost equal; guest then went back to the menu: the host doesn't pause |
| 15b | Clocks drifted (entered the battle 5+ s later, one player's game runs slow) | The player ahead has no pause; the lagging player's log has `часы: отстаю от «Имя» на N с — ускоряю до ×…` (clock: N s behind "Name", speeding up to ×…), then `часы: догнал «Имя»` (clock: caught up with "Name") |
| 15c | Lobby: the host changes the area, the guest waits on the area screen; then the host enters through «Enter Area» and through «Infinite Mode» | The guest sees the host's area, no arrows or enter buttons, the label «Локацию выбирает …» (… picks the area); the host sees «Готовы к бою: 1 из 1» (Ready: 1 of 1); the guest enters the same battle by itself, the guest's log has `лобби: хост начал бой — вхожу через <button>` (lobby: host started, entering through <button>) |
| 16 | Shared enemies (experimental) | The guest has the host's enemies, the guest's damage reaches the host, a kill at the host removes the copy |
| 16a | Shared enemies: guest further than 15 from the host, before and after the first boss wave | Own enemies spawn around the guest and go for them; back near the host, no new ones spawn |
| 16b | Shared enemies: boss wave, guest far from the host (boss gathering off) | The guest has no own enemies, mobs don't vanish in plain sight, the wave doesn't lag; after the wave own enemies come again |
| 16c | Shared enemies: boss gathering | At wave start a far guest is at the host right away, the arena border is the same as the host's, no damage beyond its edge; after the host's obelisk the mana reaches the guest, and 2 s later the guest is back in place |
| 16d | Shared enemies: Terra, special obelisk at battle start | The guest has no obelisk (`особый обелиск: у гостя убран`, special obelisk: removed at the guest), the host has it; the host picks it up: the guest gets the «Obelisk» artifact choice window, mana isn't pulled in |
| 16e | Shared enemies: artifact merchant after a boss | The merchant appears at the guest together with the host, after the second boss too; the guest's log after the host's obelisk pickup has `Battle_ClearON 0 → 1` |
| 16f | Shared enemies: spawning of all mob kinds (Terra, up to 5:00) | The guest sees the same mobs of every kind (normal, special, epic, transition) as the host; at 5:00 boss copies exist, no `тип … не создался` (type … not created) in the log |
| 16g | Shared enemies: an enemy vanishes at the host without dying (pushed into the walls around the host's hero) | The guest's copy disappears without a kill, mana or drops; the host's battle summary counts it in `пропало без смерти` (gone without dying) |
| 16h | Shared enemies: boss wave, then the merchant | The guest's clock stays no more than 1 s ahead of the host's (`часы: впереди «Имя»`, clock: ahead of "Name", in the guest's log); after an artifact from the merchant the mana is pulled in at the guest too (`мана: хост притянул свою`, mana: the host pulled its own) |
| 16i | Battle rules from the host: the guest sets other values for shared enemies, pause, heal, friend's magic damage, enemy health and damage | The host's values apply at the guest; the guest's «Кооп» window shows «Правила боя от хоста: …» (Battle rules from the host) |
| 16j | «ХП врагов, ×» and «Урон врагов, ×» at 2 at the host, then back to 1 | Enemies at the host have twice the health (living ones recalculated at once) and hit both heroes twice as hard; at 1 everything is as before |
| 17 | Ally health | Under the ally, a health bar like your own hero's; an ally off screen has an arrow in its color pointing at it and a name plate whose green fill shrinks with damage; «погиб» (died) when the friend dies |
| 18 | Heal | «Лечить» (Heal) button when a friend has less than 70% health, heals 25%, 40 s cooldown |
| 19 | Friend's magic | Projectiles, Shield, Satellites, Spirits, Electric Zone, Magic Circle and Armageddon of the friend are visible at their ally |
| 20 | Different screens | The «Кооп», «Лечить» buttons, ally arrows and plates don't cover the game UI on at least two different resolutions |
| 21 | «Ваше имя» (Your name) field in the «Кооп» window, changed while connected | Friends see the new name right away in their labels and in the «Кооп» window |

## Logs: what to collect

Draw conclusions from logs, not just by eye. The ModKit folder on the phone: `/sdcard/MelonLoader/com.vkslrzm.Zombix/UserData/ModKit/`.

| File | Contents |
|---|---|
| `modkit.log` | ModKit and mod log: `[coop]` lines, errors `ОШИБКА [coop] <где> (n/5)`, `медленный хук` (slow hook), `мод выключен` (mod disabled), `отключён из-за ошибок` (disabled due to errors) |
| `lastrun.txt` | Where the mod's code was when the game crashed: the `inside`/`after` line and the ring of recent marks (`ring`), set by `ctx.Mark` |
| `store/coop.json` | Saved mod state: player name and `clientId` |
| `config.json` | Which mods are on and their settings |
| `../../MelonLoader/Latest.log` | MelonLoader log: native crashes and load errors that aren't in `modkit.log` |

From a PC (Git Bash; `MSYS_NO_PATHCONV=1`, otherwise `/sdcard` turns into a Windows path):

```bash
D=/sdcard/MelonLoader/com.vkslrzm.Zombix
for f in UserData/ModKit/modkit.log UserData/ModKit/lastrun.txt UserData/ModKit/store/coop.json UserData/ModKit/config.json MelonLoader/Latest.log; do
  MSYS_NO_PATHCONV=1 adb -s <serial> pull "$D/$f" "$(basename "$f")"
done
```

Check the version first: `modkit.log` must have the line `кооператив <версия> включён`.

**When to collect and what to look for:**

| Case | What the logs should have |
|---|---|
| The game crashed | `lastrun.txt`: the last `coop` mark shows where it crashed (`союзник: создание`, `общие враги: создание копии врага (тип N)`, `общие враги: здоровье копии`, `общие враги: убираю своих врагов…`, `Армагеддон друга: …`, `возврат игры: …`). `Latest.log` has the native error |
| The mod was turned off (manually or by the core) | `мод выключен: coop`; when turned off during shared enemies, `общие враги: выключено (мод выключен)`. If `отключён из-за ошибок`: 5 errors within 10 s, the cause is in the `ОШИБКА [coop]` lines before it |
| Connection lost | `клиент: соединение (IP:порт): …` with the reason; after recovery, `в комнате <код>: слот N` with the same code and slot |
| End of battle | `кадр за бой (N кадров), среднее мс: …; максимум мс: …` (frame per battle: average/max ms) shows which part of the frame is expensive; `медленный кадр … мс: …` (slow frame) shows the first 5 frames over 8 ms with a breakdown and enemy count; `ошибки обработчиков за бой: …` (handler errors per battle) if hook handlers failed |

Load: in the battle summary, `кадр за бой` and the `медленный кадр` lines show how many ms go to network and pause,
sending state (with enemies at the host), Armageddon, shared enemies, clocks and allies. For a release with shared
enemies, attach these lines from a battle with 100+ enemies at both host and guest.

## Template for `docs/device-tests/<version>.md`

```markdown
# Testing <version> on phones

Date: YYYY-MM-DD. Tested by: <who>.

| Phone | Android | Resolution | Role |
|---|---|---|---|
| <model> | <version> | <width×height> | host |
| <model> | <version> | <width×height> | guest |

VPN: <Anywherelan / Tailscale / ZeroTier>.

| # | Result | Notes |
|---|---|---|
| 1 | ✅ | |
| … | | |

## Logs

- Load (battle summary, host / guest): `кадр за бой …`
- Errors `ОШИБКА [coop]` and `ошибки обработчиков за бой`: <none / which>

## Known limitations

- <what doesn't work or wasn't tested, and why>
```
