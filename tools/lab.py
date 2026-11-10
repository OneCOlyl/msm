#!/usr/bin/env python3
"""Проверки кооператива на двух телефонах через полигон coop_lab и консоль coop_debug.

Оба телефона подключены по USB, на обоих включены coop, coop_debug, coop_lab (и coop_cheat для магий), оба в бою.
Запуск:  python tools/lab.py [--host SERIAL] [--guest SERIAL] [--dir папка] [--out отчёт.txt] [--json итог.json] magic mobs chests drop perf
Скриншоты и итог (итог.md) — в папке --dir, по умолчанию dist/lab/<время>.
Код выхода 1 при любом провале, в том числе без телефонов.
"""
import argparse
import html
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
SNAP_AT = (0.6, 1.6)         # Скриншоты через столько секунд после появления магии у кастующего
GAP = 2                     # Второй герой правее кастующего: оба в кадре (4 ед. уже за краем), повтор у союзника отличим от своего героя
TARGET_TYPE = 6             # Цели для --targets: тип perf, у гостя его копии не пропадают
# Взрыв созвездия: снаряд 75 при попадании сам берёт Random.Range(75, 78), у каждого игрока свои попадания и свой вид
SAME_KIND = {"x76": "x75", "x77": "x75"}
STALE_SEC = 1.0             # Состояние друга не приходило дольше: связь пропадала, пункт не судится
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
ABILITY_NAMES = {}
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
    ABILITY_NAMES.update({int(i): n for k, i, n, _ in catalog if k == "trait"})
    items = []
    if "spells" in kinds:
        items += [("магия", i, SPELL_NAMES[i], [i], []) for i in SPELL_NAMES]
    if "traits" in kinds:
        items += [("особенность", int(i), n, [base_spell(int(i))], [int(i)]) for k, i, n, _ in catalog if k == "trait" and base_spell(int(i))]
    combos = {}
    for k, i, n, req in catalog:
        if k == "combo" and req:
            parts = [int(x) for x in req.split("+")]
            combos[int(i)] = (n, sorted({base_spell(x) for x in parts if base_spell(x)}), parts)
    if "combos" in kinds:
        items += [("комбинация", i, n, sp, parts) for i, (n, sp, parts) in combos.items()]
    if "ults" in kinds:
        items += [("ультимейт", int(i), f"{n} ({combos[int(i)][0]})", *combos[int(i)][1:]) for k, i, n, _ in catalog if k == "ult" and int(i) in combos]
    return items


# Магии

def families(items):
    """Спрайты вида без кадров, где картинки нет, и без буквы куска (MeteorLand4A/B/C — части одной воронки)."""
    return {(x[0][1], re.sub(r"(\d)[A-C]$", r"\1", x[0][2])) for x in items if x[0][2] not in ("нет", "Sprite_Trans")}


def looks(port):
    """Живые снаряды: {(вид, префаб, спрайт): (число, масштаб, до героя, до союзника)}."""
    res = {}
    for ln in lab_lines(port, "Looks"):
        p = ln.split("|")
        if len(p) == 7:
            key = (SAME_KIND.get(p[0], p[0]), p[1], re.sub(r"Explosion7[67]$", "Explosion75", p[2]) if p[0] in SAME_KIND else p[2])
            c, scale, d, al = res.get(key, (0, 0.0, 0.0, 0.0))
            n = int(p[3])
            res[key] = (c + n, max(scale, float(p[4])), (d * c + float(p[5]) * n) / (c + n), (al * c + float(p[6]) * n) / (c + n))
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


def lab_params(port):
    """Поля вида снарядов из coop: {"my": {...}, "partner": {...}}."""
    res = {}
    for ln in lab_lines(port, "LookParams"):
        who, _, rest = ln.partition("|")
        if who in ("my", "partner"):
            res[who] = dict(x.split("=", 1) for x in rest.split(",") if "=" in x)
    return res


ORB_NAMES = {"sa": "Спутники", "sb": "Спутники, второй круг", "sp": "Духи", "dp": "двойники"}


def orb_groups(port, who):
    """Орбиты из Lab.Orbs: {группа: (число, спрайт без кадра, масштаб, средний радиус)}; who — own или partner."""
    res = {}
    for ln in lab_lines(port, "Orbs"):
        p = ln.split("|")
        if len(p) == 9 and p[0] == who and p[2] != "0":
            res[p[1]] = (int(p[2]), re.sub(r"Motion[A-Z]$", "Motion", p[3]), float(p[4]), float(p[5]))
    return res


def orb_diff(own, other):
    """Копии орбит у друга против объектов кастующего: число, вид, размер и радиус."""
    res = []
    for k, (n, img, scale, rad) in sorted(own.items()):
        name = ORB_NAMES.get(k, k)
        if k not in other:
            res.append(f"{name}: у друга нет")
            continue
        n2, img2, scale2, rad2 = other[k]
        if n != n2:
            res.append(f"{name}: число {n} → {n2}")
        if img != img2:
            res.append(f"{name}: картинка {img} → {img2}")
        if abs(scale - scale2) > 0.1 * max(scale, scale2):
            res.append(f"{name}: размер {scale:g} → {scale2:g}")
        if abs(rad - rad2) > 0.3:
            res.append(f"{name}: радиус {rad:g} → {rad2:g}")
    return res


def cast_once(caster, item, max_wait, after, folder, targets, place, cast_every):
    """
    Один пункт у одного кастующего. Хост стоит в точке старта, гость в GAP правее: оба в кадре, места не меняются.
    С place друг после появления магии отходит на PLACE_GAP, и место снарядов меряется там: в GAP снаряд почти одинаково
    близок к обоим героям. С cast_every магии пункта кастуются в обход перезарядки каждые cast_every × 100 мс.
    """
    kind, num, name, spells_, abilities = item
    cp, op = (HOST_PORT, GUEST_PORT) if caster == "host" else (GUEST_PORT, HOST_PORT)
    batch(HOST_PORT, ["NoEnemies"])
    for port in (cp, op):
        batch(port, ["AutoCast 0 1", "ResetMagic", "ToStart 0" if port == HOST_PORT else f"ToStart {GAP}"])
    time.sleep(1.5)                                     # остатки прошлого пункта гаснут
    if targets:
        # Пуля, Молния, Луч и их особенности без цели не стреляют
        batch(HOST_PORT, [f"SpawnTough {TARGET_TYPE} {targets} 1000000000", "FreezeEnemies true"])
    start = time.time()
    o0, c0 = coop(op), coop(cp)
    calls = [f"Spell {sp} 5" for sp in spells_ if sp]
    if kind in ("комбинация", "ультимейт"):
        calls += [f"Combo {num} true"] + ([f"Ultimate {num} true"] if kind == "ультимейт" else [])
    else:
        calls += [f"Learn {a} 1" for a in abilities]
    granted = batch(cp, calls)
    if cast_every:
        batch(cp, [f"AutoCast {','.join(str(x) for x in spells_ if x)} {cast_every}"])
    not_given = [x for x in granted if re.search(r": (\d+) → \1$", x) or x.startswith(("ошибка", "нет ", "комбинации ")) or "нет свободной" in x]
    own, other, own_far, other_far = Seen(), Seen(), Seen(), Seen()
    orb0 = orbits(o0.get("partnerOrb"))
    orbs, arma, shots, first, moved, params, stale = [], 0, [], None, None, {}, 0.0
    orb_own, orb_other = {}, {}
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
        try:
            stale = max(stale, float(o1.get("stateAge", 0)))
        except ValueError:
            pass
        orb = orbits(o1.get("partnerOrb"))
        orbs = [f"{k}={orb[k]}" for k in ORBITS if orb.get(k, "0") not in ("0", orb0.get(k, "0"))]
        arma = int(o1.get("armaPlayed", 0)) - int(o0.get("armaPlayed", 0))
        if first is None and (own.looks or orbs or arma):
            first = now
        # Первый кадр — с запасом на задержку повтора у друга, второй — когда магия разошлась
        if first is not None and len(shots) < 4 and now - first >= SNAP_AT[len(shots) // 2]:
            shots += snap(folder, f"{kind}_{num}_{caster}_{len(shots) // 2 + 1}")
            if len(shots) == 4:
                params = {"caster": lab_params(cp), "other": lab_params(op)}
                orb_own, orb_other = orb_groups(cp, "own"), orb_groups(op, "partner")
        if place and len(shots) == 4 and moved is None and not any(t.is_alive() for t in shots):
            batch(op, [f"ToStart {PLACE_GAP}"])
            moved = time.time()
        if first is None and now - start > limit:
            break
        if first is not None and len(shots) == 4 and now - first > SNAP_AT[-1] + after and (not place or moved and now - moved > after):
            break
        time.sleep(0.3)
    if not orb_own and not not_given:
        orb_own, orb_other = orb_groups(cp, "own"), orb_groups(op, "partner")
    batch(cp, ["AutoCast 0 1"])
    if moved:
        batch(op, ["ToStart 0" if op == HOST_PORT else f"ToStart {GAP}"])
    for t in shots:
        t.join()
    c1, o1 = coop(cp), coop(op)
    sent = {b: n - counts(c0.get("sent")).get(b, 0) for b, n in counts(c1.get("sent")).items() if n > counts(c0.get("sent")).get(b, 0)}
    rep0, rep1 = counts(o0.get("replayed")), counts(o1.get("replayed"))
    errors = unity_errors_since(serials["host"], start + skew["host"]) + unity_errors_since(serials["guest"], start + skew["guest"])
    return {"not_given": not_given, "own": own, "other": other, "own_far": own_far, "other_far": other_far, "orbs": orbs, "arma": arma,
            "missed": [b for b in sent if rep1.get(b, 0) <= rep0.get(b, 0)],
            "sent_ids": set(sent), "replayed_ids": {b for b in rep1 if rep1[b] > rep0.get(b, 0)},
            "fails": int(o1.get("castFails", 0)) - int(o0.get("castFails", 0)), "errors": errors,
            "dropped": int(o1.get("castsDropped", 0)) - int(o0.get("castsDropped", 0)), "queued": o1.get("castsQueued", "?"),
            "shot": bool(shots), "params": params, "stale": stale, "orb_own": orb_own, "orb_other": orb_other, "granted": granted,
            "snaps": [f"{kind}_{num}_{caster}_{i}" for i in range(1, len(shots) // 2 + 1)]}


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
    if not r["own"].looks and not extra and not r["orb_own"]:
        note("ИНФО", f"{what}: без врагов не вызвалась")
        return "не вызвалась без врагов"
    if r["stale"] > STALE_SEC:
        r["not_given"] = [f"связь пропадала на {r['stale']:.1f} с"]
        note("НЕТ ", f"{what}: {r['not_given'][0]}, вид не сравнивался")
        return "связь пропадала"
    mine, theirs = r["own"].kinds(steady=True), r["other"].kinds()
    problems = []
    shown_as_orbit = {k for k, o in SHOT_ORBITS.items() if any(x.startswith(o + "=") for x in r["orbs"])}
    # Прозрачный снаряд (BulletSub Вспышки, s21) — область урона: у друга её нет, и разницы в виде это не даёт
    unseen = {k for k, v in mine.items() if all(x[0][2] == "Sprite_Trans" for x in v)}
    missing = [k for k in mine if k not in theirs and k not in shown_as_orbit and k not in unseen]
    # Быстрый снаряд гаснет раньше замера (3 раза в секунду), а вторичный взрыв (не из Bullet_Create) у каждого зависит от
    # своих попаданий: если повтор был или вид не пересылается, это не провал
    def num(k):
        return int(k[1:]) if k[1:].isdigit() else None
    brief = [k for k in missing if k[0] == "b" and num(k) in r["replayed_ids"] or k[0] == "x" and (num(k) not in r["sent_ids"] or k in SAME_KIND.values())]
    missing = [k for k in missing if k not in brief]
    if brief:
        note("ИНФО", f"{what}: у {sees} в замеры не попали {', '.join(brief)}")
    if missing:
        problems.append(f"у {sees} нет {', '.join(missing)}")
    for k in mine:
        if k not in theirs:
            continue
        a, b = families(mine[k]), families(theirs[k])
        if a and b and not a & b:
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
        problems.append(f"{sees} не повторил снаряды {r['missed']} (отброшено старых {r['dropped']}, в очереди {r['queued']})")
    r["scale_diff"] = scale_diff(r["own"], r["other"])
    if r["scale_diff"]:
        note("ИНФО", f"{what}: размер у друга другой: {', '.join(r['scale_diff'])}")
    r["param_diff"] = param_diff(r["params"])
    if r["param_diff"]:
        problems.append(f"параметры вида у друга другие: {', '.join(r['param_diff'])}")
    problems += orb_diff(r["orb_own"], r["orb_other"])
    r["problems"] = problems
    check(not problems, f"{what}: {shown}" + (f"; {'; '.join(problems)}" if problems else ""))
    return shown


def scale_diff(own, other, tolerance=0.15):
    """Наибольший масштаб вида у кастующего и у друга. Огонь и взрывы растут по ходу анимации, поэтому только ИНФО."""
    a, b = {}, {}
    for seen, d in ((own, a), (other, b)):
        for k, v in seen.looks.items():
            if v[2]:
                d[k] = max(d.get(k, 0), v[2])
    return [f"{k[0]} {k[2]} {a[k]:g} → {b[k]:g}" for k in sorted(a) if k in b and abs(a[k] - b[k]) > tolerance * max(a[k], b[k])]


def param_diff(params):
    """Поля вида, которые кастующий шлёт, против полученных другом: должны совпасть."""
    mine, got = params.get("caster", {}).get("my", {}), params.get("other", {}).get("partner", {})
    return [f"{k} {v} → {got.get(k, 'нет')}" for k, v in sorted(mine.items()) if got and got.get(k) != v]


def magic(casters, kinds, max_wait, after, targets=0, only=None, place=False, cast_every=0):
    """
    Магии, особенности, комбинации и ультимейты по одной: кастует хост, потом гость, второй стоит рядом. У кастующего и у
    друга сравниваются вид снаряда (номер, префаб, спрайт без номера кадра), поля вида и, с place, место: у друга снаряд
    должен быть у союзника. Свои снаряды хоста и гостя на одной магии должны совпасть.
    """
    begin("magic")
    say("== магии, особенности и комбинации без врагов: " + ", ".join("кастует " + ("хост" if c == "host" else "гость") for c in casters))
    calm_arena()
    items = [x for x in magic_items(kinds) if not only or x[1] in only]
    say(f"пунктов {len(items)}")
    folder = os.path.join(out_dir, "magic")
    for item in items:
        res = {}
        for caster in casters:
            for _ in range(3):                          # Обрыв связи (VPN) на пункте — повторить, а не считать провалом
                res[caster] = cast_once(caster, item, max_wait, after, folder, targets, place, cast_every)
                if res[caster]["stale"] <= STALE_SEC:
                    break
                time.sleep(STALE_SEC + res[caster]["stale"])
        row = {"item": f"{item[0]} {item[1]} «{item[2]}»", "kind": item[0], "id": item[1], "name": item[2],
               "spells": item[3], "parts": item[4], "detail": {}}
        for caster in casters:
            r = res[caster]
            row[caster] = judge_cast(item, caster, r)
            row["detail"][caster] = {
                "own": seen_rows(r["own"]), "other": seen_rows(r["other"]), "orbs": r["orbs"], "arma": r["arma"],
                "not_given": r["not_given"], "granted": r["granted"], "problems": r.get("problems", []),
                "scale_diff": r.get("scale_diff", []), "param_diff": r.get("param_diff", []), "params": r["params"],
                "snaps": r["snaps"], "errors": sorted(set(r["errors"]))[:3],
                "orb_own": r["orb_own"], "orb_other": r["orb_other"]}
        if len(casters) == 2 and all(res[c]["own"].looks and not res[c]["not_given"] for c in casters):
            # Редкий снаряд (метеорит) может не успеть упасть у одного за время пункта: это не разница в виде
            h, g = res["host"]["own"].kinds(), res["guest"]["own"].kinds()
            differ = [f"{k}: у хоста {sorted(families(h[k]))}, у гостя {sorted(families(g[k]))}"
                      for k in h if k in g and families(h[k]) and families(g[k]) and not families(h[k]) & families(g[k])]
            check(not differ, f"{row['item']}: своя магия у хоста и у гостя выглядит одинаково" + (f" — {'; '.join(differ)}" if differ else ""))
            only = sorted(set(h) ^ set(g))
            if only:
                note("ИНФО", f"{row['item']}: только у одного успели появиться {', '.join(only)}")
            row["same"] = "нет" if differ else "да" + (f" (только у одного: {', '.join(only)})" if only else "")
        summary["magic"].append(row)
    if targets:
        batch(HOST_PORT, ["FreezeEnemies false", "NoEnemies"])
    for port in (HOST_PORT, GUEST_PORT):
        batch(port, ["ResetMagic", "ToStart 0" if port == HOST_PORT else f"ToStart {GAP}"])


# Мобы

def seen_rows(seen):
    return [{"kind": k[0], "prefab": k[1], "sprite": k[2], "samples": v[0], "max": v[1], "scale": round(v[2], 3)}
            for k, v in sorted(seen.looks.items())]


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


HTML_STYLE = """
body{font:14px system-ui,sans-serif;margin:16px;background:#f4f4f6;color:#222}
.card{background:#fff;border-radius:8px;padding:12px 16px;margin:12px 0;box-shadow:0 1px 3px #0002}
.card h2{font-size:17px;margin:0 0 4px}.meta{color:#555;margin-bottom:8px}
.side{border-top:1px solid #ddd;padding-top:8px;margin-top:8px}
.ok{color:#17803d}.bad{color:#c62828}.warn{color:#a15c00}.info{color:#777}
.shots{display:flex;gap:8px;flex-wrap:wrap}.shots figure{margin:0}.shots img{height:360px;border:1px solid #ccc}
figcaption{font-size:12px;color:#555}
table{border-collapse:collapse;margin:6px 0;font-size:13px}td,th{border:1px solid #ddd;padding:2px 6px;text-align:left}
th{background:#f0f0f0}.miss{background:#ffe5e5}
nav label{margin-right:12px}
"""


def write_magic_html(path):
    """Отчёт по magic: пункт, что выдано, снаряды у кастующего и у друга, поля вида и скриншоты рядом."""
    e = html.escape
    who_name = {"host": "хост", "guest": "гость"}

    def status(d):
        if d["not_given"]:
            return "info", "не выдалось"
        if d["errors"]:
            return "bad", "ошибки"
        if not d["own"] and not d["orbs"] and not d["arma"]:
            return "info", "не вызвалась"
        if d["problems"]:
            return "bad", "FAIL"
        if d["scale_diff"]:
            return "warn", "размер другой"
        return "ok", "PASS"

    def looks_table(own, other):
        keys = sorted({(x["kind"], x["prefab"], x["sprite"]) for x in own + other})
        a = {(x["kind"], x["prefab"], x["sprite"]): x for x in own}
        b = {(x["kind"], x["prefab"], x["sprite"]): x for x in other}
        rows = ["<tr><th>вид</th><th>префаб</th><th>спрайт</th><th>у кастующего: число / масштаб</th><th>у друга: число / масштаб</th></tr>"]
        for k in keys:
            cell_a = f"{a[k]['max']} / {a[k]['scale']:g}" if k in a else "—"
            cell_b = f"{b[k]['max']} / {b[k]['scale']:g}" if k in b else "—"
            cls = ' class="miss"' if (k in a) != (k in b) else ""
            rows.append(f"<tr{cls}><td>{e(k[0])}</td><td>{e(k[1])}</td><td>{e(k[2])}</td><td>{cell_a}</td><td>{cell_b}</td></tr>")
        return "<table>" + "".join(rows) + "</table>" if keys else "<p class=info>снарядов нет</p>"

    def orbs_table(own, other):
        if not own and not other:
            return ""
        rows = ["<tr><th>орбита</th><th>у кастующего: число / картинка / размер / радиус</th><th>копии у друга</th></tr>"]
        for k in sorted(set(own) | set(other)):
            a, b = own.get(k), other.get(k)
            text = [f"{x[0]} / {e(x[1])} / {x[2]:g} / {x[3]:g}" if x else "—" for x in (a, b)]
            cls = ' class="miss"' if a and orb_diff({k: a}, {k: b} if b else {}) else ""
            rows.append(f"<tr{cls}><td>{e(ORB_NAMES.get(k, k))}</td><td>{text[0]}</td><td>{text[1]}</td></tr>")
        return "<table>" + "".join(rows) + "</table>"

    def params_table(params):
        mine, got = params.get("caster", {}).get("my", {}), params.get("other", {}).get("partner", {})
        if not mine:
            return ""
        rows = ["<tr><th>поле героя</th><th>у кастующего</th><th>получил друг</th></tr>"]
        for k in sorted(mine):
            cls = ' class="miss"' if got and got.get(k) != mine[k] else ""
            rows.append(f"<tr{cls}><td>{e(k)}</td><td>{e(mine[k])}</td><td>{e(got.get(k, '—'))}</td></tr>")
        return f"<details><summary>параметры вида ({len(mine)})</summary><table>{''.join(rows)}</table></details>"

    cards, counts = [], {}
    for row in summary["magic"]:
        spells_ = ", ".join(f"{SPELL_NAMES.get(x, x)} ({x}), ур. 5" for x in row["spells"] if x) or "—"
        parts = ", ".join(f"{ABILITY_NAMES.get(x, x)} ({x})" for x in row["parts"]) or "—"
        sides = []
        worst = "ok"
        for caster, d in row["detail"].items():
            other = "guest" if caster == "host" else "host"
            cls, text = status(d)
            counts[text] = counts.get(text, 0) + 1
            worst = max(worst, cls, key=["info", "ok", "warn", "bad"].index)
            notes = d["problems"] + [f"размер: {x}" for x in d["scale_diff"]] + d["not_given"] + d["errors"]
            extra = ", ".join(d["orbs"] + ([f"Армагеддон ×{d['arma']}"] if d["arma"] else []))
            figs = "".join(
                f'<figure><img loading="lazy" src="magic/{e(name)}_{role}.png"><figcaption>{i + 1}-й кадр, у {lbl}</figcaption></figure>'
                for i, name in enumerate(d["snaps"]) for role, lbl in ((caster, "кастующего"), (other, "друга")))
            sides.append(
                f'<div class=side><b>кастует {who_name[caster]}, смотрит {who_name[other]}:</b> <span class={cls}>{text}</span>'
                + "".join(f"<div class={cls}>{e(n)}</div>" for n in notes)
                + (f"<div>орбиты у друга: {e(extra)}</div>" if extra else "")
                + looks_table(d["own"], d["other"]) + orbs_table(d.get("orb_own", {}), d.get("orb_other", {})) + params_table(d["params"])
                + (f"<div class=shots>{figs}</div>" if figs else "") + "</div>")
        cards.append(f'<div class="card" data-s="{worst}"><h2>{e(row["kind"])} {row["id"]} «{e(row["name"])}»</h2>'
                     f'<div class=meta>заклинания: {e(spells_)}; выдано: {e(parts)}</div>{"".join(sides)}</div>')
    nav = ("<nav>" + "".join(f'<label><input type=checkbox checked onchange="f()" value={v}> {t}</label>'
                             for v, t in (("bad", "FAIL"), ("warn", "размер другой"), ("ok", "PASS"), ("info", "не вызвалась / не выдалось")))
           + "</nav><script>function f(){const on=[...document.querySelectorAll('nav input:checked')].map(x=>x.value);"
           "document.querySelectorAll('.card').forEach(c=>c.style.display=on.includes(c.dataset.s)?'':'none')}</script>")
    total = ", ".join(f"{k}: {v}" for k, v in sorted(counts.items()))
    with open(path, "w", encoding="utf-8") as f:
        f.write(f"<!doctype html><meta charset=utf-8><title>Магии в кооперативе</title><style>{HTML_STYLE}</style>"
                f"<h1>Магии в кооперативе</h1><p>{e(time.strftime('%Y-%m-%d %H:%M'))}; направлений {sum(counts.values())}: {e(total)}</p>"
                f"{nav}{''.join(cards)}")


skew = {}


def run_scenarios(a):
    setup(a.host, a.guest)
    skew.update({role: phone_time(s) - time.time() for role, s in serials.items()})
    casters = ["host", "guest"] if a.caster == "both" else [a.caster]
    for name in a.scenarios:
        if name == "magic":
            only = {int(x) for x in a.items.split(",")} if a.items else None
            magic(casters, a.kinds.split(","), a.wait, a.after, a.targets, only, a.place, a.cast_every)
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
    ap.add_argument("--after", type=float, default=1.5, help="для magic: сколько смотреть после второго скриншота")
    ap.add_argument("--kinds", default="spells,traits,combos,ults", help="для magic: spells, traits, combos, ults")
    ap.add_argument("--targets", type=int, default=3, help="для magic: стоящих неубиваемых врагов у хоста на каждый пункт, 0 — без врагов")
    ap.add_argument("--place", action="store_true", help="для magic: друг отходит на PLACE_GAP, проверка места снарядов")
    ap.add_argument("--cast-every", type=int, default=5, help="для magic: каст в обход перезарядки каждые N×100 мс, 0 — как в игре")
    ap.add_argument("--items", help="для magic: только эти номера через запятую")
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
    if summary["magic"]:
        write_magic_html(os.path.join(out_dir, "magic.html"))
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
