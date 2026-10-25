#!/usr/bin/env python3
"""Magic Survival Co-op — отдельный сервер комнат (протокол v2).

Нужен, только если хотите держать сервер не в игре хоста, а отдельно: в Termux на телефоне или на ПК.
Обычно он не нужен: мод сам запускает сервер, когда хост нажимает «Создать игру».

Запуск:  python coop_server.py [--port 8766] [--host 0.0.0.0]
Только стандартная библиотека Python 3.8+.
"""
import argparse
import asyncio
import json
import secrets
import time
from collections import deque

PROTOCOL = 2
MAX_PENDING_BYTES = 512 * 1024   # ~512 сообщений состояния
MAX_PLAYERS = 4
MAX_LINE = 16 * 1024
IDLE_TIMEOUT = 40
# защита от перегрузки — те же значения, что у встроенного сервера (CoopServer.cs)
MAX_CONNECTIONS = 32
MAX_CONNECTIONS_PER_IP = 8
ROOM_OPS_PER_MINUTE = 60
MAX_ROOMS = 16
JOIN_TIMEOUT = 30
ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"


def clean_name(value):
    s = "".join(ch for ch in str(value or "") if ch.isprintable())[:20].strip()
    return s or "Игрок"


def client_id_of(m):
    v = m.get("clientId")
    return v if isinstance(v, str) else ""


def protocol_ok(m):
    """Нет поля protocol (или null) — старый клиент, пускаем. Иначе только целое число, равное PROTOCOL (как ProtocolOk в CoopServer.cs).
    -> (ok, как показать значение в ошибке)"""
    if "protocol" not in m or m["protocol"] is None:
        return True, None
    pv = m["protocol"]
    if isinstance(pv, int) and not isinstance(pv, bool) and pv == PROTOCOL:
        return True, None
    try:
        shown = json.dumps(pv, ensure_ascii=False)
    except (TypeError, ValueError):
        shown = "?"
    return False, (shown[:20] + "…") if len(shown) > 20 else shown


class Conn:
    def __init__(self, cid, writer, ip=""):
        self.id = cid
        self.writer = writer
        self.ip = ip
        self.last_in_room = time.monotonic()
        self.ending = False
        self.name = "Игрок"
        self.client_id = ""
        self.slot = 0
        self.room = None
        self.last_seen = time.monotonic()
        self.closed = False

    def send(self, obj):
        if self.closed or self.ending:
            return
        try:
            # медленный клиент: как очередь на 512 сообщений во встроенном сервере — не копим бесконечно, отключаем
            if self.writer.transport.get_write_buffer_size() > MAX_PENDING_BYTES:
                self.close()
                return
            self.writer.write((json.dumps(obj, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
        except Exception:
            self.close()

    def close_after_send(self):
        """Последнее сообщение уже в буфере — закрыть, когда оно уйдёт (writer.close дописывает буфер)."""
        self.ending = True
        self.close()

    def close(self):
        if not self.closed:
            self.closed = True
            try:
                self.writer.close()
            except Exception:
                pass


class Room:
    def __init__(self, code):
        self.code = code
        self.players = []


class Server:
    def __init__(self, log=print, max_connections=MAX_CONNECTIONS, max_per_ip=MAX_CONNECTIONS_PER_IP,
                 room_ops_per_minute=ROOM_OPS_PER_MINUTE, max_rooms=MAX_ROOMS, join_timeout=JOIN_TIMEOUT):
        self.rooms = {}
        self.conns = set()
        self.room_ops = {}
        self.next_id = 0
        self.log = log
        self.max_connections = max_connections
        self.max_per_ip = max_per_ip
        self.room_ops_per_minute = room_ops_per_minute
        self.max_rooms = max_rooms
        self.join_timeout = join_timeout

    # ---------------------------------------------------------------- комнаты
    def new_code(self):
        while True:
            code = "".join(secrets.choice(ALPHABET) for _ in range(6))
            if code not in self.rooms:
                return code

    def room_msg(self, kind, room, me):
        return {
            "type": kind, "roomCode": room.code, "playerId": me.id, "slot": me.slot,
            "players": [{"playerId": p.id, "name": p.name, "slot": p.slot} for p in room.players],
        }

    def add_to_room(self, c, room, prefer=0):
        used = {p.slot for p in room.players}
        slot = prefer if prefer and prefer not in used else 1
        while slot in used:
            slot += 1
        c.slot, c.room = slot, room
        room.players.append(c)

    def leave_room(self, c):
        room = c.room
        if room is None:
            return
        if c in room.players:
            room.players.remove(c)
        c.room, c.slot = None, 0
        if not room.players:
            self.rooms.pop(room.code, None)
            return
        for p in room.players:
            p.send({"type": "PLAYER_LEFT", "playerId": c.id})

    @staticmethod
    def error(c, code, message):
        c.send({"type": "ERROR", "code": code, "message": message})

    def enter_room(self, c, room, m, reply, prefer=0):
        """Общий вход для CREATE_ROOM и JOIN_ROOM (как EnterRoomLocked во встроенном сервере)."""
        self.leave_room(c)
        c.name = clean_name(m.get("name"))
        c.client_id = client_id_of(m)
        self.add_to_room(c, room, prefer)
        c.send(self.room_msg(reply, room, c))
        for p in room.players:
            if p is not c:
                p.send({"type": "PLAYER_JOINED", "playerId": c.id, "name": c.name, "slot": c.slot})

    def drop_stale(self, c, cid):
        """Другие соединения с тем же clientId — тот же игрок, чей обрыв ещё не замечен: убрать и закрыть. -> (комната, слот)."""
        found = (None, 0)
        if not cid:
            return found
        for s in [p for p in self.conns if p is not c and p.client_id == cid]:
            if s.room is not None and found[0] is None:
                found = (s.room, s.slot)
            self.leave_room(s)
            s.client_id = ""
            s.close()
            self.log(f"{s.id}: тот же игрок вошёл заново — старое соединение закрыто")
        return found

    def room_op_allowed(self, ip):
        now = time.monotonic()
        q = self.room_ops.setdefault(ip, deque())
        while q and now - q[0] > 60:
            q.popleft()
        if len(self.room_ops) > 1024:
            for k in [k for k, v in self.room_ops.items() if not v]:
                del self.room_ops[k]
            q = self.room_ops.setdefault(ip, q)
        if len(q) >= self.room_ops_per_minute:
            return False
        q.append(now)
        return True

    # ---------------------------------------------------------------- сообщения
    def handle(self, c, m):
        kind = m.get("type")
        if kind in ("CREATE_ROOM", "JOIN_ROOM"):
            ok, shown = protocol_ok(m)
            if not ok:
                self.error(c, "protocol_mismatch",
                           f"Версия «Кооператива» не совпадает с сервером (протокол {shown}, у сервера {PROTOCOL}). Обновите мод у всех игроков.")
                return
            if not self.room_op_allowed(c.ip):
                self.log(f"{c.id}: слишком часто входит в комнаты, отключаю")
                self.error(c, "rate_limited", "Слишком частые подключения, попробуйте через минуту.")
                c.close_after_send()
                return
        if kind == "CREATE_ROOM":
            self.drop_stale(c, client_id_of(m))
            if len(self.rooms) >= self.max_rooms:
                return self.error(c, "server_busy", "На сервере слишком много комнат, попробуйте позже.")
            room = Room(self.new_code())
            self.rooms[room.code] = room
            self.enter_room(c, room, m, "ROOM_CREATED")
            self.log(f"{c.id} «{c.name}» создал комнату {room.code}")

        elif kind == "JOIN_ROOM":
            stale_room, stale_slot = self.drop_stale(c, client_id_of(m))
            code = str(m.get("roomCode") or "").strip().upper()
            if code in ("", "*"):
                free = [r for r in self.rooms.values() if len(r.players) < MAX_PLAYERS or r is c.room]
                if free:
                    room = max(free, key=lambda r: len(r.players))
                elif self.rooms:
                    return self.error(c, "room_full", "Комната заполнена (максимум 4 игрока).")
                elif len(self.rooms) >= self.max_rooms:
                    return self.error(c, "server_busy", "На сервере слишком много комнат, попробуйте позже.")
                else:
                    room = Room(self.new_code())
                    self.rooms[room.code] = room
            else:
                room = self.rooms.get(code)
                if room is None:
                    return self.error(c, "room_not_found", "Комната не найдена.")
            if c in room.players:
                return c.send(self.room_msg("ROOM_JOINED", room, c))
            if len(room.players) >= MAX_PLAYERS:
                return self.error(c, "room_full", "Комната заполнена (максимум 4 игрока).")
            self.enter_room(c, room, m, "ROOM_JOINED", stale_slot if stale_room is room else 0)
            self.log(f"{c.id} «{c.name}» вошёл в {room.code} (слот {c.slot})")

        elif kind in ("PLAYER_STATE", "EVENT"):
            is_state = kind == "PLAYER_STATE"
            payload = m.get("state" if is_state else "event")
            if not isinstance(payload, dict):
                return self.error(c, "invalid_message", "Нет поля state." if is_state else "Нет поля event.")
            if c.room is None:
                return self.error(c, "not_in_room", "Сначала войдите в комнату.")
            to = None if is_state else payload.get("to")
            out = {"type": "REMOTE_PLAYER_STATE" if is_state else "REMOTE_EVENT", "playerId": c.id, "slot": c.slot,
                   ("state" if is_state else "event"): payload}
            for p in c.room.players:
                if p is not c and (to is None or p.id == to):
                    p.send(out)

        elif kind == "PING":
            c.send({"type": "PONG", "t": m.get("t", 0)})

        elif kind == "LEAVE_ROOM":
            self.leave_room(c)

        else:
            self.error(c, "unknown_type", f"Неизвестный тип сообщения: {kind}")

    # ---------------------------------------------------------------- сеть
    async def reject(self, reader, writer, code, message):
        """Ошибка без регистрации соединения: дописать, закрыть отправку, дочитать присланное (иначе сброс съест ошибку)."""
        try:
            writer.write((json.dumps({"type": "ERROR", "code": code, "message": message}, ensure_ascii=False) + "\n").encode("utf-8"))
            await asyncio.wait_for(writer.drain(), 1.5)
            if writer.can_write_eof():
                writer.write_eof()
            async def drain_in():
                while await reader.read(1024):
                    pass
            await asyncio.wait_for(drain_in(), 1.5)
        except Exception:
            pass
        finally:
            try:
                writer.close()
            except Exception:
                pass

    async def client(self, reader, writer):
        peer = writer.get_extra_info("peername")
        ip = peer[0] if isinstance(peer, tuple) and peer else ""
        busy = None
        if len(self.conns) >= self.max_connections:
            busy = "Сервер переполнен, попробуйте позже."
        elif sum(1 for x in self.conns if x.ip == ip) >= self.max_per_ip:
            busy = "Слишком много подключений с вашего адреса."
        if busy:
            self.log(f"отказ {ip}: {busy}")
            return await self.reject(reader, writer, "server_busy", busy)
        self.next_id += 1
        c = Conn(f"p{self.next_id}", writer, ip)
        self.conns.add(c)
        self.log(f"подключение {c.id} с {peer}")
        c.send({"type": "WELCOME", "protocol": PROTOCOL, "playerId": c.id})
        try:
            while not c.closed:
                try:
                    line = await reader.readuntil(b"\n")
                except asyncio.LimitOverrunError:
                    self.error(c, "message_too_large", "Сообщение слишком большое.")
                    break
                except asyncio.IncompleteReadError:
                    break
                c.last_seen = time.monotonic()
                line = line.strip()
                if not line:
                    continue
                try:
                    m = json.loads(line.decode("utf-8", errors="replace"))
                except ValueError:
                    self.error(c, "invalid_json", "Некорректный JSON.")
                    continue
                if not isinstance(m, dict):
                    self.error(c, "invalid_message", "Ожидался JSON-объект.")
                    continue
                self.handle(c, m)
                await writer.drain()
        except (ConnectionError, OSError):
            pass
        finally:
            self.leave_room(c)
            self.conns.discard(c)
            c.close()
            self.log(f"отключение {c.id}")

    async def reaper(self):
        while True:
            await asyncio.sleep(2)
            now = time.monotonic()
            for c in list(self.conns):
                if now - c.last_seen > IDLE_TIMEOUT:
                    self.log(f"{c.id}: нет данных {IDLE_TIMEOUT} с, отключаю")
                    c.close()
                elif c.room is not None:
                    c.last_in_room = now
                elif now - c.last_in_room > self.join_timeout:
                    self.log(f"{c.id}: {self.join_timeout:.0f} с без комнаты, отключаю")
                    c.close()


async def main_async(host, port, **limits):
    srv = Server(**limits)
    server = await asyncio.start_server(srv.client, host, port, limit=MAX_LINE)
    asyncio.ensure_future(srv.reaper())
    print(f"Magic Survival Co-op server (protocol {PROTOCOL}) listening on {host}:{port}", flush=True)
    async with server:
        await server.serve_forever()


def main():
    ap = argparse.ArgumentParser(description="Magic Survival Co-op room server")
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8766)
    ap.add_argument("--max-connections", type=int, default=MAX_CONNECTIONS, help="соединений всего")
    ap.add_argument("--max-per-ip", type=int, default=MAX_CONNECTIONS_PER_IP, help="соединений с одного адреса")
    ap.add_argument("--room-ops-per-minute", type=int, default=ROOM_OPS_PER_MINUTE, help="CREATE_ROOM/JOIN_ROOM в минуту с адреса")
    ap.add_argument("--max-rooms", type=int, default=MAX_ROOMS, help="комнат одновременно")
    ap.add_argument("--join-timeout", type=float, default=JOIN_TIMEOUT, help="секунд без комнаты до отключения")
    args = ap.parse_args()
    try:
        asyncio.run(main_async(args.host, args.port, max_connections=args.max_connections, max_per_ip=args.max_per_ip,
                               room_ops_per_minute=args.room_ops_per_minute, max_rooms=args.max_rooms, join_timeout=args.join_timeout))
    except KeyboardInterrupt:
        print("остановлено")


if __name__ == "__main__":
    main()
