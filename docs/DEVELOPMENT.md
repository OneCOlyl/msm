# For developers

English | [Русский](DEVELOPMENT.ru.md)

How the repository is organized, how to build the mod, run the tests and publish a release. Players don't need this
document: [installation](INSTALL.md) and [connecting](CONNECT.md) are enough for them.

## Contents

- [Repository layout](#repository-layout)
- [Build and tests](#build-and-tests)
- [Testing on a phone](#testing-on-a-phone)
- [Protocol v2](#protocol-v2)
- [Publishing a new version](#publishing-a-new-version)
- [Game research](#game-research)

## Repository layout

| Path | Contents |
|---|---|
| `mod/coop/` | Mod sources. `Net/` is network code with no Unity dependencies (client, embedded server, protocol). |
| `mod/coop_lab/`, `tools/lab.py` | Test arena: an in-battle «ПОЛИГОН» button (calm arena, any mobs, battle mechanics) and co-op check scenarios on two phones. Players don't need it. |
| `tools/terra.py` | Two-player Terra autoplay: choice windows, chests, merchant, obelisks. |
| `tools/phones.py` | Hands-free run on two USB phones: install the build from `dist/`, restart the game, connect, enter a battle, run `lab.py` scenarios, collect logs. |
| `mod/coop_debug/`, `tools/coop_dbg.py` | Debug console: port 8770 on the phone, read and change game fields during a battle. Players don't need it. |
| `mod/coop_cheat/` | Cheat: an in-battle «ЧИТ» button that gives levels of spells, traits, combinations, synergies and the hero. Players don't need it. The three developer mods are described in [TOOLS.md](TOOLS.md). |
| `tools/release_notes.py` | Release description: changes from `.github/release_notes.md` since the last published release (called by the `release` workflow). |
| `server/coop_server.py` | Standalone room server (Python 3.8+), for when the server shouldn't run inside the game, e.g. in Termux or on a PC. |
| `server/coop_client.py` | Connection check without the game. |
| `tests/NetTest` | Network tests. The same checks run against the embedded server and `coop_server.py`: responses, error codes, slots, reconnection and limits must match in both. |
| `tests/server_test.py` | Internal checks of `coop_server.py`: the per-address join counter (window, expiry, address limit) and per-recipient backpressure. |
| `tests/ModSim` | Game simulation: two mod instances (host and guest) in one process. |
| `tests/DebugSim` | Debug console checks: command parsing, port closing on shutdown. |
| `tests/CheatSim` | Cheat checks against a fake game: giving and removing levels, hero level, all spells to max. |
| `stubs/` | Unity stubs for building without a phone. |
| `libs/` | `ModKit.dll` and `modkit_*.dll` (including `modkit_spells_data.dll` for the cheat) from the MS_KIT 2.0 release (MIT), for building only. Hashes are in `libs/SHA256SUMS`; CI checks them. |
| `docs/` | Documentation, game research, the checklist and phone test results. Each document exists in English (`*.md`) and Russian (`*.ru.md`); update both. |
| `.github/workflows/` | `ci.yml`: build and tests on every push; `release.yml`: publishing a Co-op release; `tools-release.yml`: a developer tools release on a `tools-*` tag. |
| `.github/release_notes.md` | Changes by version; the release description gets the changes since the last release. |

## Build and tests

Requires .NET SDK 8, Python 3 and bash (Git Bash on Windows).

```bash
bash build.sh
```

The script builds the Unity stubs, builds `coop`, `coop_debug`, `coop_cheat` and `coop_lab`, packs them into
`dist/<mod>.zip` and runs all tests. If tests fail, the script exits with an error. `SKIP_TESTS=1 bash build.sh` only
builds and packs.

The version is not stored in the repository: `mod.json` and `.csproj` have `0.0.0-dev`. `build.sh` takes it from
`VERSION` (for all four mods), else from the nearest git tag (`git describe`: `vX.Y.Z` for `coop`, `tools-X.Y.Z` for
the developer mods), else `0.0.0-dev.<number of commits>`. A build with uncommitted changes gets `-dirty.<time>`:
ModKit doesn't reread an archive with the same version. The version goes into the dll and into `mod.json` inside the
archive; the file in the repository doesn't change.
Tests run in parallel, about 80 s: ModSim takes longest (section 7 runs as a separate process, `ModSim.dll 7`, the rest
as `ModSim.dll main`). A new networked test needs its own port.

Building against the real game assemblies (the `net8/` and `il2cpp/` folders copied from the phone after Zombix's first
launch):

```bash
REF=/path/to/ref bash build.sh
```

## Static analysis

- C#: .NET analyzers (`Directory.Build.props`, level `latest-recommended`); their warnings show up in the build output.
  Disabled rules are in `.editorconfig`.
- Python: `ruff check .` (rules in `ruff.toml`).
- `build.sh`: `shellcheck build.sh`.

CI fails on ruff and shellcheck findings.

## Testing on a phone

With two phones on USB, ModKit and the MS_KIT libraries on both, and the host IP set in the guest's coop settings, one
script does everything:

```bash
python tools/phones.py run --build                  # build, install, restart, connect, battle, lab.py mobs drop, logs
python tools/phones.py run --no-deploy --lab "spells --spells 13,17"
python tools/phones.py status                       # mod versions on the phones and coop state
```

Separate steps: `deploy`, `restart`, `connect`, `battle [--area N] [--loop]`, `logs`. The result is PASS/FAIL and the
exit code; both phones' logs go to `dist/phones/<time>/`. The script:

- installs an archive only when the phone has a different version: ModKit ignores an archive with the same version (a FAIL for `coop`);
- removes `running.lock` after `am force-stop`, otherwise ModKit treats the restart as a crash and disables the mods;
- enables `coop`, `coop_debug`, `coop_cheat`, `coop_lab` in `config.json` if they are off;
- connects and enters the battle through the `coop_debug` console (`Host`, `Join`, `Button_AreaStart`), without screen taps;
- after the checks, looks for Unity exceptions in logcat and errors in `modkit.log` since the last coop start.

On Terra (`--area 14`) the battle stands still and enemies vanish until the special obelisk is picked up, so `lab.py`
scenarios don't work there. Terra has an autoplay script:

```bash
python tools/terra.py --minutes 6        # restart, a two-player Terra battle, everything up to 6:00, logs in dist/terra/<time>/
```

The script presses window buttons through `coop_lab` (`Buttons`, `Click`), with no screen taps. Spell and attribute: the
top card. Magic upgrade: the top-left magic first, then the next one to the right, row by row, in each following window.
Chest: the first artifact. Merchant: the first item if there is enough mana (the game then enables Buy), then leave. The
host steps onto obelisks (`Pickups`, `GoTo`), both players onto chests and the merchant. Nobody steers the heroes, so both
get the arena's immortality. A window the script doesn't know is printed with its buttons and a screenshot.

Manually:

Install a fresh build over USB:

```bash
adb push dist/coop.zip /sdcard/Download/
```

Then in Zombix: «Моды» (Mods) → «+» → `coop`. Mod log:

```bash
adb shell cat /sdcard/MelonLoader/com.vkslrzm.Zombix/UserData/ModKit/modkit.log | grep "\[coop\]"
```

Before testing, make sure the log has the line `кооператив <версия> включён` (co-op <version> enabled).

The debug console (with `coop_debug` enabled on the phone) is reachable over USB only:

```bash
adb forward tcp:8771 tcp:8770
python tools/coop_dbg.py 8771 "get mgr.Time_Sec"
```

### Test arena

Enable `coop_lab` in the Mods window (a new ModKit mod is disabled by default). In battle the «ПОЛИГОН» button opens
three tabs:

- Arena: game spawn, immortality, experience (no experience means no magic selection windows; the cheat's hero level
  button still works), stopping and shifting the clock, removing or killing enemies;
- Mobs: any enemy type (0–50), epic, special, transition;
- Mechanics: clock to the boss, boss wave, obelisk, area change, mana pull, item by id.

The same actions from a PC through the console: `call T:coop_lab:MagicSurvivalLab.Lab/Current.Spawn 6 10`, `.Status`,
`.CoopStatus`. Spells come from `coop_cheat`, which the arena calls in `Spell <id> <level>`.

Scenarios on two phones (both in battle, connected, with `coop`, `coop_debug`, `coop_lab` and `coop_cheat`):

```bash
python tools/lab.py mobs drop spells                 # everything; --spells 13,17 --wait 20 for selected spells
python tools/lab.py --shots shots magic              # every spell, trait and combo (~35 min)
```

`mobs` checks that the host's mobs of every kind appear at the guest, `spells` that the host's spells are visible at the
guest, `drop` reconnection after a dropped link and matching clocks, `magic` that every projectile of every host spell, trait
and combo (MagicCom) is replayed at the guest with no exceptions in the game log. Before the checks the scenario makes both heroes
immortal, turns experience off and removes spells.

Standalone server and a connection check without the game:

```bash
python server/coop_server.py --port 8766
python server/coop_client.py <IP> 8766    # then the join command
```

## Protocol v2

JSON lines (UTF-8, one line is one message, at most 16 KB) over TCP, default port 8766.

- **Client → server:** `CREATE_ROOM`, `JOIN_ROOM`, `PLAYER_STATE`, `EVENT`, `PING`, `LEAVE_ROOM`.
- **Server → client:** `WELCOME`, `ROOM_CREATED`, `ROOM_JOINED`, `PLAYER_JOINED`, `PLAYER_LEFT`, `REMOTE_PLAYER_STATE`,
  `REMOTE_EVENT`, `PONG`, `ERROR`.

The host sends `CREATE_ROOM` and after a disconnect returns to its room with `JOIN_ROOM` and the room code. `JOIN_ROOM`
with an empty code joins the room on the server; if there are no rooms, the standalone server creates one, while the
server inside the host's game answers `room_not_found` («Хост ещё не создал игру», the host hasn't created the game yet),
and the client retries. If the port is closed, the client tries ports from the given one up to +4.

The `protocol` field in `CREATE_ROOM`/`JOIN_ROOM`: no field means an old client, let it in; otherwise only the integer
`2` is accepted, anything else is `protocol_mismatch`. The server doesn't check the mod version itself.

Error codes: `room_full`, `room_not_found`, `protocol_mismatch`, `server_busy` (too many connections or rooms),
`rate_limited` (joining too often), `not_in_room`, `invalid_json`, `invalid_message`, `message_too_large`,
`unknown_type`.

Embedded server limits: 32 connections, 8 per address, 16 rooms, 60 room joins per minute per address, 30 s to join a
room after connecting, disconnect after 40 s without data.

## Publishing a new version

Changes between releases go into the `## Не выпущено` (unreleased) section at the top of `.github/release_notes.md`:
English first, then the same in Russian after a `**По-русски:**` line.

1. Rename `## Не выпущено` to `## Co-op <version>`. The release description gets only the sections that weren't in
   the last published release (`tools/release_notes.py`). The rest of the history stays in the file.
2. Build the candidate with `VERSION=<version> bash build.sh`, test it on two or more phones following the
   [checklist](RELEASE_CHECKLIST.md) and record the results in `docs/device-tests/<version>.md` (English) and
   `docs/device-tests/<version>.ru.md` (Russian).
3. Commit this to `main`, tag the commit and push the tag:

   ```bash
   git tag v1.7.9 && git push origin v1.7.9
   ```

   GitHub builds the mod with the version from the tag, runs the tests and publishes the `v<version>` release with
   `coop.zip`; the test results go into the release description. Without the results file the release isn't
   published; the Russian file is appended if present.

Developer tools (`coop_debug`, `coop_cheat`, `coop_lab`) are released separately with a `tools-<version>` tag: see
[TOOLS.md](TOOLS.md#publishing).

## Game research

Probe logs and findings about Magic Survival's internals:

- [ENEMY_SYNC.md](ENEMY_SYNC.md): enemies, spawning, Shared enemies mode.
- [ORBITALS.md](ORBITALS.md): Shield, Satellites, Spirits (spells without projectiles).
- [ARMAGEDDON.md](ARMAGEDDON.md): Armageddon.
