#!/usr/bin/env python3
"""Проверка связи без игры: python coop_client.py <IP хоста> [порт]

Команды: join [код] [имя] | create [имя] | state {"x":1,"y":2} | sos | ping | leave | quit
Подходит и для сервера в моде (хост нажал «Создать игру»), и для coop_server.py.
"""
import json
import socket
import sys
import threading
import time


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return
    host = sys.argv[1]
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 8766
    sock = socket.create_connection((host, port), timeout=10)
    sock.settimeout(None)
    f = sock.makefile("rwb")
    print(f"Подключено к {host}:{port}. Наберите: join")

    def reader():
        for line in f:
            print("<", line.decode("utf-8", errors="replace").strip())
        print("сервер закрыл соединение")

    threading.Thread(target=reader, daemon=True).start()

    send_lock = threading.Lock()   # пинг идёт из своего потока — строки не должны перемешаться

    def send(obj):
        data = (json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8")
        with send_lock:
            f.write(data)
            f.flush()

    def pinger():
        while True:
            time.sleep(5)
            try:
                send({"type": "PING", "t": 0})
            except OSError:
                return

    threading.Thread(target=pinger, daemon=True).start()

    for raw in sys.stdin:
        cmd, _, rest = raw.strip().partition(" ")
        cmd = cmd.lower()
        if cmd in ("quit", "exit"):
            break
        if cmd == "create":
            send({"type": "CREATE_ROOM", "name": rest or "Тест"})
        elif cmd == "join":
            parts = rest.split(" ", 1)
            code = parts[0] if parts and parts[0] else ""
            name = parts[1] if len(parts) > 1 else "Тест"
            send({"type": "JOIN_ROOM", "roomCode": code, "name": name})
        elif cmd == "state":
            try:
                send({"type": "PLAYER_STATE", "state": json.loads(rest or "{}")})
            except ValueError as e:
                print("неверный JSON:", e)
        elif cmd == "sos":
            send({"type": "EVENT", "event": {"kind": "sos"}})
        elif cmd == "ping":
            send({"type": "PING", "t": 0})
        elif cmd == "leave":
            send({"type": "LEAVE_ROOM"})
        elif cmd:
            print("команды: join [код] [имя] | create [имя] | state {json} | sos | ping | leave | quit")
    sock.close()


if __name__ == "__main__":
    main()
