#!/usr/bin/env python3
"""Проверки coop_server.py изнутри: счётчик входов (NET-06) и обратное давление по получателю (NET-11).
Сетевые ответы обоих серверов сверяет NetTest; здесь — то, что снаружи не видно.

Запуск:  python tests/server_test.py
"""
import asyncio
import json
import os
import socket
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "server"))
import coop_server  # noqa: E402

passes = fails = 0


def check(ok, what):
    global passes, fails
    if ok:
        passes += 1
        print("  PASS " + what)
    else:
        fails += 1
        print("  FAIL " + what)


def room_ops():
    print("1. счётчик входов: окно, забывание по времени, предел адресов")
    now = [1000.0]
    srv = coop_server.Server(log=lambda s: None, room_ops_per_minute=2, room_ops_window=60, max_tracked_ips=100,
                             clock=lambda: now[0])
    check(srv.room_op_allowed("a") and srv.room_op_allowed("a") and not srv.room_op_allowed("a"), "третий вход за окно → отказ")
    now[0] += 30
    check(not srv.room_op_allowed("a"), "через полокна всё ещё отказ")
    now[0] += 31
    check(srv.room_op_allowed("a"), "после окна вход снова разрешён")

    srv.room_ops.clear()
    srv.room_op_allowed("once")              # один вход и больше никогда
    now[0] += 61
    srv.sweep_room_ops()                     # то, что делает reaper раз в 2 с
    check("once" not in srv.room_ops, "адрес без новых запросов забыт после окна (по таймеру)")

    for i in range(5000):
        now[0] += 0.001
        srv.room_op_allowed(f"10.1.{i // 250}.{i % 250}")
    check(len(srv.room_ops) <= 100, f"5000 разных адресов → в счётчике {len(srv.room_ops)} (предел 100)")
    now[0] += 61
    srv.sweep_room_ops()
    check(len(srv.room_ops) == 0, "после окна счётчик пуст")

    # переполнение не сбрасывает предел активному адресу: забывается тот, кто входил давнее всех
    srv.room_op_allowed("busy"); srv.room_op_allowed("busy")
    for i in range(150):
        now[0] += 0.001
        srv.room_op_allowed(f"10.2.0.{i}")
        srv.room_op_allowed("busy")          # активный адрес продолжает стучаться
    check(not srv.room_op_allowed("busy"), "активный адрес при переполнении счётчика остаётся ограничен")


async def read_type(reader, kind, timeout=3.0):
    end = time.monotonic() + timeout
    while True:
        line = await asyncio.wait_for(reader.readline(), max(0.01, end - time.monotonic()))
        if not line:
            return None
        m = json.loads(line)
        if m.get("type") == kind:
            return m


async def send(writer, obj):
    writer.write((json.dumps(obj) + "\n").encode())
    await writer.drain()


async def backpressure():
    print("2. получатель не читает: отправитель не тормозит, получатель отключается по таймауту drain")
    srv = coop_server.Server(log=lambda s: None, drain_timeout=1.0)
    server = await asyncio.start_server(srv.client, "127.0.0.1", 0, limit=coop_server.MAX_LINE)
    port = server.sockets[0].getsockname()[1]

    ra, wa = await asyncio.open_connection("127.0.0.1", port)
    await send(wa, {"type": "CREATE_ROOM", "name": "A", "protocol": 2})
    room = (await read_type(ra, "ROOM_CREATED"))["roomCode"]

    sock = socket.socket()
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4096)
    sock.connect(("127.0.0.1", port))
    sock.setblocking(False)
    rb, wb = await asyncio.open_connection(sock=sock)
    await send(wb, {"type": "JOIN_ROOM", "roomCode": room, "name": "B", "protocol": 2})
    bid = (await read_type(rb, "ROOM_JOINED"))["playerId"]
    cb = next(c for c in srv.conns if c.id == bid)
    # B больше ничего не читает

    pad = "x" * 8000
    paused = False
    for i in range(3000):
        await send(wa, {"type": "PLAYER_STATE", "state": {"i": i, "pad": pad}})
        if i % 20 == 0:
            await asyncio.sleep(0)
        size = cb.writer.transport.get_write_buffer_size() if not cb.closed else 0
        if cb.closed or size > 128 * 1024:
            paused = not cb.closed
            break
    check(paused, f"буфер получателя переполнен ({cb.writer.transport.get_write_buffer_size() if not cb.closed else 'закрыт'} Б), но ниже предела {coop_server.MAX_PENDING_BYTES}")
    check(cb.drainer is not None and not cb.drainer.done(), "у получателя своя задача drain()")

    t0 = time.monotonic()
    await send(wa, {"type": "PING", "t": 1})
    pong = await read_type(ra, "PONG", 2.0)
    check(pong is not None and time.monotonic() - t0 < 0.5, f"отправитель получает ответы без задержки ({(time.monotonic() - t0) * 1000:.0f} мс)")

    t0 = time.monotonic()
    while not cb.closed and time.monotonic() - t0 < 4:
        await asyncio.sleep(0.05)
    check(cb.closed, f"получатель, не разбирающий буфер, отключён через {time.monotonic() - t0:.1f} с (таймаут 1 с)")

    await send(wa, {"type": "PING", "t": 2})
    check(await read_type(ra, "PONG", 2.0) is not None, "отправитель после этого жив")

    for w in (wa, wb):
        w.close()
    server.close()
    await server.wait_closed()


def main():
    sys.stdout.reconfigure(encoding="utf-8")   # консоль Windows не в UTF-8, а в выводе стрелки и кириллица
    room_ops()
    asyncio.run(backpressure())
    print(f"\nИТОГ: {passes} PASS, {fails} FAIL")
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
