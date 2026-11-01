# Features, settings and limitations

English | [Русский](FEATURES.ru.md)

This description matches mod version **1.7.0**. Changes in each version are in the [release notes](../.github/release_notes.md)
and on the [releases page](https://github.com/OneCOlyl/msm/releases).

## Contents

- [Key point: everyone has their own world](#key-point-everyone-has-their-own-world)
- [Features](#features)
- [What is synced](#what-is-synced)
- [Settings](#settings)
- [Version compatibility](#version-compatibility)
- [Known limitations](#known-limitations)
- [Security](#security)
- [What has been tested](#what-has-been-tested)

## Key point: everyone has their own world

Each player plays in **their own** battle: their own enemies, level, spells, artifacts and experience. Enemies, damage
and leveling are computed inside the game's closed code, so the mod doesn't make a fully shared battle. A friend comes
to your battle as an **ally**: a colored copy of the hero that moves and casts the same way the friend does in their
own game.

The exception is the experimental [Shared enemies](#shared-enemies-experimental) mode: in it every player fights the
host's enemies.

## Features

### Lobby

The host picks the area, difficulty and mode (normal or Infinite Mode). Each player picks their own character.

- **Guest's area screen.** It shows the host's area; the area arrows are hidden. If the guest taps another difficulty,
  the mod switches it back to the host's. A label at the top reads «Локацию выбирает <host name>, ждём старта».
- **Start.** Until the host starts the battle, the guest's enter buttons are hidden. A guest waiting on the area screen
  enters the battle automatically once the host starts, through the same button the host pressed. A guest who reaches
  the screen later sees the enter buttons and the label «<host name> в бою — входите».
- **Host's area screen.** The label «Готовы к бою: N из M» shows how many guests are waiting on the area screen. The
  host also gets a pop-up when a guest is ready.
- **Locked area.** A guest enters the host's area even if they haven't unlocked it: the game checks unlocking only with
  the arrows on the selection screen.

### Allies

- Friends who are in a battle right now appear in your battle as allies. An ally follows the friend's path; movement is
  smooth because the mod fills in the path between updates.
- An ally's color depends on the player number: 1 gold, 2 light blue, 3 green, 4 pink.
- An ally **repeats the friend's magic**: every projectile of the friend's hero (Fireball, Meteor, Lightning and most
  other spells) is fired from the ally in your game. Damage of repeated spells is computed from **your** hero's stats
  and the «Урон магии друга у вас» (Friend's magic damage in your game) setting.
- The friend's **Shield, Satellites, Spirits, Electric Zone and Magic Circle** are shown around their ally.
- The friend's **Armageddon** also goes off in your game, with animation and damage.
- A friend who dies disappears from your battle. A friend with no data for 3 seconds also disappears and comes back when
  the connection recovers.

### Health and help

- Each ally has a **health bar** under it, the same as your hero's.
- When an ally goes off screen, an **arrow pointing towards it** appears at the screen edge, with a name plate filled
  green by its health. Markers are hidden while paused.
- When a friend has less than 70% health, a **«Лечить»** (Heal) button appears. It restores 25% of their max health
  (the healer's «Лечение друга, %» setting), with a 40-second cooldown. If several friends are wounded, the one with the
  lowest health share is healed.
- When you have less than 25% health, friends get a **«нужна помощь!»** (need help!) signal, at most once every 20 seconds.
- On-screen labels: «присоединился» (joined), «начал бой — заходите!» (started a battle, come in!), «погиб!» (died!),
  «вышел» (left).

### Shared pause

- While someone picks a skill or is in the pause menu, the game is paused for everyone, and battle clocks don't drift
  apart. The pause holds however long the friend takes to choose, as long as they're connected. If there is no data from
  the friend for 3 seconds, the pause is lifted.
- **Shared battle start:** whoever enters the battle first waits on pause with the label «Ждём «Имя» — ещё не в бою…»
  (Waiting for "Name", not in battle yet…) until every player in the room enters, but no longer than 2 minutes. Once
  everyone is in, this wait is off until the end of the battle.
- **Battle clocks catch up without pauses:** if clocks drift apart (someone entered the battle later or their game runs
  slow), the lagging player's game speeds up: ×1.25 when 2+ seconds behind, ×1.5 from 4, ×2 from 8, until it catches
  up. The player ahead doesn't wait. In the 30 seconds before a boss wave the speed-up starts at 1 second and is one step
  stronger, so the wave starts for everyone at almost the same time. The game slows time down by itself when the phone
  can't render frames fast enough, which is why clocks on a weak phone fall behind; the mod lifts this slowdown while
  catching up.
- Works for players who have the «Общая пауза» (Shared pause) setting on: turn it off and your battle won't pause
  because of friends.

### Shared enemies (experimental)

The «Общие враги» (Shared enemies) setting, on by default. When updating from 1.6 the mod turns it on once; after
that the player's choice is kept. **It must be on for every player.**

- Guests don't get their own enemies: instead they have copies of the host's enemies in the same places. An enemy killed
  at the host is killed at the guest too, with experience. A guest's damage to enemies counts at the host.
- A guest who moves further from the host than the «Общие враги: гость дальше этого от хоста» (Shared enemies: guest
  further than this from the host) setting (15 by default) gets their own enemies: the guest's game spawns them again,
  and they go for the guest. The host doesn't see these enemies; the guest finishes them off alone. When the guest comes
  back closer (2 units inside the border, so it doesn't flip back and forth), new own enemies stop spawning. Those that
  followed the guest from afar stay until the guest kills them. During a boss wave the guest has no own enemies even far
  from the host.
- A guest who is far from the host when a boss wave starts is moved to the host right away, and the guest's boss arena
  is set the same as the host's. When the host picks up the obelisk, all mana on the field is pulled to the guest, and
  2 s later the guest returns to where they stood. Turned off with the «на босс-волну гость вдали переносится к хосту»
  (on a boss wave a far guest is moved to the host) setting.
- In this mode an ally stands where the friend really is, without the «Дальность призрака друга» (Friend ghost range)
  limit.
- The boss and obelisk follow the host: when the host picks up the obelisk, the guests' boss wave ends, their clocks align
  with the host, and the guest gets what a pickup would give: a legendary artifact choice and pulled-in mana. After the
  boss the guest's battle progresses like the host's: enemies get stronger, the artifact merchant appears.
- The special obelisk at the start of a battle on Terra exists only at the host. When the host picks it up, the guest
  gets what a pickup would give: an artifact choice and an area change.
- The host's bosses (for example, the three big bosses on Terra at 5:00) appear at the guest too.
- The mode's limitations are in [Known limitations](#known-limitations) and in [docs/ENEMY_SYNC.md](ENEMY_SYNC.md).

## What is synced

| What | How it's sent |
|---|---|
| Hero position | Offset from the battle start point, 15 times per second (the «Обновлений позиции в секунду» setting), with a timestamp for smooth movement |
| Health, death, kills | In every state update |
| Battle entry, pause, skill choice window | In every state update; shared pause and shared battle start are built on this |
| Battle clock | In every state update during a battle |
| Spells | Fired projectiles, in batches of events; the friend's game repeats each one from the ally |
| Shield, Satellites, Spirits, Electric Zone, Magic Circle | In the state update during a battle |
| Armageddon | A separate event |
| Heal and "need help" | Separate events |
| Host's enemies (Shared enemies mode) | Positions and health of the host's enemies in the host's updates; the guest's damage as an event to the host |

**Not synced:** hero level and chosen skills, spell levels, artifacts, items and gold, experience (except kills in
Shared enemies mode), saves and progress between battles, hero and location choice. Each player picks a hero and a
location in their own game.

## Settings

Settings are in the «Моды» (Mods) window → the «Кооператив» card. They apply to your phone.

| Setting | Default | Range | What it changes |
|---|---|---|---|
| «Дальность призрака друга» (Friend ghost range) | 6 | 2–12 | How far from you an ally can go. |
| «Лечение друга, %» (Friend heal, %) | 25 | 0–50 | How much the «Лечить» button restores. 0 removes the button. |
| «Перезарядка лечения, с» (Heal cooldown, s) | 40 | 10–120 | How often you can heal. |
| «Урон магии друга у вас, %» (Friend's magic damage in your game, %) | 100 | 0–200 | Strength of the friend's repeated spells in your battle. 0 means effect only, no damage. |
| «Показывать Щит, Спутников и Духов друга» (Show friend's Shield, Satellites and Spirits) | 1 | 0–1 | 1 shows them around the ally, 0 doesn't. |
| «Общая пауза» (Shared pause) | 1 | 0–1 | 1: your battle pauses while a friend picks a skill and waits for friends at the start; 0: you have your own pause. |
| «Общие враги» (Shared enemies) | 1 | 0–1 | 1: every player fights the host's enemies (experimental). Turn it on for every player. |
| «Общие враги: гость дальше этого от хоста — у него свои враги» (Shared enemies: guest further than this from the host gets own enemies) | 15 | 0–40 | Applies at the guest. Beyond this distance from the host the guest's own enemies spawn and go for the guest; 0 means never. |
| «Общие враги: на босс-волну гость вдали переносится к хосту» (Shared enemies: on a boss wave a far guest is moved to the host) | 1 | 0–1 | Applies at the guest. 1: moved to the host for the boss wave, after the wave mana is pulled in and the guest goes back; 0: stays put. |
| «Обновлений позиции в секунду» (Position updates per second) | 15 | 5–30 | How often your phone tells friends where you are. Higher means allies repeat movement more precisely, but more traffic (about 3 KB/s at 15). 20–30 on a good connection, 10 on a weak one. |

The host's IP address and port are set in the «Кооп» window and saved between launches.

## Version compatibility

- **Game:** only Zombix on MelonLoader (`com.vkslrzm.Zombix`), arm64. The original Magic Survival and the Frida version
  of Zombix aren't supported.
- **ModKit:** the mod is built against `ModKit.dll` and the libraries from MS_KIT 2.0. It needs the `modkit_combat`,
  `modkit_hero`, `modkit_spells`, `modkit_companions` libraries, version 1.0 or later and below 2.0. With 2.x libraries
  the mod won't start and says why.
- **Between players:** the mod checks only the network protocol version (currently 2; unchanged since version 1.0). If
  the protocol doesn't match, the server answers «Версия «Кооператива» не совпадает с хостом» (Co-op version doesn't
  match the host). Different mod versions with the same protocol connect, but features missing at one of the players
  won't work:

  | Feature | Version needed by both |
  |---|---|
  | Repeating all of the friend's magic (in older versions the ally fires only the friend's single best spell) | 1.3 |
  | Friend's Shield, Satellites, Spirits, Electric Zone | 1.4.6 |
  | Friend's Armageddon | 1.4.8 |
  | Shared enemies | 1.5.0 |
  | Shared pause | 1.6.0 |

  So install the same, latest version for every player.

## Known limitations

- **No host, no game.** The server runs inside the host's game: if the host closes or minimizes it, everyone else loses
  the connection. The mod reconnects by itself when the host comes back.
- **Minimized game.** Android suspends minimized apps. After 40 seconds without data the server disconnects the player;
  when they return to the game, the mod puts them back in the room with the same number.
- **Shared pause has no time limit.** If a friend opened the pause menu and walked away from the phone, the others wait
  until they come back or leave the battle (or turn off «Общая пауза» in your own game).
- **Player name** («Игрок» and a random number) is picked by the mod the first time it's turned on; you can't change it
  in the interface.
- **One game per host**, up to 4 players. Connection is by IP only; there's no game discovery on the network.
- **Shared enemies** is still in progress: the guest's own boss is hidden until the host picks up the obelisk; enemy
  projectiles and some special enemies aren't fully transferred; the first frames of a battle may stutter at the guest
  while enemy copies are created.
- **Screens.** Button and marker placement is tested on two resolutions (1440×3200 and 1080×2220); others may need
  adjustment.
- **Mono runtime crash** (`mono-threads-posix.c`) happened at the guest on 1.6.46 and 1.6.56. Message receiving was
  reworked in 1.6.57, and the crash hasn't recurred since, but it's not considered fixed: there have been few checks so far.
- **Lobby and locked areas.** Whether the game credits a guest with progress (journal, best score) in an area they
  haven't unlocked is untested. Terra difficulty and paid area entry at the guest are untested too.

## Security

The connection is **not encrypted and has no password**. Play only inside the virtual network (Anywherelan, Tailscale,
ZeroTier) and don't open port 8766 to the internet on your router. The virtual networks themselves encrypt traffic
between devices.

The server limits the number of connections (32 total, 8 per address), rooms (16), room join rate (60 per minute per
address) and message length (16 KB), and closes a connection that hasn't joined a room within 30 seconds. This protects
against accidental overload, not against an attacker: a public server on the internet would need password login and
encryption.

## What has been tested

**On two phones** (Android 16 and Android 12, Anywherelan, host and guest), 22.10–01.11.2026: joining by IP, the ally
and its smooth movement, repeating the friend's magic, Shield, Satellites, Spirits and Electric Zone, the friend's
Armageddon, shared pause (including a long skill choice), shared battle start, clock catch-up by speeding up, shared
enemies in a battle up to 7:00 with two boss waves, special mobs, the Terra special obelisk, the artifact merchant after
a boss, 5:00 bosses at the guest, network loss at the guest, VPN switching from Wi-Fi to mobile data and back, the guest
minimizing the game. Per-version details are in [docs/device-tests/](device-tests/).

**PC autotests only:** 3 and 4 players, rejecting a fifth, the same player rejoining, turning the mod off mid-battle,
server limits, client queue overflow, the standalone `coop_server.py` server.

**Not yet tested on phones:** Tailscale and ZeroTier, 3–4 players, network loss at the host, turning the mod off
mid-battle. Every release goes through the [checklist](RELEASE_CHECKLIST.md) on phones.
