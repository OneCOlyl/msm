#!/usr/bin/env python3
"""Проверки кооператива на двух телефонах через полигон coop_lab и консоль coop_debug.

Оба телефона подключены по USB, на обоих включены coop, coop_debug, coop_lab (и coop_cheat для магий), оба в бою.
Запуск:  python tools/lab.py [--host SERIAL] [--guest SERIAL] [--dir папка] [--out отчёт.txt] [--json итог.json] magic mobs chests drop perf
Скриншоты и итог (итог.md) — в папке --dir, по умолчанию dist/lab/<время>.
Код выхода 1 при любом провале, в том числе без телефонов.
"""
import argparse
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import threading
import time


def find_adb():
    if os.environ.get("ADB"):
        return os.environ["ADB"]
    return shutil.which("adb") or os.path.join(os.environ.get("LOCALAPPDATA", ""), "Android", "Sdk", "platform-tools", "adb.exe")


ADB = find_adb()
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LAB = "T:coop_lab:MagicSurvivalLab.Lab/Current"
HOST_PORT, GUEST_PORT = 8771, 8772
HOST_SERIAL, GUEST_SERIAL = "07838C5001A00540", "985AY130RJ"
SPELL_NAMES = {1: "Пуля", 2: "Огн. шар", 3: "Дух", 4: "Спутник", 5: "Лёд. иглы", 6: "Щит", 7: "Молния", 8: "Электрозона",
               9: "Цунами", 10: "Метеорит", 11: "Призрак", 12: "Вихрь", 13: "Электричество", 14: "Армагеддон",
               15: "Испепеление", 16: "Энергопуля", 17: "Метель", 18: "Луч", 19: "Маг. круг", 20: "Лав. поле", 21: "Вспышка"}
ORBITS = ("sh", "sa", "sb", "sp", "ez", "mc")
ARTIFACT_CHESTS = (75, 104)   # Артефакты, которые кладут выбравшему сундуки
SHOT_ORBITS = {"z": "ez"}         # Электрозону кастующего coop у друга показывает орбитой союзника, а не объектом зоны
PLACE_GAP = 7               # Куда отходит друг, пока меряется место снарядов
GAP = 2                     # Второй герой правее кастующего: оба в кадре (4 ед. уже за краем), повтор у союзника отличим от своего героя
PLACE_MARGIN = 0.5          # Насколько повтор должен быть ближе к ответу «у своего героя», чтобы это считалось ошибкой, ед.
DEATH_GAP = 1.0             # Враг у хоста и его копия у гостя должны погибнуть с разницей не больше, с
STILL_TOLERANCE = 0.3       # Копия стоящего врага на его месте, ед.
MOVE_TOLERANCE = 1.5        # Насколько копия идущего врага может отстать, ед.
MOVE_SEC = 3

ARTIFACT_CARD = re.compile(r"Canvas/Canvas_Artifact/Field_Artifact/\d+")
OBTAIN = "Canvas/Canvas_Artifact/Field_ArtifactSelect/Button_ArtifactSelect"
MERCHANT_OUT = "Canvas/Canvas_Merchant/Button_Merchant_OUT"
HUD = ("Canvas/ModKitHud_", "Canvas/Field_BattleUI/")

PERF_KEYS = ("frames", "fps", "frameAvg", "frameMax", "slowFrames", "netSec", "netTx", "netRx", "netTxMax", "netRxMax",
             "pingAvg", "pingMax", "pingSd", "pings")
report = []
results = []
summary = {"magic": [], "mobs": [], "chests": []}
serials = {}
out_dir = ""


class Abort(Exception):
    pass


def say(line):
    print(line, flush=True)
    report.append(line)


def begin(name):
    results.append({"name": name, "passed": True, "checks": [], "numbers": {}})


def note(kind, what):
    """НЕТ и ИНФО: не провал мода, но в итоге видно."""
    say(f"{kind} {what}")
    if results:
        results[-1]["checks"].append({"ok": None, "text": what})


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


def lab_lines(port, call):
    """Список от полигона. Ошибка полигона прерывает сценарий: иначе она выглядела бы как пустой список."""
    raw = lab(port, call)
    if raw.startswith("ошибка"):
        raise Abort(f"{call}: {raw}")
    return raw.splitlines()


def coop(port):
    return dict(kv.split("=", 1) for kv in lab(port, "CoopStatus").split("; ") if "=" in kv)


def check(ok, what):
    say(("PASS " if ok else "FAIL ") + what)
    if results:
        results[-1]["checks"].append({"ok": bool(ok), "text": what})
        results[-1]["passed"] &= bool(ok)
    return ok


def connected_serials():
    try:
        out = subprocess.run([ADB, "devices"], capture_output=True, text=True, timeout=30).stdout
    except (OSError, subprocess.TimeoutExpired) as e:
        raise Abort(f"adb не запускается ({ADB}): {e}. Укажите путь в переменной ADB или добавьте adb в PATH") from e
    return {ln.split()[0] for ln in out.splitlines()[1:] if ln.strip().endswith("\tdevice")}


def setup(host, guest):
    have = connected_serials()
    for role, serial in (("хост", host), ("гость", guest)):
        if serial not in have:
            raise Abort(f"{role}: телефон {serial} не подключён по adb (подключены: {', '.join(sorted(have)) or 'нет'})")
    serials.update(host=host, guest=guest)
    for serial, port in ((host, HOST_PORT), (guest, GUEST_PORT)):
        r = subprocess.run([ADB, "-s", serial, "forward", f"tcp:{port}", "tcp:8770"], capture_output=True, text=True)
        if r.returncode:
            raise Abort(f"adb forward на {serial} не вышел: {r.stderr.strip()}")
    try:
        h, g = coop(HOST_PORT), coop(GUEST_PORT)
    except OSError as e:
        raise Abort(f"консоль coop_debug не отвечает ({e}): игра запущена, coop_debug включён?") from e
    if h.get("coop") != "1" or g.get("coop") != "1":
        raise Abort("нет мода coop или coop_lab на одном из телефонов")
    if h.get("connected") != "True" or g.get("connected") != "True":
        raise Abort("телефоны не на связи: в окне «Кооп» создайте игру у хоста и подключите друга")
    if h.get("hosting") != "True":
        raise Abort("первый телефон (--host) не хост")
    if h.get("inRun") != "True" or g.get("inRun") != "True":
        raise Abort("оба должны быть в бою")
    for port in (HOST_PORT, GUEST_PORT):
        lab(port, "SetImmortal true")
        lab(port, "SetMana false")
        for i in SPELL_NAMES:                                   # Без магий вызванные мобы доживут до проверки
            lab(port, f"Spell {i} 0")
    say("настройка: " + lab(HOST_PORT, "SetSpawn false") + ", " + lab(HOST_PORT, "ClearEnemies") + ", у обоих бессмертие, без опыта и без магий")


def screenshot(serial, path):
    with open(path, "wb") as f:
        f.write(subprocess.run([ADB, "-s", serial, "exec-out", "screencap", "-p"], capture_output=True).stdout)


def snap(folder, name):
    """Скриншоты обоих телефонов сразу, в фоне: проверка идёт дальше. Вернёт потоки, их нужно дождаться."""
    os.makedirs(folder, exist_ok=True)
    ts = [threading.Thread(target=screenshot, args=(serials[role], os.path.join(folder, f"{name}_{role}.png"))) for role in ("host", "guest")]
    for t in ts:
        t.start()
    return ts


def calm_arena():
    """Оба героя на своих точках старта (то есть рядом), врагов нет, часы стоят: босс-волна не начнётся."""
    lab(HOST_PORT, "SetSpawn false")
    for port in (HOST_PORT, GUEST_PORT):
        lab(port, "HoldClock true")
    heroes_home()
    lab(HOST_PORT, "NoEnemies")


def heroes_home():
    lab(HOST_PORT, "ToStart 0")
    lab(GUEST_PORT, f"ToStart {GAP}")


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


def phone_time(serial):
    out = subprocess.run([ADB, "-s", serial, "shell", "date", "+%s"], capture_output=True, text=True).stdout.strip()
    return float(out) if out.isdigit() else time.time()


def buttons(port):
    res = []
    for ln in lab(port, "Buttons").splitlines():
        parts = ln.split("|", 5)
        if len(parts) == 6 and parts[0].isdigit():
            res.append({"i": int(parts[0]), "path": parts[1], "x": float(parts[2]), "y": float(parts[3]),
                        "on": parts[4] == "True", "text": re.sub(r"<[^>]+>", "", parts[5])})
    return [b for b in res if not b["path"].startswith(HUD)]


def in_reading_order(bs, pattern):
    """
    Сверху вниз, в ряду слева направо. Номера карт в окнах игры идут в этом порядке, а координаты во время анимации
    открытия окна ещё не на месте.
    """
    return sorted((b for b in bs if pattern.fullmatch(b["path"])), key=lambda b: int(b["path"].rsplit("/", 1)[1]))


def close_artifact(port, wait=8):
    """Окно сундука: первая карта и «взять»; лавка торговца: выход. Пока окно открыто, общая пауза держит обоих."""
    t0 = time.time()
    while time.time() - t0 < wait:
        on = {b["path"]: b for b in buttons(port) if b["on"]}
        if MERCHANT_OUT in on:
            lab(port, f"Click {on[MERCHANT_OUT]['i']} {MERCHANT_OUT}")
            time.sleep(1)
            continue
        if OBTAIN in on:
            lab(port, f"Click {on[OBTAIN]['i']} {OBTAIN}")
            time.sleep(1)
            continue
        cards = in_reading_order(on.values(), ARTIFACT_CARD)
        if cards:
            lab(port, f"Click {cards[0]['i']} {cards[0]['path']}")
            time.sleep(0.5)
            continue
        if time.time() - t0 > 2:                        # Окно открывается не сразу после подбора
            return True
        time.sleep(0.3)
    return False


def counts(raw):
    return {} if raw in (None, "0", "нет") or raw.startswith("ошибка") else {int(k): int(v) for k, v in (x.split(":") for x in raw.split(","))}


def base_spell(ability):
    return (ability - 361) // 9 + 1 if 361 <= ability < 361 + 9 * 21 else None


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


# Магии

def looks(port):
    """Живые снаряды: {(вид, префаб, спрайт): (число, масштаб, до героя, до союзника)}."""
    res = {}
    for ln in lab_lines(port, "Looks"):
        p = ln.split("|")
        if len(p) == 7:
            res[(p[0], p[1], p[2])] = (int(p[3]), float(p[4]), float(p[5]), float(p[6]))
    return res


def orbits(raw):
    return {k: v for k, v in (x.split(":") for x in (raw or "").split(",") if ":" in x)}


class Seen:
    """Что видно на телефоне за время пункта: по каждому виду снаряда сколько раз попался, наибольшее число, расстояния."""

    def __init__(self):
        self.looks = {}

    def add(self, sample):
        for k, (c, scale, d, a) in sample.items():
            n, m, s, sd, sa = self.looks.get(k, (0, 0, 0.0, 0.0, 0.0))
            self.looks[k] = (n + 1, max(m, c), max(s, scale), sd + d, sa + a)

    def steady(self):
        """Виды, попавшиеся хотя бы в двух замерах: снаряд, мелькнувший на один кадр, повтор у друга мог не застать."""
        return {k: v for k, v in self.looks.items() if v[0] >= 2}

    def kinds(self, steady=False):
        res = {}
        for k, v in (self.steady() if steady else self.looks).items():
            res.setdefault(k[0], []).append((k, v))
        return res


def mean(looks, i):
    return sum(v[i] for _, v in looks) / sum(v[0] for _, v in looks)


def describe(seen, extra=""):
    if not seen.looks and not extra:
        return "ничего"
    parts = [f"{k[0]} {k[1]}/{k[2]} ×{v[1]}" + (f" (масштаб {v[2]:g})" if v[2] and abs(v[2] - 1) > 0.01 else "")
             for k, v in sorted(seen.looks.items())]
    return ", ".join(parts + ([extra] if extra else []))


def cast_once(caster, item, max_wait, after, folder):
    """
    Один пункт у одного кастующего. Пока магия появляется, второй герой стоит в GAP правее: скриншот с обоими. Потом он
    отходит на PLACE_GAP, и место снарядов меряется уже там: в GAP снаряд почти одинаково близок к обоим героям.
    """
    kind, num, name, spells_, abilities = item
    cp, op = (HOST_PORT, GUEST_PORT) if caster == "host" else (GUEST_PORT, HOST_PORT)
    batch(HOST_PORT, ["NoEnemies"])
    batch(cp, ["ResetMagic", "ToStart 0"])
    batch(op, ["ResetMagic", f"ToStart {GAP}"])
    time.sleep(1.5)                                     # остатки прошлого пункта гаснут
    start = time.time()
    o0, c0 = coop(op), coop(cp)
    calls = [f"Spell {sp} 5" for sp in spells_ if sp]
    calls += [f"Combo {num} true"] if kind == "комбинация" else [f"Learn {a} 1" for a in abilities]
    granted = batch(cp, calls)
    not_given = [x for x in granted if re.search(r": (\d+) → \1$", x) or x.startswith(("ошибка", "нет ", "комбинации "))]
    own, other, own_far, other_far = Seen(), Seen(), Seen(), Seen()
    orb0 = orbits(o0.get("partnerOrb"))
    orbs, arma, shots, first, moved = [], 0, [], None, None
    limit = max(max_wait, 40) if 14 in spells_ else max_wait
    while not not_given:
        a, b = looks(cp), looks(op)
        own.add(a)
        other.add(b)
        now = time.time()
        if moved and now - moved > 0.7:                 # союзник у кастующего доехал за другом
            own_far.add(a)
            other_far.add(b)
        o1 = coop(op)
        orb = orbits(o1.get("partnerOrb"))
        orbs = [f"{k}={orb[k]}" for k in ORBITS if orb.get(k, "0") not in ("0", orb0.get(k, "0"))]
        arma = int(o1.get("armaPlayed", 0)) - int(o0.get("armaPlayed", 0))
        if first is None and (own.looks or orbs or arma):
            first = now
            shots = snap(folder, f"{kind}_{num}_{caster}")
        if first is not None and moved is None and not any(t.is_alive() for t in shots):
            batch(op, [f"ToStart {PLACE_GAP}"])
            moved = time.time()
        if (first is None and now - start > limit) or (moved is not None and now - moved > after):
            break
        time.sleep(0.3)
    for t in shots:
        t.join()
    c1, o1 = coop(cp), coop(op)
    sent = {b: n - counts(c0.get("sent")).get(b, 0) for b, n in counts(c1.get("sent")).items() if n > counts(c0.get("sent")).get(b, 0)}
    rep0, rep1 = counts(o0.get("replayed")), counts(o1.get("replayed"))
    errors = unity_errors_since(serials["host"], start + skew["host"]) + unity_errors_since(serials["guest"], start + skew["guest"])
    return {"not_given": not_given, "own": own, "other": other, "own_far": own_far, "other_far": other_far, "orbs": orbs, "arma": arma,
            "missed": [b for b in sent if rep1.get(b, 0) <= rep0.get(b, 0)],
            "fails": int(o1.get("castFails", 0)) - int(o0.get("castFails", 0)), "errors": errors,
            "shot": bool(shots)}


def judge_cast(item, caster, r):
    """Проверки одного направления. Вернёт текст для итога."""
    kind, num, name = item[:3]
    who, sees = ("хост", "гость") if caster == "host" else ("гость", "хост")
    what = f"[{who}→{sees}] {kind} {num} «{name}»"
    extra = ", ".join(r["orbs"] + ([f"Армагеддон ×{r['arma']}"] if r["arma"] else []))
    shown = f"у {who}: {describe(r['own'])}; у {sees}: {describe(r['other'], extra)}"
    if r["not_given"]:
        note("НЕТ ", f"{what}: не выдалось ({'; '.join(r['not_given'])})")
        return "не выдалось"
    if r["errors"] or r["fails"]:
        check(False, f"{what}: ошибки повторов {r['fails']}, исключений {len(r['errors'])}: {'; '.join(sorted(set(r['errors']))[:3])}")
        return shown
    if not r["own"].looks and not extra:
        note("ИНФО", f"{what}: без врагов не вызвалась")
        return "не вызвалась без врагов"
    mine, theirs = r["own"].kinds(steady=True), r["other"].kinds()
    problems = []
    shown_as_orbit = {k for k, o in SHOT_ORBITS.items() if any(x.startswith(o + "=") for x in r["orbs"])}
    missing = [k for k in mine if k not in theirs and k not in shown_as_orbit]
    if missing:
        problems.append(f"у {sees} нет {', '.join(missing)}")
    for k in mine:
        if k not in theirs:
            continue
        a = {x[0][1:] for x in mine[k]}
        b = {x[0][1:] for x in theirs[k]}
        if not a & b:
            problems.append(f"{k} выглядит иначе: {sorted(a)} → {sorted(b)}")
    # Друг отошёл на PLACE_GAP. У кастующего снаряд в dc от героя и в ac от союзника-друга, у друга повтор должен быть в dc от
    # союзника и в ac от своего героя. Средние разбросаны на ед. и больше, поэтому сравниваются два ответа
    far_mine, far_theirs = r["own_far"].kinds(), r["other_far"].kinds()
    for k in mine:
        if k not in far_mine or k not in far_theirs:
            continue
        dc, ac = mean(far_mine[k], 3), mean(far_mine[k], 4)
        do, ao = mean(far_theirs[k], 3), mean(far_theirs[k], 4)
        if min(dc, ac, do, ao) >= 0:
            at_ally, at_own = abs(ao - dc) + abs(do - ac), abs(do - dc) + abs(ao - ac)
            if at_own + PLACE_MARGIN < at_ally:
                problems.append(f"{k} у героя друга, а не у союзника: у кастующего {dc:.1f} от героя и {ac:.1f} от союзника, "
                                f"у друга {ao:.1f} от союзника и {do:.1f} от своего героя")
    if r["missed"]:
        problems.append(f"{sees} не повторил снаряды {r['missed']}")
    check(not problems, f"{what}: {shown}" + (f"; {'; '.join(problems)}" if problems else ""))
    return shown


def magic(casters, kinds, max_wait, after):
    """
    Магии, особенности и комбинации по одной: кастует хост, потом гость, второй стоит рядом. Врагов нет, на скриншотах только магия.
    У кастующего и у друга сравниваются вид снаряда (номер, префаб, спрайт без номера кадра) и место: у друга снаряд
    должен быть у союзника. Свои снаряды хоста и гостя на одной магии должны совпасть.
    """
    begin("magic")
    say("== магии, особенности и комбинации без врагов: " + ", ".join("кастует " + ("хост" if c == "host" else "гость") for c in casters))
    calm_arena()
    items = magic_items(kinds)
    say(f"пунктов {len(items)}")
    folder = os.path.join(out_dir, "magic")
    for item in items:
        res = {}
        for caster in casters:
            res[caster] = cast_once(caster, item, max_wait, after, folder)
        row = {"item": f"{item[0]} {item[1]} «{item[2]}»"}
        for caster in casters:
            row[caster] = judge_cast(item, caster, res[caster])
        if len(casters) == 2 and all(res[c]["own"].looks and not res[c]["not_given"] for c in casters):
            # Редкий снаряд (метеорит) может не успеть упасть у одного за время пункта: это не разница в виде
            h, g = res["host"]["own"].kinds(), res["guest"]["own"].kinds()
            differ = [f"{k}: у хоста {sorted({x[0][1:] for x in h[k]})}, у гостя {sorted({x[0][1:] for x in g[k]})}"
                      for k in h if k in g and not {x[0][1:] for x in h[k]} & {x[0][1:] for x in g[k]}]
            check(not differ, f"{row['item']}: своя магия у хоста и у гостя выглядит одинаково" + (f" — {'; '.join(differ)}" if differ else ""))
            only = sorted(set(h) ^ set(g))
            if only:
                note("ИНФО", f"{row['item']}: только у одного успели появиться {', '.join(only)}")
            row["same"] = "нет" if differ else "да" + (f" (только у одного: {', '.join(only)})" if only else "")
        summary["magic"].append(row)
    for port in (HOST_PORT, GUEST_PORT):
        batch(port, ["ResetMagic", "ToStart 0" if port == HOST_PORT else f"ToStart {GAP}"])


# Мобы

def mob_list(port):
    res = []
    for ln in lab_lines(port, "Mobs"):
        p = ln.split("|")
        if len(p) == 9:
            res.append({"id": int(p[0]), "type": int(p[1]), "kind": p[2], "prefab": p[3], "sprite": p[4], "scale": float(p[5]),
                        "back": p[6] == "1", "x": float(p[7]), "y": float(p[8])})
    return res


def both_mobs():
    """Враги хоста и копии гостя, снятые одновременно: {id: моб}."""
    res = {}

    def get(port, kind):
        try:
            res[kind] = {m["id"]: m for m in mob_list(port) if m["kind"] == kind and m["id"] >= 0}
        except (Abort, OSError) as e:
            res[kind] = e
    ts = [threading.Thread(target=get, args=(HOST_PORT, "own")), threading.Thread(target=get, args=(GUEST_PORT, "copy"))]
    for t in ts:
        t.start()
    for t in ts:
        t.join()
    for v in res.values():
        if isinstance(v, Exception):
            raise v
    return res["own"], res["copy"]


def mob_text(m):
    return f"{m['type']} {m['sprite']}" + (f" ×{m['scale']:g}" if m["scale"] else "") + (" с обводкой" if m["back"] else "")


def group(ms):
    n = {}
    for m in ms:
        n[mob_text(m)] = n.get(mob_text(m), 0) + 1
    return ", ".join(f"{k} — {v}" for k, v in sorted(n.items())) or "нет"


def dist(a, b):
    return ((a["x"] - b["x"]) ** 2 + (a["y"] - b["y"]) ** 2) ** 0.5


def wait_gone(ids, port, own, timeout=5):
    """Когда каждый из ids пропал из списка врагов телефона: {id: секунды от вызова}."""
    t0, gone = time.time(), {}
    while time.time() - t0 < timeout and len(gone) < len(ids):
        alive = {m["id"] for m in mob_list(port) if (m["kind"] == "own") == own}
        for i in ids:
            if i not in alive and i not in gone:
                gone[i] = time.time() - t0
        time.sleep(0.05)
    return gone


def mobs():
    """
    Мобы хоста у гостя. Сначала мобы стоят: копия должна быть на месте врага хоста и выглядеть так же (тип, спрайт без
    номера кадра, масштаб на экране, обводка особых), скриншоты обоих сравнимы. Потом мобы идут MOVE_SEC секунд: насколько
    копия отстаёт от врага. Потом смерть: хост убивает двух, гость ранит двух копий.
    """
    begin("mobs")
    say("== мобы хоста у гостя: появление, вид, место, движение, смерть")
    calm_arena()
    folder = os.path.join(out_dir, "mobs")
    cases = [(f"тип {t}", f"Spawn {t} 4") for t in (1, 6, 11, 13, 16, 25, 41, 50)]
    cases += [("эпические", "SpawnEpic 6 5"), ("особые", "SpawnSpecial 5"), ("переход", "Transition")]
    for name, call in cases:
        lab(HOST_PORT, "NoEnemies")
        lab(HOST_PORT, "FreezeEnemies true")
        heroes_home()                                   # идущие мобы прошлого случая толкают героев
        time.sleep(2)
        made = lab(HOST_PORT, call)
        t0 = time.time()
        host, guest = {}, {}
        appear, ids, stable_at = None, set(), t0
        # Переход и особые игра создаёт по одному: сравнивать, когда новых у хоста нет секунду
        while time.time() - t0 < 10:
            time.sleep(0.3)
            host, guest = both_mobs()
            if set(host) != ids:
                ids, stable_at = set(host), time.time()
            if host and ids <= set(guest):
                appear = appear or time.time() - t0
                if time.time() - stable_at >= 1:
                    break
        appear = appear if appear is not None else time.time() - t0
        row = {"case": name, "host": group(host.values()), "guest": group(guest.values()), "appear": f"{appear:.1f}"}
        summary["mobs"].append(row)
        if not host:
            note("НЕТ ", f"{name}: у хоста «{made}», но через 10 с врагов нет")
            row.update(place="—", move="—", death="—")
            continue
        time.sleep(1)                                   # копии доезжают до места стоящих врагов
        host, guest = both_mobs()
        for t in snap(folder, name.replace(" ", "_")):
            t.join()
        missing = [i for i in host if i not in guest]
        differ = [f"{i}: {mob_text(m)} → {mob_text(guest[i])}" for i, m in host.items() if i in guest and (
            m["type"] != guest[i]["type"] or m["sprite"] != guest[i]["sprite"] or m["back"] != guest[i]["back"]
            or (m["scale"] and abs(guest[i]["scale"] / m["scale"] - 1) > 0.15))]
        check(not missing and not differ, f"{name}: у хоста «{made}» — {group(host.values())}; у гостя за {appear:.1f} с — {group(guest.values())}" +
              (f"; у гостя нет {missing}" if missing else "") + (f"; иначе выглядят: {'; '.join(differ)}" if differ else ""))
        off = {i: dist(m, guest[i]) for i, m in host.items() if i in guest}
        row["place"] = f"до {max(off.values()):.2f} ед." if off else "—"
        far = [f"{i}: {d:.2f}" for i, d in off.items() if d > STILL_TOLERANCE]
        check(not far, f"{name}: стоящие копии на месте врагов хоста, расхождение до {max(off.values(), default=0):.2f} ед." +
              (f"; дальше {STILL_TOLERANCE}: {', '.join(far)}" if far else ""))
        row["move"] = movement(name)
        both = [i for i in host if i in guest]
        row["death"] = deaths(name, both[:2], both[2:4])
    lab(HOST_PORT, "FreezeEnemies false")
    lab(HOST_PORT, "NoEnemies")


def movement(name):
    """Мобы идут к героям: на сколько копия у гостя отстаёт от врага хоста."""
    lab(HOST_PORT, "FreezeEnemies false")
    gaps = []
    t0 = time.time()
    while time.time() - t0 < MOVE_SEC:
        host, guest = both_mobs()
        gaps += [dist(m, guest[i]) for i, m in host.items() if i in guest]
        time.sleep(0.3)
    lab(HOST_PORT, "FreezeEnemies true")
    if not gaps:
        note("ИНФО", f"{name}: в движении сравнить нечего")
        return "—"
    avg, top = sum(gaps) / len(gaps), max(gaps)
    check(top <= MOVE_TOLERANCE, f"{name}: в движении копия от врага хоста в среднем {avg:.2f} ед., до {top:.2f} ед. (замеров {len(gaps)})")
    return f"в среднем {avg:.2f}, до {top:.2f} ед."


def deaths(name, by_host, by_guest):
    texts = []
    if by_host:
        t0 = time.time()
        for i in by_host:
            lab(HOST_PORT, f"KillEnemy {i}")
        shift = time.time() - t0
        g = wait_gone(by_host, GUEST_PORT, own=False)
        late = [g.get(i) for i in by_host]
        ok = all(x is not None and x + shift <= DEATH_GAP for x in late)
        text = "хост убил: у гостя пропали через " + ", ".join("—" if x is None else f"{x + shift:.2f} с" for x in late)
        check(ok, f"{name}: {text}")
        texts.append(text)
    if by_guest:
        for i in by_guest:
            lab(GUEST_PORT, f"HurtEnemy {i}")
        res = {}

        def watch(port, own):
            res[own] = wait_gone(by_guest, port, own)
        ts = [threading.Thread(target=watch, args=(HOST_PORT, True)), threading.Thread(target=watch, args=(GUEST_PORT, False))]
        for t in ts:
            t.start()
        for t in ts:
            t.join()
        pairs = [(res[True].get(i), res[False].get(i)) for i in by_guest]
        ok = all(h is not None and g is not None and abs(g - h) <= DEATH_GAP for h, g in pairs)
        text = "гость ранил: " + ", ".join(f"у хоста {'—' if h is None else f'{h:.2f} с'}, у гостя {'—' if g is None else f'{g:.2f} с'}" for h, g in pairs)
        check(ok, f"{name}: {text}")
        texts.append(text)
    return "; ".join(texts) or "—"


# Сундуки

def chest_list(port):
    res = []
    for ln in lab_lines(port, "Chests"):
        p = ln.split("|")
        if len(p) == 5:
            res.append((int(p[0]), float(p[1]), float(p[2]), float(p[3]), float(p[4])))
    return res


def near(chests, kind, rx, ry, r=0.6):
    return next((c for c in chests if c[0] == kind and abs(c[1] - rx) <= r and abs(c[2] - ry) <= r), None)


def wait_for(fn, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        v = fn()
        if v:
            return v, time.time() - t0
        time.sleep(0.2)
    return None, None


def chests():
    """Общие сундуки: хост кладёт сундук, он появляется у гостя; подбирает хост, потом гость, сундук пропадает у обоих."""
    begin("chests")
    say("== общие сундуки")
    if coop(HOST_PORT).get("chestsShared") != "True":
        note("НЕТ ", "общие сундуки выключены: включите их у хоста в лобби (настройка chests)")
        return
    calm_arena()
    for kind, dy, picker in ((5, 2, "host"), (6, -2, "guest"), (7, 3.5, "guest")):
        chest_case(kind, dy, picker, os.path.join(out_dir, "chests"))
    for artifact in ARTIFACT_CHESTS:
        for chooser in ("host", "guest"):
            artifact_case(artifact, chooser, os.path.join(out_dir, "chests"))


def lying(port):
    return {(c[0], round(c[1], 1), round(c[2], 1)) for c in chest_list(port)}


def artifact_case(artifact, chooser, folder):
    """
    Артефакт, который кладёт сундуки, выбирает chooser. Все сундуки общие: сундуки от артефакта должны лечь у обоих на одних
    местах и лежать дальше. Выбрал гость: его игра кладёт их у себя, coop отдаёт хосту, и они приходят гостю копиями.
    """
    who = "хост" if chooser == "host" else "гость"
    row = {"case": f"артефакт {artifact} выбрал {who}"}
    summary["chests"].append(row)
    heroes_home()
    clear_chests()
    cp = HOST_PORT if chooser == "host" else GUEST_PORT
    made = lab(HOST_PORT, f"Chest 5 {0 if chooser == 'host' else GAP} -1.5")
    box, _ = wait_for(lambda: near(chest_list(cp), 5, 0 if chooser == "host" else GAP, -1.5, r=1), 5)
    if not box:
        note("НЕТ ", f"{row['case']}: сундук для окна артефакта не лёг у {who} («{made}»)")
        row.update(host="—", guest="—", pick="—")
        return
    before = {HOST_PORT: lying(HOST_PORT), GUEST_PORT: lying(GUEST_PORT)}
    lab(cp, f"GoTo {box[3]} {box[4]}")
    cards, _ = wait_for(lambda: in_reading_order([b for b in buttons(cp) if b["on"]], ARTIFACT_CARD), 5)
    if not cards:
        note("НЕТ ", f"{row['case']}: окно сундука не открылось")
        row.update(host="—", guest="—", pick="окно не открылось")
        return
    picked = lab(cp, f"ArtifactPick {artifact}")
    closed = close_artifact(cp)
    time.sleep(5)                                       # гость отдаёт хосту, хост кладёт и рассылает, гость кладёт копию
    host = sorted(lying(HOST_PORT) - before[HOST_PORT])
    guest = sorted(lying(GUEST_PORT) - before[GUEST_PORT])
    for th in snap(folder, f"артефакт_{artifact}_{chooser}"):
        th.join()
    time.sleep(3)
    still_h, still_g = lying(HOST_PORT), lying(GUEST_PORT)

    def text(items):
        return ", ".join(f"вид {k} ({x:g}; {y:g})" for k, x, y in items) or "нет"

    def matched(xs, ys):
        return [x for x in xs if not any(y[0] == x[0] and abs(y[1] - x[1]) <= 0.3 and abs(y[2] - x[2]) <= 0.3 for y in ys)]
    row["host"] = f"новых {len(host)}: {text(host)}"
    row["guest"] = f"новых {len(guest)}: {text(guest)}"
    row["pick"] = picked + ("" if closed else "; окно не закрылось")
    if not host and not guest:
        note("ИНФО", f"{row['case']}: артефакт сундуков не положил ({picked})")
        clear_chests()
        return
    only_h, only_g = matched(host, guest), matched(guest, host)
    gone = [c for c in host if c not in still_h] + [c for c in guest if c not in still_g]
    check(not only_h and not only_g and not gone,
          f"{row['case']}: у хоста {row['host']}; у гостя {row['guest']}" +
          (f"; только у хоста {text(only_h)}" if only_h else "") + (f"; только у гостя {text(only_g)}" if only_g else "") +
          (f"; пропали через 3 с: {text(gone)}" if gone else ""))
    clear_chests()


def clear_chests():
    """Без подбора: подбор открыл бы окно, а случайный артефакт положил бы новые сундуки. Сначала хост: копии у гостя уйдут сами."""
    lab(HOST_PORT, "ClearChests")
    time.sleep(1.5)
    lab(GUEST_PORT, "ClearChests")


def chest_case(kind, dy, picker, folder):
    who = "хост" if picker == "host" else "гость"
    row = {"case": f"сундук {kind}, берёт {who}"}
    summary["chests"].append(row)
    before = {(c[0], round(c[1]), round(c[2])) for c in chest_list(HOST_PORT)}
    made = lab(HOST_PORT, f"Chest {kind} 1.5 {dy}")
    mine, _ = wait_for(lambda: next((c for c in chest_list(HOST_PORT) if c[0] == kind and (c[0], round(c[1]), round(c[2])) not in before), None), 3)
    if not mine:
        note("НЕТ ", f"сундук {kind}: у хоста «{made}», но сундука нет")
        row.update(host="не лёг", guest="—", pick="—")
        return
    copy, t = wait_for(lambda: near(chest_list(GUEST_PORT), kind, mine[1], mine[2]), 5)
    for th in snap(folder, f"сундук_{kind}_появился"):
        th.join()
    row["host"] = f"лёг в ({mine[1]:g}; {mine[2]:g}) от старта"
    row["guest"] = f"появился через {t:.1f} с в ({copy[1]:g}; {copy[2]:g})" if copy else "не появился"
    if not check(copy is not None, f"сундук {kind}: у хоста {row['host']}, у гостя {row['guest']}"):
        row["pick"] = "—"
        lab(HOST_PORT, f"GoTo {mine[3]} {mine[4]}")
        close_artifact(HOST_PORT)
        return
    port, target = (HOST_PORT, mine) if picker == "host" else (GUEST_PORT, copy)
    lab(port, f"GoTo {target[3]} {target[4]}")
    t0, gone = time.time(), {}
    while time.time() - t0 < 5 and len(gone) < 2:
        for role, p in (("host", HOST_PORT), ("guest", GUEST_PORT)):
            if role not in gone and near(chest_list(p), kind, mine[1], mine[2]) is None:
                gone[role] = time.time() - t0
        time.sleep(0.2)
    for th in snap(folder, f"сундук_{kind}_взял_{picker}"):
        th.join()
    closed = close_artifact(port)
    row["pick"] = ", ".join(f"у {r} {'пропал через %.1f с' % gone[k] if k in gone else 'НЕ пропал'}" for k, r in (("host", "хоста"), ("guest", "гостя"))) + \
        ("" if closed else "; окно сундука не закрылось")
    check(len(gone) == 2, f"сундук {kind}, берёт {who}: {row['pick']}")
    lab(port, "ToStart 0" if port == HOST_PORT else f"ToStart {GAP}")


# Связь и нагрузка

def drop():
    begin("drop")
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


def puppets(port):
    raw = coop(port).get("puppets", "0")
    return {} if raw in ("0", "нет") else {k: int(v) for k, v in (x.split(":") for x in raw.split(","))}


def enemy_count():
    m = re.search(r"врагов (\d+)", lab(HOST_PORT, "Status"))
    return int(m.group(1)) if m else 0


def top_up(want, kind):
    """Магии на максимуме выбивают врагов: добор до want и большое здоровье."""
    for _ in range(10):
        n = enemy_count()
        if n >= want:
            break
        lab(HOST_PORT, f"SpawnTough {kind} {min(50, want - n)} 1000000")
    return enemy_count()


def perf(seconds, want, kind):
    """Нагрузка: спокойная арена, want врагов, все магии на максимум у обоих; итог кадра и сети за seconds секунд."""
    begin("perf")
    say(f"== нагрузка: {want} врагов, все магии на максимум, {seconds:.0f} с")
    calm_arena()
    for port, role in ((HOST_PORT, "хост"), (GUEST_PORT, "гость")):
        say(f"{role}: {lab(port, 'MaxMagic')}")
    top_up(want, kind)
    for port in (HOST_PORT, GUEST_PORT):
        lab(port, "CoopProfileReset")
    t0, seen = time.time(), []
    while time.time() - t0 < seconds:
        time.sleep(min(5, max(0.0, seconds - (time.time() - t0))))
        seen.append(top_up(want, kind))
    numbers = {}
    for port, role in ((HOST_PORT, "host"), (GUEST_PORT, "guest")):
        st = coop(port)
        numbers[role] = {k: float(st[k]) for k in PERF_KEYS if k in st}
        numbers[role]["puppets"] = sum(puppets(port).values()) if role == "guest" else None
    numbers["enemiesAvg"] = round(sum(seen) / len(seen), 1) if seen else 0
    results[-1]["numbers"] = numbers
    say(f"врагов у хоста в среднем {numbers['enemiesAvg']}")
    say("| | " + " | ".join(PERF_KEYS) + " |")
    say("|---|" + "---|" * len(PERF_KEYS))
    for role in ("host", "guest"):
        say(f"| {role} | " + " | ".join(f"{numbers[role][k]:g}" if k in numbers[role] else "—" for k in PERF_KEYS) + " |")
    for role, name in (("host", "хост"), ("guest", "гость")):
        n = numbers[role]
        check(n.get("frames", 0) > 0, f"{name}: кадров {n.get('frames', 0):g}, средний кадр мода {n.get('frameAvg', 0):g} мс")
        check(n.get("netTx", 0) > 0 and n.get("netRx", 0) > 0, f"{name}: сеть {n.get('netTx', 0):g} Б/с отправка, {n.get('netRx', 0):g} Б/с приём")
        check(max(n.get("netTxMax", 0), n.get("netRxMax", 0)) < 16384, f"{name}: самая длинная строка {max(n.get('netTxMax', 0), n.get('netRxMax', 0)):g} Б из 16384")
    for port in (HOST_PORT, GUEST_PORT):
        lab(port, "ResetMagic")
    lab(HOST_PORT, "NoEnemies")


# Итог

def cell(s):
    return str(s).replace("|", "/").replace("\n", " ")


def write_summary(path, error):
    lines = ["# Итог проверки кооператива", ""] + ([f"**Прервано:** {error}", ""] if error else [])
    if summary["magic"]:
        lines += ["## Магии", "", "Скриншоты: `magic/<пункт>_<кто кастует>_<телефон>.png`.", "",
                  "| Пункт | Вызвал хост: у хоста и у гостя | Вызвал гость: у гостя и у хоста | Одинаково |", "|---|---|---|---|"]
        lines += [f"| {cell(r['item'])} | {cell(r.get('host', '—'))} | {cell(r.get('guest', '—'))} | {r.get('same', '—')} |" for r in summary["magic"]]
        lines.append("")
    if summary["mobs"]:
        lines += ["## Мобы", "", "Скриншоты: `mobs/<случай>_<телефон>.png`, мобы стоят.", "",
                  "| Случай | Вызвано у хоста | Показано у гостя | Появление, с | Место стоящих | Отставание в движении | Смерть |",
                  "|---|---|---|---|---|---|---|"]
        lines += [f"| {cell(r['case'])} | {cell(r['host'])} | {cell(r['guest'])} | {r['appear']} | {cell(r.get('place', '—'))} | "
                  f"{cell(r.get('move', '—'))} | {cell(r.get('death', '—'))} |" for r in summary["mobs"]]
        lines.append("")
    if summary["chests"]:
        lines += ["## Сундуки", "", "Скриншоты: `chests/сундук_<вид>_появился_<телефон>.png`, `chests/сундук_<вид>_взял_<кто>_<телефон>.png`.", "",
                  "| Случай | У хоста | У гостя | Подбор |", "|---|---|---|---|"]
        lines += [f"| {cell(r['case'])} | {cell(r.get('host', '—'))} | {cell(r.get('guest', '—'))} | {cell(r.get('pick', '—'))} |" for r in summary["chests"]]
        lines.append("")
    fails = [r for r in report if r.startswith("FAIL")]
    lines += ["## Провалы", ""] + ([f"- {cell(r[5:])}" for r in fails] or ["нет"]) + [""]
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))


skew = {}


def run_scenarios(a):
    setup(a.host, a.guest)
    skew.update({role: phone_time(s) - time.time() for role, s in serials.items()})
    casters = ["host", "guest"] if a.caster == "both" else [a.caster]
    for name in a.scenarios:
        if name == "magic":
            magic(casters, a.kinds.split(","), a.wait, a.after)
        elif name == "perf":
            perf(a.perf_sec, a.perf_enemies, a.perf_type)
        else:
            {"mobs": mobs, "chests": chests, "drop": drop}[name]()


def main():
    global out_dir
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default=HOST_SERIAL)
    ap.add_argument("--guest", default=GUEST_SERIAL)
    ap.add_argument("--dir", help="папка для скриншотов и итог.md, по умолчанию dist/lab/<время>")
    ap.add_argument("--out")
    ap.add_argument("--wait", type=float, default=6, help="для magic: сколько ждать, пока магия появится (Армагеддон — не меньше 40)")
    ap.add_argument("--after", type=float, default=3, help="для magic: сколько смотреть после появления")
    ap.add_argument("--kinds", default="spells,traits,combos", help="для magic: spells, traits, combos")
    ap.add_argument("--caster", default="both", choices=["host", "guest", "both"], help="для magic: кто кастует")
    ap.add_argument("--json", help="итог по сценариям в файл JSON: имя, прошёл, проверки, цифры")
    ap.add_argument("--perf-sec", type=float, default=60, help="для perf: длина замера, с")
    ap.add_argument("--perf-enemies", type=int, default=200, help="для perf: число врагов")
    ap.add_argument("--perf-type", type=int, default=6, help="для perf: тип врагов")
    ap.add_argument("scenarios", nargs="+", choices=["magic", "mobs", "chests", "drop", "perf"])
    a = ap.parse_args()
    out_dir = a.dir or os.path.join(ROOT, "dist", "lab", time.strftime("%Y%m%d-%H%M%S"))
    os.makedirs(out_dir, exist_ok=True)
    error = None
    try:
        run_scenarios(a)
    except Abort as e:
        error = str(e)
    except OSError as e:
        error = f"консоль coop_debug оборвалась: {e}"
    if error:
        say(f"ОШИБКА {error}")
        if results:
            results[-1]["passed"] = False
    fails = sum(1 for r in report if r.startswith("FAIL"))
    say(f"ИТОГ: {sum(1 for r in report if r.startswith('PASS'))} PASS, {fails} FAIL")
    write_summary(os.path.join(out_dir, "итог.md"), error)
    say(f"итог и скриншоты: {out_dir}")
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            f.write("\n".join(report) + "\n")
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump({"passed": not error and not fails, "error": error, "scenarios": results, "summary": summary}, f, ensure_ascii=False, indent=2)
    sys.exit(1 if error or fails else 0)


if __name__ == "__main__":
    main()
