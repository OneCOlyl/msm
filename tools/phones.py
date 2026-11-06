#!/usr/bin/env python3
"""Проверка кооператива на двух телефонах без рук: установка сборки, перезапуск игры, подключение, бой, сценарии lab.py, журналы.

Нужно: оба телефона на USB, на них ModKit и библиотеки MS_KIT, у гостя в настройках coop указан IP хоста.
Запуск:  python tools/phones.py run [--build] [--area N] [--lab "mobs drop"]
Шаги по отдельности: status, deploy, restart, connect, battle, logs.
"""
import argparse
import io
import json
import os
import re
import shlex
import subprocess
import sys
import tempfile
import time
import zipfile

import lab
from lab import ADB, GUEST_PORT, HOST_PORT, console

PKG = "com.vkslrzm.Zombix"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = f"/sdcard/MelonLoader/{PKG}"
KIT = f"{GAME}/UserData/ModKit"
MODS = ("coop", "coop_debug", "coop_cheat", "coop_lab")
MOD_LIST = "T:ModKit:ModKit.Core/Mods"
TERRA = 14
LOG_FILES = ("UserData/ModKit/modkit.log", "UserData/ModKit/lastrun.txt", "UserData/ModKit/config.json", "MelonLoader/Latest.log")
LOG_PROBLEMS = re.compile(r"ОШИБКА \[|отключён из-за ошибок|мод выключен|Exception")

fails = []


def check(ok, what):
    print(("PASS " if ok else "FAIL ") + what, flush=True)
    if not ok:
        fails.append(what)
    return ok


def adb(serial, *args):
    r = subprocess.run([ADB, "-s", serial, *args], capture_output=True)
    if r.returncode:
        sys.exit(f"adb -s {serial} {' '.join(args)}: {r.stderr.decode('utf-8', 'replace').strip()}")
    return r.stdout.decode("utf-8", "replace")


def pull(serial, path):
    with tempfile.TemporaryDirectory() as d:
        local = os.path.join(d, "f")
        if subprocess.run([ADB, "-s", serial, "pull", path, local], capture_output=True).returncode:
            return None
        with open(local, "rb") as f:
            return f.read()


def push(serial, data, path):
    with tempfile.TemporaryDirectory() as d:
        local = os.path.join(d, os.path.basename(path))
        with open(local, "wb") as f:
            f.write(data)
        adb(serial, "push", local, path)


def zip_version(data):
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        return json.loads(z.read("mod.json").decode("utf-8-sig"))["version"]


def zip_files(data):
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        return {n: z.read(n) for n in z.namelist()}


def local_zip(mod):
    with open(os.path.join(ROOT, "dist", f"{mod}.zip"), "rb") as f:
        return f.read()


def phones(a):
    return ((a.host, HOST_PORT, "хост"), (a.guest, GUEST_PORT, "гость"))


def try_console(port, cmd):
    try:
        return console(port, cmd, timeout=10)
    except OSError:
        return None


def coop_state(port):
    raw = try_console(port, f"call {lab.LAB}.CoopStatus") or ""
    return dict(kv.split("=", 1) for kv in raw.split("; ") if "=" in kv)


def session(port):
    """Путь к сессии coop в консоли: номер мода в списке ModKit зависит от набора модов на телефоне."""
    for i in range(100):
        mod_id = console(port, f"get {MOD_LIST}[{i}].Def.Id")
        if mod_id == "coop":
            return f"{MOD_LIST}[{i}].Def.OnEnable.Target._session"
        if mod_id.startswith("ошибка"):
            break
    sys.exit(f"порт {port}: мод coop не включён")


def wait(what, cond, timeout):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if cond():
            return True
        time.sleep(2)
    sys.exit(f"не дождался за {timeout} с: {what}")


def build(_):
    subprocess.run(["bash", "build.sh"], cwd=ROOT, check=True, env={**os.environ, "SKIP_TESTS": "1"})


def deploy(a):
    for mod in MODS:
        data = local_zip(mod)
        version = zip_version(data)
        for serial, _, role in phones(a):
            old = pull(serial, f"{KIT}/mods/{mod}.zip")
            if old and zip_files(old) == zip_files(data):
                continue
            if old and zip_version(old) == version:
                # Пересборка без правок тоже меняет байты dll, поэтому только предупреждение
                print(f"ВНИМАНИЕ {role}: {mod} {version} на телефоне другой сборки, ModKit архив той же версии не перечитает: не ставлю."
                      " Сборка с незакоммиченными правками получает новую версию сама; с VERSION=… задайте другую", flush=True)
                continue
            # Кнопка «+» в окне «Моды» ставит архив из Download и откатила бы версию, будь он там старее
            for path in (f"{KIT}/mods/{mod}.zip", f"/sdcard/Download/{mod}.zip"):
                push(serial, data, path)
            print(f"{role}: {mod} {zip_version(old) if old else 'нет'} → {version}", flush=True)


def enable_mods(serial, role):
    raw = pull(serial, f"{KIT}/config.json")
    if raw is None:
        return
    cfg = json.loads(raw.decode("utf-8-sig"))
    enabled = cfg.setdefault("enabled", {})
    off = [m for m in MODS if enabled.get(m) is not True]
    if off:
        enabled.update({m: True for m in off})
        push(serial, (json.dumps(cfg, ensure_ascii=False, indent=2) + "\n").encode("utf-8"), f"{KIT}/config.json")
        print(f"{role}: включены {', '.join(off)}", flush=True)


def logged_version(serial):
    found = re.findall(r"кооператив (\S+) включён", (pull(serial, f"{KIT}/modkit.log") or b"").decode("utf-8", "replace"))
    return found[-1] if found else None


def restart(a):
    want = zip_version(local_zip("coop"))
    for serial, port, role in phones(a):
        # running.lock после force-stop ModKit считает падением и выключает моды, которые тогда работали
        adb(serial, "shell", f"am force-stop {PKG}; rm -f {KIT}/running.lock")
        enable_mods(serial, role)
        adb(serial, "forward", f"tcp:{port}", "tcp:8770")
        adb(serial, "shell", f"input keyevent KEYCODE_WAKEUP; monkey -p {PKG} -c android.intent.category.LAUNCHER 1")
    for serial, port, role in phones(a):
        wait(f"{role}: консоль coop_debug и coop_lab", lambda p=port: coop_state(p).get("coop") == "1", 120)
        check(logged_version(serial) == want, f"{role}: в modkit.log кооператив {logged_version(serial)}, в dist {want}")


def connect(a):
    for serial, port, _ in phones(a):
        adb(serial, "forward", f"tcp:{port}", "tcp:8770")
    console(HOST_PORT, f"call {session(HOST_PORT)}.Host")
    time.sleep(2)
    console(GUEST_PORT, f"call {session(GUEST_PORT)}.Join")
    wait("связь хоста и гостя",
         lambda: coop_state(HOST_PORT).get("hosting") == "True" and all(coop_state(p).get("connected") == "True" for p in (HOST_PORT, GUEST_PORT)), 40)
    print("на связи", flush=True)


def battle(a):
    # Button_AreaStart на Terra молча ничего не делает
    button = "Button_AreaStart_Loop" if a.loop else "Button_AreaSpecialSheet_Start" if a.area == TERRA else "Button_AreaStart"
    for _, port, role in phones(a):
        if coop_state(port).get("inRun") == "True":
            continue
        if a.area:
            console(port, f"set db.Game_AreaID {a.area}")
        s = session(port)
        console(port, f"call {s}.FindAllOfType A_MainMenuInformation")
        res = console(port, f"call last._items[0].{button}")
        if res.startswith("ошибка"):
            sys.exit(f"{role}: {button}: {res}")
        time.sleep(3)
    wait("оба в бою", lambda: all(coop_state(p).get("inRun") == "True" for p in (HOST_PORT, GUEST_PORT)), 90)
    print("оба в бою", flush=True)


def logs(a):
    out = a.out or os.path.join(ROOT, "dist", "phones", time.strftime("%Y%m%d-%H%M%S"))
    for serial, _, role in phones(a):
        folder = os.path.join(out, role)
        os.makedirs(folder, exist_ok=True)
        for f in LOG_FILES:
            data = pull(serial, f"{GAME}/{f}")
            if data is not None:
                with open(os.path.join(folder, os.path.basename(f)), "wb") as fh:
                    fh.write(data)
        text = (pull(serial, f"{KIT}/modkit.log") or b"").decode("utf-8", "replace").splitlines()
        starts = [i for i, ln in enumerate(text) if re.search(r"кооператив \S+ включён", ln)]
        run_lines = text[starts[-1]:] if starts else text
        problems = [ln for ln in run_lines if LOG_PROBLEMS.search(ln)]
        check(not problems, f"{role}: modkit.log с последнего запуска coop без ошибок" +
              (f", найдено {len(problems)}:\n  " + "\n  ".join(problems[:10]) if problems else ""))
    print(f"журналы: {out}", flush=True)


def status(a):
    for serial, port, role in phones(a):
        adb(serial, "forward", f"tcp:{port}", "tcp:8770")
        model = adb(serial, "shell", "getprop ro.product.model").strip()
        running = bool(adb(serial, "shell", f"pidof {PKG} || true").strip())
        print(f"== {role} {serial} ({model}), игра {'запущена' if running else 'не запущена'}, в modkit.log кооператив {logged_version(serial)}")
        for mod in MODS:
            old = pull(serial, f"{KIT}/mods/{mod}.zip")
            have = zip_version(old) if old else "нет"
            print(f"  {mod}: {have}" + ("" if old and zip_files(old) == zip_files(local_zip(mod)) else f" (в dist {zip_version(local_zip(mod))}, другой архив)"))
        state = coop_state(port)
        print("  " + ("; ".join(f"{k}={v}" for k, v in state.items()) if state else "консоль не отвечает"))


def run(a):
    if a.build:
        build(a)
    if not a.no_deploy:
        deploy(a)
    restart(a)
    connect(a)
    battle(a)
    started = time.time()
    if a.lab:
        lab_args = [sys.executable, os.path.join(ROOT, "tools", "lab.py"), "--host", a.host, "--guest", a.guest, *shlex.split(a.lab)]
        check(subprocess.run(lab_args).returncode == 0, f"lab.py {a.lab}")
    for serial, _, role in phones(a):
        errors = lab.unity_errors_since(serial, started + lab.phone_time(serial) - time.time())
        check(not errors, f"{role}: исключений Unity за проверку {len(errors)}" + (": " + "; ".join(sorted(set(errors))[:3]) if errors else ""))
    logs(a)


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("step", choices=["run", "status", "deploy", "restart", "connect", "battle", "logs"])
    ap.add_argument("--host", default=lab.HOST_SERIAL)
    ap.add_argument("--guest", default=lab.GUEST_SERIAL)
    ap.add_argument("--build", action="store_true", help="run: сначала bash build.sh без тестов")
    ap.add_argument("--no-deploy", action="store_true", help="run: не ставить архивы из dist")
    ap.add_argument("--area", type=int, help="номер локации (Game_AreaID), по умолчанию выбранная в игре")
    ap.add_argument("--loop", action="store_true", help="бой через Infinite Mode")
    ap.add_argument("--lab", default="mobs drop", help="run: аргументы lab.py, пусто — без сценариев")
    ap.add_argument("--out", help="папка для журналов, по умолчанию dist/phones/<время>")
    a = ap.parse_args()
    steps = {"run": run, "status": status, "deploy": deploy, "restart": restart, "connect": connect, "battle": battle, "logs": logs}
    steps[a.step](a)
    if fails:
        print(f"ИТОГ: {len(fails)} FAIL")
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
