# Installing on Android

English | [Русский](INSTALL.ru.md)

Step-by-step installation of the Co-op mod («Кооператив») on one phone. Repeat it on every player's phone, then go on to
[connecting players](CONNECT.md).

## Contents

- [What you need](#what-you-need)
- [Where to download the files](#where-to-download-the-files)
- [Step 1. Zombix (MelonLoader)](#step-1-zombix-melonloader)
- [Step 2. ModKit core](#step-2-modkit-core)
- [Step 3. ModKit libraries](#step-3-modkit-libraries)
- [Step 4. The Co-op mod](#step-4-the-co-op-mod)
- [Step 5. Virtual network app](#step-5-virtual-network-app)
- [Checking the installation](#checking-the-installation)
- [Updating the mod](#updating-the-mod)
- [If something went wrong](#if-something-went-wrong)

## What you need

- An Android phone with an **arm64** processor (almost every phone from recent years). Zombix doesn't run on other
  processors.
- A file manager that can extract zip files: the built-in "Files" app or, for example, ZArchiver.
- Permission to install apps from unknown sources (Android asks for it itself when you install the APK).

The mod works **only in Zombix on MelonLoader**, a Magic Survival clone with mod support. It won't start in the original
Magic Survival from Google Play or in the old Frida version of Zombix. The original game stays installed, and the clone
has its own saves.

## Where to download the files

Get every file from the official releases on GitHub. Don't download them from third-party sites.

| What | File | Where |
|---|---|---|
| Zombix game (MelonLoader) | `Zombix-melon.apk` | [MS_KIT → Zombix (MelonLoader) v0.9941](https://github.com/Kysnez4/MS_KIT/releases/tag/zombix-melon-v0.9941) |
| ModKit core | `ModKit-core.zip` | [MS_KIT → ModKit: core (MelonLoader)](https://github.com/Kysnez4/MS_KIT/releases/tag/modkit-core) |
| ModKit libraries | `modkit_combat.zip`, `modkit_hero.zip`, `modkit_spells.zip`, `modkit_companions.zip` | [MS_KIT → ModKit: libraries (MelonLoader)](https://github.com/Kysnez4/MS_KIT/releases/tag/modkit-libs) |
| Co-op mod | `coop.zip` | [this repository → latest release](https://github.com/OneCOlyl/msm/releases/latest) |
| Virtual network | Anywherelan, Tailscale or ZeroTier app | see [step 5](#step-5-virtual-network-app) |

Download files from the **Assets** list under the release description. The "Source code" archives are sources; you
don't need them to install.

## Step 1. Zombix (MelonLoader)

1. Download `Zombix-melon.apk` and open it. Allow installation from unknown sources. If Play Protect warns about an
   unknown app, choose "Install anyway".
2. Start Zombix. The game opens the **"All files access"** setting by itself: turn it on and return to the game.
   Mods won't load without this permission.
3. **Wait for the main menu.** The first launch takes several minutes: MelonLoader prepares the game libraries. Don't
   close or minimize the game, even if the screen doesn't change for a long time.
4. When the main menu appears, close the game.

After the first launch the phone has the folder `/sdcard/MelonLoader/com.vkslrzm.Zombix/` (in a file manager:
"Internal storage" → `MelonLoader` → `com.vkslrzm.Zombix`). If it's missing, the first launch didn't finish: start the
game again and wait for the menu.

## Step 2. ModKit core

1. Download `ModKit-core.zip`.
2. Extract it **into the folder** `/sdcard/MelonLoader/com.vkslrzm.Zombix/`. The folders from the archive must merge
   with the existing ones, not land next to them inside a nested `ModKit-core` folder.
3. Check that the file `/sdcard/MelonLoader/com.vkslrzm.Zombix/Mods/ModKit.dll` exists.

## Step 3. ModKit libraries

The mod needs four libraries: `modkit_combat`, `modkit_hero`, `modkit_spells` and `modkit_companions`, version 1.x
(the libraries release currently has 1.0).

1. Download `modkit_combat.zip`, `modkit_hero.zip`, `modkit_spells.zip` and `modkit_companions.zip`. You can also
   download every library from the release: the extra ones don't turn on by themselves.
2. Put the zip files, **without extracting them**, into `/sdcard/MelonLoader/com.vkslrzm.Zombix/UserData/ModKit/mods/`.
   If the `mods` folder doesn't exist, create it.

Another way: put the zip files into `Download/ModKit` and install each one with the «+» button in the «Моды» (Mods)
window, as in step 4.

The libraries change nothing in the game by themselves: ModKit turns them on when a mod that needs them is on.

## Step 4. The Co-op mod

1. Download [`coop.zip`](https://github.com/OneCOlyl/msm/releases/latest/download/coop.zip) and leave it in the
   `Download` folder. Don't extract it.
2. Start Zombix and open the **«Моды»** (Mods) window from the main menu.
3. Tap **«+»** at the top of the window and pick `coop`. ModKit looks for archives in `Download` and `Download/ModKit`.
4. Turn on the **«Кооператив»** (Co-op) card. The required libraries turn on by themselves.
5. A blue **«Кооп»** (Co-op) button appears at the top center of the screen: installation is done.

## Step 5. Virtual network app

Phones connect to each other through a virtual network. All players must pick **the same** app and be in **the same**
network.

| App | Where to get it | Account |
|---|---|---|
| Anywherelan | [F-Droid](https://f-droid.org/packages/com.anywherelan.awl/) or an APK from the [GitHub releases](https://github.com/anywherelan/awl/releases) | not needed |
| Tailscale | [tailscale.com/download/android](https://tailscale.com/download/android) (Google Play) | needed (Google, Microsoft, GitHub, Apple and others) |
| ZeroTier | the ZeroTier One app from [zerotier.com/download](https://www.zerotier.com/download/) (Google Play) | the host needs one to create the network |

If you're not sure which to pick, take Anywherelan: no sign-up, tested with the mod on phones. Network setup is
described in the [connection guide](CONNECT.md).

If all players are on the same Wi-Fi network, you may not need a virtual network. See
[Players on the same Wi-Fi network](CONNECT.md#players-on-the-same-wi-fi-network).

## Checking the installation

- The «Кооператив» card is turned on in the «Моды» window.
- The main menu has the «Кооп» button at the top center, and it opens the «Кооператив» window.
- The ModKit log `/sdcard/MelonLoader/com.vkslrzm.Zombix/UserData/ModKit/modkit.log` has the line
  `кооператив <версия> включён, имя «Игрок NN»` (co-op <version> enabled, name "Player NN").

The mod picks the player name («Игрок» and a random number) by itself the first time it's turned on. Friends see it in
labels and in the player list.

## Updating the mod

1. Download the new `coop.zip` into `Download`.
2. In the «Моды» window tap «+» and pick it.
3. Update the mod for **all** players. A different mod version will connect, but new features work only if both players
   have them (see [Version compatibility](FEATURES.md#version-compatibility)).

To remove the mod, long-press its card in the «Моды» window.

## If something went wrong

| What happens | What to do |
|---|---|
| The first launch never finishes | Check free space on the phone. Close the game and start it again. |
| No `MelonLoader/com.vkslrzm.Zombix` folder | The first launch didn't reach the main menu, or "All files access" wasn't granted. Turn it on in Android Settings → Apps → Zombix → Permissions and start the game again. |
| No «Моды» window | The ModKit core isn't installed: check that `Mods/ModKit.dll` exists (step 2). |
| «+» says no new archives were found | Check that `coop.zip` is in `Download` or `Download/ModKit` and isn't extracted. |
| The card says a library is needed, or «нужен … >=1.0 <2» | A library is missing or has an incompatible version. Repeat step 3 with the files from the libraries release. |
| The mod is on, but there's no «Кооп» button | Open «Моды» → the «Кооператив» card → «Настройки» (Settings): the same window opens. Look for the cause in `modkit.log`. |

If that didn't help, open an [issue](https://github.com/OneCOlyl/msm/issues) and attach the last lines of `modkit.log`
containing `[coop]`.
