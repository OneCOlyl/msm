# Connecting players

English | [Русский](CONNECT.ru.md)

How to set up a game for 2–4 people, including from different cities and networks. The mod must already be installed on
every phone: see [installing on Android](INSTALL.md).

## Contents

- [How it works](#how-it-works)
- [Before you play](#before-you-play)
- [Virtual network](#virtual-network)
  - [Anywherelan](#anywherelan)
  - [Tailscale](#tailscale)
  - [ZeroTier](#zerotier)
  - [Players on the same Wi-Fi network](#players-on-the-same-wi-fi-network)
- [Creating a game (host)](#creating-a-game-host)
- [Joining a game (other players)](#joining-a-game-other-players)
- [Connection check](#connection-check)
- [If you can't connect](#if-you-cant-connect)

## How it works

- One player is the **host**. They tap «Создать игру» (Create game), and a server starts inside their Zombix on port
  **8766**. You don't need a separate server or VPS.
- The others connect to the host by IP address. The host is always player 1; the others get numbers 2–4 in turn.
- Phones on different networks (at home, at a friend's, on mobile data) can't see each other directly. So all players
  join one **virtual network**: Anywherelan, Tailscale or ZeroTier. In it each phone has its own permanent address, and
  friends connect to the host at that address.

## Before you play

- Every phone has the same version of the Co-op mod.
- Everyone picked **one** VPN app and joined **one** network.
- Android keeps only one VPN on at a time. Turn off other VPN apps and proxies, or the virtual network disconnects.
- Turn off battery saving for the VPN app and for Zombix (Settings → Apps → … → Battery → "Unrestricted"), or Android
  may stop them in the background.
- **The host keeps the game open** for the whole session. Android suspends a minimized game: after 40 seconds without
  data the server disconnects players, and the mod reconnects them when the host comes back.

## Virtual network

Set up the network once: next time, just turn on the VPN in the app.

### Anywherelan

Free, no sign-up, phones connect directly. It's the only network tested with the mod on phones.

1. Install Anywherelan on every phone: [F-Droid](https://f-droid.org/packages/com.anywherelan.awl/) or an APK from the
   [GitHub releases](https://github.com/anywherelan/awl/releases).
2. Open the app and turn on the VPN. Android asks for permission to set up a VPN connection: allow it.
3. Add friends. The host sends each player their link, QR code or device ID (peer ID). The player opens the link or adds
   the ID in the app, and the host gets a friend request, which the host **accepts**. An invite link adds the device
   right away, without confirmation.
4. The friend must be online in the device list. Anywherelan addresses usually look like `10.66.0.X`. The host's address
   is shown in the «Кооп» window, on the line marked «← VPN».

Guests only need to be friends with the host: all game data goes through the host, and guests don't need to connect to
each other.

If both phones are behind a strict NAT (some mobile carriers, for example), Anywherelan relays data through an
intermediate node. The game works, but ping may be higher.

### Tailscale

You need an account (Google, Microsoft, GitHub, Apple and others). The free plan is enough.

1. Install [Tailscale](https://tailscale.com/download/android) on every phone, sign in and connect.
2. Join the devices in one of two ways:
   - **Share the host's phone (easiest).** The host opens the [Tailscale console](https://login.tailscale.com/admin/machines)
     → Machines → their phone → "Share…" and sends an invite (email or link) to each friend. The friend accepts the invite
     in their own account, and the host's phone appears in their device list. Friends can then connect to the host, which
     is all the game needs.
   - **Invite friends to your network.** The host opens [Users](https://login.tailscale.com/admin/users) → "Invite users".
     Friends join the host's network, and all devices see each other.
3. The host's address looks like `100.X.Y.Z`. It's shown in the Tailscale app and in the «Кооп» window, marked «← VPN».

Don't sign the whole group into one account: it's harder to tell whose device is whose, and easier to disconnect someone
else's by accident.

### ZeroTier

The host needs an account to create the network; the others only need the app.

1. The host signs in at [my.zerotier.com](https://my.zerotier.com/), clicks **Create A Network** and copies the
   **Network ID**: 16 characters like `d5e04297a16fa690`. Send it to your friends.
2. Each player installs the **ZeroTier One** app ([zerotier.com/download](https://www.zerotier.com/download/)), taps "+",
   enters the Network ID and turns the network on. Android asks for VPN permission: allow it.
3. On the network page, under **Members**, the host ticks **Auth** for each new device. Until a device is authorized, its
   network shows "Access Denied".
4. A device's address is in the **Managed IPs** column on the network page and in the ZeroTier One app. The address range
   is set in the network settings on my.zerotier.com.

### Players on the same Wi-Fi network

If all phones are connected to the same router, you can do without a virtual network: the host's «Кооп» window shows an
address like `192.168.X.Y` (without the «← VPN» mark), and friends enter it. If connecting fails, the router most likely
isolates devices from each other (common in guest and public networks); then turn on a virtual network.

## Creating a game (host)

1. Turn on the VPN on your phone.
2. Start Zombix and tap **«Кооп»** (Co-op) in the main menu (top center). If you can't see the button: «Моды» (Mods) →
   the «Кооператив» card → «Настройки» (Settings).
3. Tap **«Создать игру (я хост)»** (Create game, I'm the host). The status line at the top of the window shows
   «Создаю игру…» (Creating game…), then «Игра создана. Сообщите друзьям IP, отмеченный «← VPN».» (Game created. Give
   your friends the IP marked «← VPN».).
4. In the «Ваш IP» (Your IP) block find the address marked **«← VPN»**: it's your address in the virtual network. Give
   it to your friends. The other addresses (`192.168…`, the mobile network address) won't work for friends on other
   networks.
5. Don't close or minimize the game until everyone has joined and finished the battle.

The default port is `8766`. If another program on the phone took it, the mod takes the next free one (up to `8770`) and
says so. Friends don't need to change the port: the mod finds the game by itself.

The «Игроки» (Players) list shows the room code next to your name (`комната ABC123`, room ABC123). You don't need to
enter it: friends connect by IP only.

## Joining a game (other players)

1. Turn on the VPN and make sure the host's phone is visible in the VPN app.
2. Start Zombix and tap **«Кооп»**.
3. On the «Подключиться к хосту — IP и порт» (Connect to host: IP and port) line, enter the four numbers of the host's
   IP, for example `10` . `66` . `0` . `2`. Leave the port at `8766` unless the host said otherwise.
4. Tap **«Подключиться»** (Connect). The status shows «подключаюсь…» (connecting…), then «Хост …: на связи, пинг N мс»
   (Host …: connected, ping N ms), and the «Игроки» list shows all participants.
5. Start a battle, each player in their own game. Friends appear in your battle as allies. Whoever enters the battle
   first waits on pause until the others enter (2 minutes at most).

You can tap «Подключиться» even before the host creates the game: the mod says «Хост ещё не создал игру» (The host
hasn't created the game yet) and joins by itself when the game appears.

The IP you entered is saved: next time just tap «Подключиться».

One game holds up to 4 players. A fifth gets «Комната заполнена» (Room is full).

## Connection check

Check in order: each step makes sense only if the previous one passed.

### 1. The virtual network works

- **Anywherelan:** the friend is online in the device list.
- **Tailscale:** the host's device is in the list and isn't marked as offline.
- **ZeroTier:** the app shows the network as OK; on my.zerotier.com every member has Auth and a recent Last Seen time.

### 2. You know the right host IP

- On the host: «Кооп» window → «Ваш IP» → the line marked «← VPN». The address must match the phone's address in the
  VPN app.
- Compare with the usual form: Anywherelan `10.66.0.X`, Tailscale `100.X.Y.Z`, ZeroTier from the Managed IPs column.
- Don't use `127.0.0.1`, `192.168.…` (unless you're on the same Wi-Fi network) or mobile internet addresses.

### 3. The host's phone responds (ping)

You need [Termux](https://f-droid.org/packages/com.termux/) on the guest's phone or a computer in the same virtual
network:

```bash
ping -c 4 10.66.0.2
```

On Windows: `ping -n 4 10.66.0.2`. If there are no replies, the problem is in the virtual network, not the game: go
back to step 1. Note that some networks and devices don't answer ping even when everything works. Then go on to step 4.

### 4. The game port is open

The host must have tapped «Создать игру» already and keep the game open.

- **Termux on the guest's phone:**

  ```bash
  pkg install netcat-openbsd
  nc -zv 10.66.0.2 8766
  ```

  `succeeded` or `open` means the port is reachable. `Connection refused` means the host didn't create the game or
  minimized it. A long wait with no answer means the virtual network blocks the port, or the IP is wrong.

- **Windows (PowerShell), a computer in the same virtual network:**

  ```powershell
  Test-NetConnection 10.66.0.2 -Port 8766
  ```

  `TcpTestSucceeded : True` means the port is reachable.

- **Check that the mod's server is the one answering** (Python 3 in Termux or on a computer, file
  [`server/coop_client.py`](../server/coop_client.py)):

  ```bash
  python coop_client.py 10.66.0.2 8766
  ```

  The line `< {"type":"WELCOME","protocol":2,…}` means the mod's server works. Type `quit`.
  **Don't type `join`**: it takes a player slot, and an extra participant appears for the host.

### 5. In-game connection

In the guest's «Кооп» window the status reads «на связи, пинг N мс» (connected, ping N ms). The higher the ping, the
more the allies lag behind. On a weak connection, lower the mod setting «Обновлений позиции в секунду» (Position updates
per second) to 10.

## If you can't connect

Messages from the status line of the «Кооп» window and on-screen labels:

| Message | What to do |
|---|---|
| «хост не принимает подключения» (host isn't accepting connections) | The host didn't tap «Создать игру» or minimized the game. The host's game must be open. |
| «адрес недоступен (VPN выключен или IP неверный)» (address unreachable: VPN off or wrong IP) | Turn on the VPN on both phones, check the IP (check steps 1–2). |
| «время ожидания истекло» (timed out) or «сервер не ответил за 8 с» (server didn't answer within 8 s) | The host's phone doesn't respond: one of the two has the VPN off, the host isn't in the same network, or the IP is wrong. |
| «нет ответа 20 с» (no answer for 20 s) | The connection dropped mid-game. The mod reconnects by itself when the network comes back. |
| «на порту … отвечает не сервер кооператива» (the port … isn't answered by a co-op server) | Another program runs at this address and port. Check the IP and port. |
| Tapping «Создать игру» did nothing | Look at the status line at the top of the «Кооп» window: it shows the cause. |
| «Хост ещё не создал игру» (The host hasn't created the game yet) | Nothing to do: the mod waits and joins the host's game by itself. |
| «Комната заполнена (максимум 4 игрока)» (Room is full, 4 players max) | The game already has 4 players. |
| «Версия «Кооператива» не совпадает с хостом» (Co-op version doesn't match the host) | Install the latest mod version for all players. |
| «У … другая версия мода» (… has a different mod version) or «У … старая версия мода» (… has an old mod version) | You are connected, but some features (shared enemies, the look of special mobs) may not work. Both versions are shown under that friend in the «Кооп» window. Install the same, latest version for all players. |
| «Слишком много подключений с вашего адреса» (Too many connections from your address), «Сервер переполнен» (Server is full) | Close extra copies of the game or wait: the mod retries by itself. |
| «Слишком частые подключения, попробуйте через минуту» (Connecting too often, try again in a minute) | More than 60 connections per minute. Wait a minute. |
| «Порт 8766 занят другой программой, игра создана на порту 8767» (Port 8766 is taken by another program, game created on port 8767) | Nothing to do: friends with port 8766 find the game by themselves. |
| «Не удалось создать игру: порты 8766–8770 заняты» (Couldn't create the game: ports 8766–8770 are taken) | Another program on the host's phone holds the ports (a server in Termux, for example). Close it, or set another port in the «Кооп» window and give it to your friends. |
| «Связь потеряна, переподключаюсь…» (Connection lost, reconnecting…) | Nothing to do: the mod reconnects by itself every 3 seconds. |

Mod log: `/sdcard/MelonLoader/com.vkslrzm.Zombix/UserData/ModKit/modkit.log` (lines with `[coop]`). If you open an
[issue](https://github.com/OneCOlyl/msm/issues), attach the last lines of the host's and guest's logs and name the VPN.

The connection is not encrypted and has no password. Play only inside the virtual network and don't open port 8766 to
the internet on your router. More in [Security](FEATURES.md#security).
