---
name: device-test
description: Проверка изменений «Кооператива» — без телефонов только build.sh, с двумя телефонами по USB сценарии tools/lab.py и tools/phones.py, для производительности замер perf до и после. Использовать перед PR с изменениями в mod/, когда нужно проверить мод в игре или замерить кадр и сеть.
---

# Проверка на телефонах

## Без телефонов (облачная сессия, нет adb)

Только сборка и тесты на ПК:

```bash
bash build.sh
python3 tools/check_comments.py
ruff check .
```

Не пишите в PR, что мод проверен в игре. `python tools/lab.py …` без телефонов завершится с кодом 1 и строкой
`ОШИБКА …`: это не провал изменения.

## С двумя телефонами по USB

Телефонами пользуются люди. Перед долгой проверкой спросите пользователя, можно ли их занять.

На обоих телефонах:

- включена отладка по USB, `adb devices` показывает оба (хост и гость: серийники в `tools/lab.py`, `HOST_SERIAL`,
  `GUEST_SERIAL`, или `--host`/`--guest`);
- в ModKit включены `coop`, `coop_debug`, `coop_lab` и `coop_cheat` (для `magic` и `perf`);
- в настройках `coop` у гостя указан IP хоста; для общих врагов `world=1`.

adb ищется в переменной `ADB`, затем в `PATH`, затем в `%LOCALAPPDATA%\Android\Sdk\platform-tools`. В Git Bash перед
путями `/sdcard/...` ставьте `MSYS_NO_PATHCONV=1`.

Всё за один раз: сборка, установка из `dist/`, перезапуск игры, подключение, вход в бой, сценарии, журналы:

```bash
python tools/phones.py run --build --lab "mobs drop magic"
```

После перезапуска игра может показать «Load» (продолжить прошлый бой) или «Restore Gold»: `phones.py` их не закрывает,
и шаг `battle` ждёт впустую. Смотрите экран (`adb exec-out screencap -p`), нажимайте Cancel или Confirm, дальше шагами:
`phones.py connect`, `phones.py battle --area 1`. На Terra и поздних локациях бой для замеров не годится: Terra стоит до
особого обелиска, на поздних герой гибнет раньше, чем `lab.py` включит бессмертие.

Если игра уже в бою и телефоны на связи, сценарии можно запустить отдельно:

```bash
python tools/lab.py --json r.json mobs chests drop magic perf
```

Сборку с номером будущего релиза (`VERSION=X.Y.Z`) на телефоны не ставьте: ModKit не перечитывает архив той же
версии, и потом не встанет сам релиз.

Журнал мода: `/sdcard/MelonLoader/com.vkslrzm.Zombix/UserData/ModKit/modkit.log`. `phones.py run` и `phones.py logs`
кладут журналы обоих в `dist/phones/<время>/`. Сначала проверьте строку `кооператив <версия> включён`: версия должна
совпадать с `mod.json` в `dist/coop.zip`.

Провал:

- код выхода `lab.py` или `phones.py` не 0;
- строки `FAIL` или `ОШИБКА` в выводе, `"passed": false` в `--json`;
- исключения Unity в logcat за время проверки (`phones.py run` проверяет сам);
- в `modkit.log` не та версия, или игра упала.

`НЕТ` и `ИНФО` — не провал мода: так отмечено то, что игра не дала сделать (например, не создала моба).

## Изменения производительности

Замер `perf`: спокойная арена, 200 врагов, все магии на максимум у обоих, 60 с.

1. До изменения: `python tools/lab.py --json perf-before.json perf`.
2. После, на тех же телефонах, той же локации и с теми же модами: `python tools/lab.py --json perf-after.json perf`.
3. Обе таблицы из вывода (`| | frames | fps | frameAvg | …`) — в описание PR.

`frameAvg` и `frameMax` — время мода за кадр в мс, `slowFrames` — кадры мода дольше 8 мс, `netTx`/`netRx` — байт в
секунду, `netTxMax`/`netRxMax` — самая длинная строка в байтах (предел 16 384), `pingAvg`/`pingMax`/`pingSd` — пинг в мс.
