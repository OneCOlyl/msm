# Magic Survival — Co-op

English | [Русский](README.ru.md)

A mod for [MS_KIT](https://github.com/Kysnez4/MS_KIT): Magic Survival for 2–4 players over the internet, including from
different cities and countries. You don't need your own server (VPS): it is built into the mod and one of the players
(the host) starts it with a button. Phones connect through a free virtual network: Anywherelan, Tailscale or ZeroTier.

> **Players need only the [`coop.zip`](https://github.com/OneCOlyl/msm/releases/latest/download/coop.zip) file** from
> the [latest release](https://github.com/OneCOlyl/msm/releases/latest) plus the MS_KIT components (see [installation](docs/INSTALL.md)).
> Everything else in the repository is source code for those who want to change or rebuild the mod.

The mod's in-game interface is in Russian. The docs quote its labels and messages as they appear on screen, with a
translation next to them.

## Contents

- **For players**
  - [What the mod does](#what-the-mod-does)
  - [What you need](#what-you-need)
  - [Quick start](#quick-start)
  - [Good to know](#good-to-know)
- **For developers**
  - [Build and project layout](#for-developers)
- [Project status](#project-status)
- [Documentation](#documentation)
- [Feedback](#feedback)

## For players

### What the mod does

- Friends **appear in your battle as allies**: colored copies of the hero. An ally moves the same way the friend moves
  in their own game and **repeats their magic**: projectiles, Shield, Satellites, Spirits, Electric Zone, Magic Circle
  and Armageddon.
- **Shared pause:** while someone picks a skill, the game is paused for everyone, and everyone starts the battle together.
- An ally has a **health bar** under it, and when it goes off screen, an **arrow points towards it**.
- A **«Лечить»** (Heal) button when a friend is in trouble, and a **«нужна помощь!»** (need help!) signal when you are.
- **Shared enemies** (experimental, on by default): every player fights the host's enemies.

Details are in the [feature description](docs/FEATURES.md).

### What you need

- An Android phone with an **arm64** processor for each player.
- **Zombix on MelonLoader** with the ModKit core and libraries from [MS_KIT](https://github.com/Kysnez4/MS_KIT/releases).
  It is a Magic Survival clone with mod support; the original game stays installed, and the clone has its own saves.
- The [`coop.zip`](https://github.com/OneCOlyl/msm/releases/latest/download/coop.zip) mod, **the same version for all players**.
- The same virtual network app for everyone:
  [Anywherelan](https://f-droid.org/packages/com.anywherelan.awl/),
  [Tailscale](https://tailscale.com/download/android) or
  [ZeroTier](https://www.zerotier.com/download/).

### Quick start

1. **Install** Zombix, ModKit and the mod on every phone: [step-by-step guide](docs/INSTALL.md).
2. **Join one virtual network**: [Anywherelan, Tailscale or ZeroTier](docs/CONNECT.md#virtual-network).
3. **The host creates a game:** main menu → the «Кооп» (Co-op) button at the top center → «Создать игру (я хост)»
   (Create game, I'm the host). The address to give your friends is marked «← VPN».
4. **The others join:** «Кооп» → the four numbers of the host's IP, port `8766` → «Подключиться» (Connect). The status
   shows «на связи, пинг … мс» (connected, ping … ms).
5. **Start a battle**, each player in their own game. Friends appear in your battle as allies.

Can't connect? See the [connection check](docs/CONNECT.md#connection-check) and the
[message table](docs/CONNECT.md#if-you-cant-connect).

### Good to know

- **Everyone has their own world.** Your own enemies, level, spells and artifacts; a friend comes to your battle as an
  ally and helps you fight. The game doesn't allow a fully shared battle, except for the experimental «Общие враги»
  (Shared enemies) mode.
- **The host keeps the game open.** The server runs inside the host's game, and Android suspends a minimized game.
- **The connection is not encrypted and has no password.** Play only inside the virtual network and don't expose port
  8766 to the internet.
- All limitations and what has been tested are in [Known limitations](docs/FEATURES.md#known-limitations).

## For developers

Build, packaging into `dist/coop.zip` and all tests (requires .NET SDK 8 and Python 3):

```bash
bash build.sh
```

Main paths:

| Path | Contents |
|---|---|
| `mod/coop/` | Mod sources; `Net/` is network code with no Unity dependencies. |
| `server/` | Standalone room server `coop_server.py` and a connection check without the game, `coop_client.py`. |
| `tests/` | Network tests (against both servers), a game simulation with two mod instances, debug mod checks. |
| `libs/`, `stubs/` | MS_KIT libraries and Unity stubs for building without a phone. |

Repository layout, protocol v2, testing on a phone and publishing a release: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Project status

The mod is built against `ModKit.dll` and the libraries from MS_KIT 2.0.

Tested on two phones (host and guest) over Anywherelan. Not yet tested on phones: Tailscale and ZeroTier, 3–4 players
(PC autotests only), other screen resolutions. Every release goes through the
[checklist](docs/RELEASE_CHECKLIST.md); results are in [docs/device-tests/](docs/device-tests/).

## Documentation

| Document | Topic |
|---|---|
| [docs/INSTALL.md](docs/INSTALL.md) | Installing Zombix, ModKit and the mod on Android, updating |
| [docs/CONNECT.md](docs/CONNECT.md) | Virtual network, creating a game, joining, connection check, errors |
| [docs/FEATURES.md](docs/FEATURES.md) | Features, sync, settings, version compatibility, limitations, security |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | Repository layout, build, tests, protocol, publishing a release |
| [docs/TOOLS.md](docs/TOOLS.md) | Developer tools: debug console, cheat and test arena (`tools-*` releases) |
| [docs/RELEASE_CHECKLIST.md](docs/RELEASE_CHECKLIST.md) | Release checklist for testing on phones |
| [docs/ENEMY_SYNC.md](docs/ENEMY_SYNC.md), [docs/ORBITALS.md](docs/ORBITALS.md), [docs/ARMAGEDDON.md](docs/ARMAGEDDON.md) | Research into the game's internals |
| [.github/release_notes.md](.github/release_notes.md) | Changes by version |

## Feedback

Bugs, questions and screenshots go to [issues](https://github.com/OneCOlyl/msm/issues). Attach the last lines of the log
`/sdcard/MelonLoader/com.vkslrzm.Zombix/UserData/ModKit/modkit.log` (lines with `[coop]`), the mod version and the VPN
you played over.
