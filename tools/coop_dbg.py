#!/usr/bin/env python3
"""Клиент отладочной консоли coop_debug (для разработки).

Сначала проброс порта с телефона: adb -s <серийник> forward tcp:8771 tcp:8770
Затем:  python tools/coop_dbg.py 8771 "get mgr.Time_Sec" "ls hero Circle"
Без команд — интерактивный режим (строка — команда, пустая строка — выход).
"""
import socket, sys

def run(port, cmds):
    with socket.create_connection(("127.0.0.1", port), timeout=20) as s:
        f = s.makefile("rwb")
        for c in cmds:
            f.write((c + "\n").encode("utf-8")); f.flush()
            out = []
            while True:
                line = f.readline()
                if not line: raise SystemExit("соединение закрыто")
                line = line.decode("utf-8").rstrip("\n")
                if line == "<<END>>": break
                out.append(line)
            print(f"> {c}\n" + "\n".join(out))

if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"): sys.stdout.reconfigure(encoding="utf-8")
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8771
    cmds = sys.argv[2:]
    if cmds: run(port, cmds)
    else:
        while True:
            c = input("dbg> ").strip()
            if not c: break
            run(port, [c])
