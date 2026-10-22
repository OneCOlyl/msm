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

PROTOCOL = 2
MAX_PLAYERS = 4
MAX_LINE = 16 * 1024
IDLE_TIMEOUT = 40
ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"


def clean_name(value):
    s = "".join(ch for ch in str(value or "") if ch.isprintable())[:20].strip()
    return s or "Игрок"


class Conn:
    def __init__(self, cid, writer):
        self.id = cid
        self.writer = writer
        self.name = "Игрок"
        self.client_id = ""
        self.slot = 0
        self.room = None
        self.last_seen = time.monotonic()
        self.closed = False

    def send(self, obj):
        if self.closed:
            return
        try:
            self.writer.write((json.dumps(obj, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
        except Exception:
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
    def __init__(self, log=print):
        self.rooms = {}
        self.conns = set()
        self.next_id = 0
        self.log = log

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

    # ---------------------------------------------------------------- сообщения
    def handle(self, c, m):
        kind = m.get("type")
        if kind == "CREATE_ROOM":
            self.leave_room(c)
            c.name = clean_name(m.get("name"))
            room = Room(self.new_code())
            self.rooms[room.code] = room
            self.add_to_room(c, room)
            c.send(self.room_msg("ROOM_CREATED", room, c))
            self.log(f"{c.id} «{c.name}» создал комнату {room.code}")

        elif kind == "JOIN_ROOM":
            code = str(m.get("roomCode") or "").strip().upper()
            if code in ("", "*"):
                free = [r for r in self.rooms.values() if len(r.players) < MAX_PLAYERS]
                if free:
                    room = max(free, key=lambda r: len(r.players))
                elif self.rooms:
                    return self.error(c, "room_full", "Комната заполнена (максимум 4 игрока).")
                else:
                    room = Room(self.new_code())
                    self.rooms[room.code] = room
            else:
                room = self.rooms.get(code)
                if room is None:
                    return self.error(c, "room_not_found", "Комната не найдена.")
            if c in room.players:
                return c.send(self.room_msg("ROOM_JOINED", room, c))
            cid = str(m.get("clientId") or "")
            prefer = 0
            stale = next((p for p in room.players if p is not c and cid and p.client_id == cid), None)
            if stale is not None:  # тот же игрок переподключился раньше, чем сервер заметил обрыв
                prefer = stale.slot
                self.leave_room(stale)
                stale.close()
            if len(room.players) >= MAX_PLAYERS:
                return self.error(c, "room_full", "Комната заполнена (максимум 4 игрока).")
            self.leave_room(c)
            c.name = clean_name(m.get("name"))
            c.client_id = cid
            self.add_to_room(c, room, prefer)
            c.send(self.room_msg("ROOM_JOINED", room, c))
            for p in room.players:
                if p is not c:
                    p.send({"type": "PLAYER_JOINED", "playerId": c.id, "name": c.name, "slot": c.slot})
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
    async def client(self, reader, writer):
        self.next_id += 1
        c = Conn(f"p{self.next_id}", writer)
        self.conns.add(c)
        peer = writer.get_extra_info("peername")
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


async def main_async(host, port):
    srv = Server()
    server = await asyncio.start_server(srv.client, host, port, limit=MAX_LINE)
    asyncio.ensure_future(srv.reaper())
    print(f"Magic Survival Co-op server (protocol {PROTOCOL}) listening on {host}:{port}", flush=True)
    async with server:
        await server.serve_forever()


def main():
    ap = argparse.ArgumentParser(description="Magic Survival Co-op room server")
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8766)
    args = ap.parse_args()
    try:
        asyncio.run(main_async(args.host, args.port))
    except KeyboardInterrupt:
        print("остановлено")


if __name__ == "__main__":
    main()
