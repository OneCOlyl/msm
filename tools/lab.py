#!/usr/bin/env python3
"""Проверки кооператива на двух телефонах через полигон coop_lab и консоль coop_debug.

Оба телефона подключены по USB, на обоих включены coop, coop_debug, coop_lab (и coop_cheat для магий), оба в бою.
Запуск:  python tools/lab.py [--host SERIAL] [--guest SERIAL] [--out отчёт.txt] mobs spells drop
"""
import argparse
import os
import re
import socket
import subprocess
import sys
import threading
import time

ADB = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Android", "Sdk", "platform-tools", "adb.exe")
LAB = "T:coop_lab:MagicSurvivalLab.Lab/Current"
HOST_PORT, GUEST_PORT = 8771, 8772
SPELL_NAMES = {1: "Пуля", 2: "Огн. шар", 3: "Дух", 4: "Спутник", 5: "Лёд. иглы", 6: "Щит", 7: "Молния", 8: "Электрозона",
               9: "Цунами", 10: "Метеорит", 11: "Призрак", 12: "Вихрь", 13: "Электричество", 14: "Армагеддон",
               15: "Испепеление", 16: "Энергопуля", 17: "Метель", 18: "Луч", 19: "Маг. круг", 20: "Лав. поле", 21: "Вспышка"}
ORB_SPELLS = {3: "sp", 4: "sa", 6: "sh", 8: "ez", 19: "mc"}

report = []


def say(line):
    print(line, flush=True)
    report.append(line)


def console(port, cmd, timeout=60):
    with socket.create_connection(("127.0.0.1", port), timeout=timeout) as s:
        f = s.makefile("rwb")
        f.write((cmd + "\n").encode("utf-8"))
        f.flush()
        out = []
        for raw in f:
            line = raw.decode("utf-8").rstrip("\r\n")
            if line == "<<END>>":
                break
            out.append(line)
    text = "\n".join(out)
    return text[len("результат: "):] if text.startswith("результат: ") else text


def lab(port, call):
    return console(port, f"call {LAB}.{call}")


def coop(port):
    return dict(kv.split("=", 1) for kv in lab(port, "CoopStatus").split("; ") if "=" in kv)


def puppets(port):
    raw = coop(port).get("puppets", "0")
    return {} if raw in ("0", "нет") else {k: int(v) for k, v in (x.split(":") for x in raw.split(","))}


def check(ok, what):
    say(("PASS " if ok else "FAIL ") + what)
    return ok


def setup(host, guest):
    for serial, port in ((host, HOST_PORT), (guest, GUEST_PORT)):
        subprocess.run([ADB, "-s", serial, "forward", f"tcp:{port}", "tcp:8770"], check=True, capture_output=True)
    h, g = coop(HOST_PORT), coop(GUEST_PORT)
    if h.get("coop") != "1" or g.get("coop") != "1":
        sys.exit("нет мода coop или coop_lab на одном из телефонов")
    if h.get("connected") != "True" or g.get("connected") != "True":
        sys.exit("телефоны не на связи: в окне «Кооп» создайте игру у хоста и подключите друга")
    if h.get("hosting") != "True":
        sys.exit("первый телефон (--host) не хост")
    if h.get("inRun") != "True" or g.get("inRun") != "True":
        sys.exit("оба должны быть в бою")
    for port in (HOST_PORT, GUEST_PORT):
        lab(port, "SetImmortal true")
        lab(port, "SetMana false")
        for i in SPELL_NAMES:                                   # Без магий вызванные мобы доживут до проверки
            lab(port, f"Spell {i} 0")
    say("настройка: " + lab(HOST_PORT, "SetSpawn false") + ", " + lab(HOST_PORT, "ClearEnemies") + ", у обоих бессмертие, без опыта и без магий")


def mobs():
    say("== мобы хоста у друга")
    cases = [(f"тип {t}", f"Spawn {t} 3", str(t)) for t in (1, 6, 11, 13, 16, 25, 41, 50)]
    cases += [("эпические", "SpawnEpic 6 5", "s2"), ("особые", "SpawnSpecial 5", "s1"), ("переход", "Transition", "s3")]
    for name, call, key in cases:
        lab(HOST_PORT, "ClearEnemies")
        time.sleep(2)
        made = lab(HOST_PORT, call)
        time.sleep(5)
        host = lab(HOST_PORT, "Status")
        got = puppets(GUEST_PORT)
        n = sum(v for k, v in got.items() if k == key or (key.startswith("s") and k.endswith(key)))
        check(n > 0, f"{name}: у хоста «{made}», {host.split('; ')[-1]}; у друга копий {n} ({got})")
    lab(HOST_PORT, "ClearEnemies")


def spells(ids, wait):
    say("== магии хоста у друга")
    lab(HOST_PORT, "Spawn 6 20")
    for i in ids:
        name = SPELL_NAMES[i]
        before = coop(GUEST_PORT)
        set_to = lab(HOST_PORT, f"Spell {i} 5")
        time.sleep(max(wait, 40) if i == 14 else wait)
        after = coop(GUEST_PORT)
        casts = int(after.get("castsDone", 0)) - int(before.get("castsDone", 0))
        arma = int(after.get("armaPlayed", 0)) - int(before.get("armaPlayed", 0))
        orb = dict(x.split(":") for x in after.get("partnerOrb", "").split(",") if ":" in x)
        seen = []
        if casts > 0:
            seen.append(f"повторов {casts}")
        if i in ORB_SPELLS and orb.get(ORB_SPELLS[i], "0") not in ("0", ""):
            seen.append(f"орбита {ORB_SPELLS[i]}={orb[ORB_SPELLS[i]]}")
        if arma > 0:
            seen.append(f"Армагеддон ×{arma}")
        check(bool(seen), f"{i} {name}: {set_to}; у друга {', '.join(seen) or 'ничего'}")
        lab(HOST_PORT, f"Spell {i} 0")
        if lab(HOST_PORT, "Status").endswith("врагов 0: "):
            lab(HOST_PORT, "Spawn 6 20")


def drop():
    say("== обрыв связи у друга")
    lab(GUEST_PORT, "CoopStopClient")
    time.sleep(6)
    lab(GUEST_PORT, "CoopJoin")
    deadline = time.time() + 40
    h = g = {}
    while time.time() < deadline:
        time.sleep(3)
        h, g = coop(HOST_PORT), coop(GUEST_PORT)
        if g.get("connected") == "True" and g.get("world") == "True" and abs(int(h["clock"]) - int(g["clock"])) <= 2:
            break
    check(g.get("connected") == "True", "друг переподключился")
    check(g.get("world") == "True", "общие враги у друга снова включены")
    check(abs(int(h.get("clock", -99)) - int(g.get("clock", 99))) <= 2, f"часы сошлись: хост {h.get('clock')} с, друг {g.get('clock')} с")


def counts(raw):
    return {} if raw in (None, "0", "нет") or raw.startswith("ошибка") else {int(k): int(v) for k, v in (x.split(":") for x in raw.split(","))}


def base_spell(ability):
    return (ability - 361) // 9 + 1 if 361 <= ability < 361 + 9 * 21 else None


def screenshot(serial, path):
    with open(path, "wb") as f:
        f.write(subprocess.run([ADB, "-s", serial, "exec-out", "screencap", "-p"], capture_output=True).stdout)


def calm_arena():
    """Оба героя на своих точках старта (то есть рядом), врагов нет, часы стоят: босс-волна не начнётся."""
    lab(HOST_PORT, "SetSpawn false")
    for port, dx in ((HOST_PORT, 0), (GUEST_PORT, 1.5)):
        lab(port, "HoldClock true")
        lab(port, f"ToStart {dx}")
    lab(HOST_PORT, "NoEnemies")


def magic_items(kinds):
    catalog = [ln.split("|") for ln in lab(HOST_PORT, "Catalog").splitlines() if ln.count("|") == 3]
    items = []
    if "spells" in kinds:
        items += [("магия", i, SPELL_NAMES[i], [i], []) for i in SPELL_NAMES]
    if "traits" in kinds:
        items += [("особенность", int(i), n, [base_spell(int(i))], [int(i)]) for k, i, n, _ in catalog if k == "trait" and base_spell(int(i))]
    if "combos" in kinds:
        for k, i, n, req in catalog:
            if k == "combo" and req:
                parts = [int(x) for x in req.split("+")]
                items.append(("комбинация", int(i), n, sorted({base_spell(x) for x in parts if base_spell(x)}), parts))
    return items


def batch(port, calls):
    """Несколько вызовов полигона одним подключением к консоли."""
    with socket.create_connection(("127.0.0.1", port), timeout=60) as s:
        f = s.makefile("rwb")
        for c in calls:
            f.write((f"call {LAB}.{c}\n").encode("utf-8"))
        f.flush()
        res, out = [], []
        for raw in f:
            line = raw.decode("utf-8").rstrip("\r\n")
            if line != "<<END>>":
                out.append(line)
                continue
            text = "\n".join(out)
            res.append(text[len("результат: "):] if text.startswith("результат: ") else text)
            out = []
            if len(res) == len(calls):
                break
    return res


def unity_errors_since(serial, since):
    """Исключения Unity в журнале телефона с момента since (секунды Unix): журнал не чистится, проверки идут параллельно."""
    out = subprocess.run([ADB, "-s", serial, "logcat", "-d", "-v", "epoch", "-s", "Unity:E"], capture_output=True, text=True,
                         encoding="utf-8", errors="replace").stdout
    res = []
    for ln in out.splitlines():
        parts = ln.split(None, 1)
        try:
            t = float(parts[0])
        except (ValueError, IndexError):
            continue
        if t >= since and "Exception" in ln:
            res.append(ln.split("): ", 1)[-1])
    return res


def magic(host, guest, casters, kinds, max_wait, shots):
    """
    Магии, особенности и комбинации по очереди у кастующего, второй смотрит. Обе стороны идут одновременно: снаряды хоста
    меряются по sent хоста и replayed гостя, снаряды гостя по sent гостя и replayed хоста, счётчики не пересекаются.
    Пункт закончен, как только все новые снаряды повторены или видна орбита; молчащий ждёт max_wait секунд.
    """
    say("== магии, особенности и комбинации: " + ", ".join("кастует " + ("хост" if c == "host" else "гость") for c in casters))
    calm_arena()
    for port in (HOST_PORT, GUEST_PORT):
        lab(port, "ResetMagic")
    items = magic_items(kinds)
    say(f"проверок {len(items)} на сторону")
    lock = threading.Lock()
    skew = {s: phone_time(s) - time.time() for s in (host, guest)}

    def run(caster):
        cp, op = (HOST_PORT, GUEST_PORT) if caster == "host" else (GUEST_PORT, HOST_PORT)
        cs, os_ = (host, guest) if caster == "host" else (guest, host)
        who, sees = ("хост", "гость") if caster == "host" else ("гость", "хост")
        folder = os.path.join(shots, caster) if shots else None
        if folder:
            os.makedirs(folder, exist_ok=True)
        for kind, num, name, spells_, abilities in items:
            start = time.time()
            batch(cp, ["ResetMagic", f"ToStart {0 if caster == 'host' else 1.5}"])
            batch(HOST_PORT, ["NoEnemies"])
            c0, o0 = coop(cp), coop(op)
            calls = [f"Spell {sp} 5" for sp in spells_ if sp]
            calls += [f"Combo {num} true"] if kind == "комбинация" else [f"Learn {a} 1" for a in abilities]
            granted = batch(cp, calls)
            not_given = [x for x in granted if re.search(r": (\d+) → \1$", x) or x.startswith(("ошибка", "нет ", "комбинации "))]
            sent0, rep0 = counts(c0.get("sent")), counts(o0.get("replayed"))
            limit = max(max_wait, 40) if 14 in spells_ else max_wait
            t0 = time.time()
            new, rep1, orbs, arma, o1 = {}, rep0, [], 0, o0
            while not not_given and time.time() - t0 < limit:
                time.sleep(1)
                c1, o1 = coop(cp), coop(op)
                sent1, rep1 = counts(c1.get("sent")), counts(o1.get("replayed"))
                new = {b: sent1[b] - sent0.get(b, 0) for b in sent1 if sent1[b] > sent0.get(b, 0)}
                orb = dict(x.split(":") for x in o1.get("partnerOrb", "").split(",") if ":" in x)
                orbs = [f"{k}={orb[k]}" for k in ("sh", "sa", "sb", "sp", "ez", "mc") if orb.get(k, "0") != "0"]
                arma = int(o1.get("armaPlayed", 0)) - int(o0.get("armaPlayed", 0))
                if (new and all(rep1.get(b, 0) > rep0.get(b, 0) for b in new)) or (orbs and not new) or arma:
                    break
            if folder:
                screenshot(os_, os.path.join(folder, f"{kind}_{num}_видит_{sees}.png"))
                screenshot(cs, os.path.join(folder, f"{kind}_{num}_у_{who}.png"))
            missed = [b for b in new if rep1.get(b, 0) <= rep0.get(b, 0)]
            fails = int(o1.get("castFails", 0)) - int(o0.get("castFails", 0))
            errors = unity_errors_since(cs, start + skew[cs]) + unity_errors_since(os_, start + skew[os_])
            what = f"[{who}→{sees}] {kind} {num} «{name}»"
            shown = f"снаряды {', '.join(f'{b}×{n}→{rep1.get(b, 0) - rep0.get(b, 0)}' for b, n in sorted(new.items())) or 'нет'}" + \
                    (f"; орбиты {', '.join(orbs)}" if orbs else "") + (f"; Армагеддон ×{arma}" if arma else "")
            with lock:
                if errors or fails:
                    check(False, f"{what}: ошибки повторов {fails}, исключений {len(errors)}: {'; '.join(sorted(set(errors))[:3])}")
                elif not_given:
                    say(f"НЕТ  {what}: не выдалось ({'; '.join(not_given)})")
                elif missed:
                    check(False, f"{what}: {sees} не повторил снаряды {missed} ({shown})")
                elif new or orbs or arma:
                    check(True, f"{what}: {shown}")
                else:
                    say(f"ИНФО {what}: без врагов ничего не пересылается")
        batch(cp, ["ResetMagic"])

    threads = [threading.Thread(target=run, args=(c,)) for c in casters]
    for t in threads:
        t.start()
    for t in threads:
        t.join()


def phone_time(serial):
    out = subprocess.run([ADB, "-s", serial, "shell", "date", "+%s"], capture_output=True, text=True).stdout.strip()
    return float(out) if out.isdigit() else time.time()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="07838C5001A00540")
    ap.add_argument("--guest", default="985AY130RJ")
    ap.add_argument("--out")
    ap.add_argument("--spells", default="", help="номера магий через запятую, по умолчанию все")
    ap.add_argument("--wait", type=float, default=6, help="секунд на магию (для magic — наибольшее ожидание)")
    ap.add_argument("--kinds", default="spells,traits,combos", help="для magic: spells, traits, combos")
    ap.add_argument("--shots", help="для magic: папка для скриншотов обоих")
    ap.add_argument("--caster", default="both", choices=["host", "guest", "both"], help="для magic: кто кастует")
    ap.add_argument("scenarios", nargs="+", choices=["mobs", "spells", "drop", "magic"])
    a = ap.parse_args()
    setup(a.host, a.guest)
    for name in a.scenarios:
        if name == "magic":
            magic(a.host, a.guest, ["host", "guest"] if a.caster == "both" else [a.caster], a.kinds.split(","), a.wait, a.shots)
        elif name == "spells":
            spells([int(x) for x in a.spells.split(",")] if a.spells else list(SPELL_NAMES), a.wait)
        else:
            {"mobs": mobs, "drop": drop}[name]()
    fails = sum(1 for r in report if r.startswith("FAIL"))
    say(f"ИТОГ: {sum(1 for r in report if r.startswith('PASS'))} PASS, {fails} FAIL")
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            f.write("\n".join(report) + "\n")
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
