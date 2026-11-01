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
| `mod/coop_probe/` | Development probe: writes to `UserData/ModKit/coop_probe.txt` how the relevant parts of the game work. Players don't need it. |
| `mod/coop_debug/`, `tools/coop_dbg.py` | Debug console: port 8770 on the phone, read and change game fields during a battle. Players don't need it. |
| `tools/release_notes.py` | Release description: changes from `.github/release_notes.md` since the last published release (called by the `release` workflow). |
| `server/coop_server.py` | Standalone room server (Python 3.8+), for when the server shouldn't run inside the game, e.g. in Termux or on a PC. |
| `server/coop_client.py` | Connection check without the game. |
| `tests/NetTest` | Network tests. The same checks run against the embedded server and `coop_server.py`: responses, error codes, slots, reconnection and limits must match in both. |
| `tests/server_test.py` | Internal checks of `coop_server.py`: the per-address join counter (window, expiry, address limit) and per-recipient backpressure. |
| `tests/ModSim` | Game simulation: two mod instances (host and guest) in one process. |
| `tests/DebugSim` | Debug console checks: command parsing, port closing on shutdown. |
| `tests/ProbeSim` | Probe checks on fake game classes. |
| `stubs/` | Unity stubs for building without a phone. |
| `libs/` | `ModKit.dll` and `modkit_*.dll` from the MS_KIT 2.0 release (MIT), for building only. Hashes are in `libs/SHA256SUMS`; CI checks them. |
| `docs/` | Documentation, game research, the checklist and phone test results. Each document exists in English (`*.md`) and Russian (`*.ru.md`); update both. |
| `.github/workflows/` | `ci.yml`: build and tests on every push; `release.yml`: publishing a release. |
| `.github/release_notes.md` | Changes by version; the release description gets the changes since the last release. |

## Build and tests

Requires .NET SDK 8, Python 3 and bash (Git Bash on Windows).

```bash
bash build.sh
```

The script builds the Unity stubs, checks that versions match, builds `coop`, `coop_probe` and `coop_debug`, packs them
into `dist/coop.zip`, `dist/coop_probe.zip`, `dist/coop_debug.zip` and runs all tests. If tests fail or versions don't
match, the script exits with an error. `SKIP_TESTS=1 bash build.sh` only builds and packs.

Building against the real game assemblies (the `net8/` and `il2cpp/` folders copied from the phone after Zombix's first
launch):

```bash
REF=/path/to/ref bash build.sh
```

## Testing on a phone

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

1. Bump the version in three places: `version` in `mod/coop/mod.json`, `ModVersion` in `mod/coop/CoopMod.cs` and
   `<Version>` in `mod/coop/coop.csproj` (`build.sh` checks that they match).
2. Add the changes to the top of `.github/release_notes.md` as a `## Co-op <version>` section: English first, then the
   same in Russian after a `**По-русски:**` line. The release description
   gets only the sections that weren't in the last published release (`tools/release_notes.py`). The rest of the history
   stays in the file.
3. Test the build on two or more phones following the [checklist](RELEASE_CHECKLIST.md) and record the results in
   `docs/device-tests/<version>.md` (English) and `docs/device-tests/<version>.ru.md` (Russian).
4. On GitHub open Actions → release → Run workflow. GitHub builds the mod, runs the tests and publishes the
   `v<version>` release with `coop.zip`; the test results go into the release description. Without the results file
   the release isn't published; the Russian file is appended if present.

## Game research

Probe logs and findings about Magic Survival's internals:

- [ENEMY_SYNC.md](ENEMY_SYNC.md): enemies, spawning, Shared enemies mode.
- [ORBITALS.md](ORBITALS.md): Shield, Satellites, Spirits (spells without projectiles).
- [ARMAGEDDON.md](ARMAGEDDON.md): Armageddon.
