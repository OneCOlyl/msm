#!/usr/bin/env python3
"""Автобой на Terra вдвоём: окна выбора, сундуки, торговец и обелиски проходит сам, без касаний.

Нужно то же, что для phones.py (оба телефона на USB, coop, coop_debug, coop_lab), и coop_lab 0.1.12+.
Запуск:  python tools/terra.py [--minutes 6] [--no-restart]
Умение и атрибут: верхняя карта. Усиление магии: первая магия сверху слева, в каждом следующем окне следующая
вправо по рядам. Сундук: первый артефакт.
Торговец: первый артефакт, если хватает денег, затем выход. Обелиски поднимает хост. Героев никто не водит, поэтому у обоих
бессмертие полигона.
"""
import argparse
import os
import re
import subprocess
import sys
import time

import lab
import phones
from lab import ADB, ARTIFACT_CARD, GUEST_PORT, HOST_PORT, MERCHANT_OUT, OBTAIN, buttons, in_reading_order

ABILITY_CARD = re.compile(r"Canvas/Canvas_Ability/Field_Ability/\d+")
TRAIT_CARD = re.compile(r"Canvas/Canvas_AbilityCh/Field_AbilityChButton/\d+")
LEARN = "Canvas/Canvas_AbilityCh/Button_AbilityCh_Decision"
UPGRADE_CARD = re.compile(r"Canvas/Canvas_MagicUpgrade/Field_MagicUpgrade/\d+")
UPGRADE_OK = "Canvas/Canvas_MagicUpgrade/Button_MagicUpgrade_Decision"
MERCHANT_CARD = re.compile(r"Canvas/Canvas_Merchant/Field_MerchantSheet/\d+")
BUY = "Canvas/Canvas_Merchant/Button_Merchant_BUY"
# Эти окна открываются поверх окна умений, поэтому проверяются раньше него
OVERLAYS = ((TRAIT_CARD, LEARN, "атрибут"), (UPGRADE_CARD, UPGRADE_OK, "усиление магии"), (ARTIFACT_CARD, OBTAIN, "артефакт"))
OBELISKS = ("box9", "box10", "box11", "obelisk")
CHESTS = ("box5", "box6", "box7")


def call(port, what):
    return lab.lab(port, what)


def pickups(port):
    res = []
    for ln in call(port, "Pickups").splitlines():
        kind, x, y = ln.split("|")
        res.append((kind, float(x), float(y)))
    return res


class Player:
    def __init__(self, role, serial, port, out):
        self.role, self.serial, self.port, self.out = role, serial, port, out
        self.upgrades = 0
        self.shopped = False
        self.tries = {}
        self.unknown = set()
        self.picked = {}

    def say(self, what):
        print(f"[{self.role} {clock(self.port)}] {what}", flush=True)

    def click(self, b, why, closes=True):
        r = call(self.port, f"Click {b['i']} {b['path']}")
        self.say(f"{why}: {b['text'] or b['path'].rsplit('/', 1)[-1]} ({r})")
        if not closes:
            return
        # Окно закрывается не сразу, и второе нажатие попало бы в него же
        deadline = time.time() + 3
        while time.time() < deadline and any(x["path"] == b["path"] and x["text"] == b["text"] and x["on"] for x in buttons(self.port)):
            time.sleep(0.3)

    def step(self, pickup_kinds):
        bs = buttons(self.port)
        on = {b["path"]: b for b in bs if b["on"]}
        if MERCHANT_OUT in on:
            return self.shop(on)
        for card, confirm, what in OVERLAYS:
            if confirm in on:
                if card is UPGRADE_CARD:
                    self.upgrades += 1
                return self.click(on[confirm], "подтверждаю " + what)
            cards = in_reading_order(on.values(), card)
            if cards:
                # Усиление магии приходит часто: каждый раз следующая магия, чтобы не качать одну
                return self.click(cards[self.upgrades % len(cards)] if card is UPGRADE_CARD else cards[0], what, closes=False)
        other = [b for b in bs if not b["path"].startswith("Canvas/Canvas_Ability/")]
        if other:
            return self.unknown_window(other)
        cards = in_reading_order(on.values(), ABILITY_CARD)
        if cards:
            return self.click(cards[0], "умение")
        self.walk(pickup_kinds)

    def shop(self, on):
        """Первый товар, если хватает денег (тогда игра включает Buy), и выход."""
        cards = in_reading_order(on.values(), MERCHANT_CARD)
        if self.shopped or not cards:
            self.shopped = False
            return self.click(on[MERCHANT_OUT], "ухожу от торговца")
        self.shopped = True
        self.click(cards[0], f"торговец, цена {cards[0]['text']}", closes=False)
        time.sleep(0.5)
        buy = next((b for b in buttons(self.port) if b["path"] == BUY), None)
        if buy and buy["on"]:
            self.click(buy, "покупаю", closes=False)
        else:
            self.say("торговец: денег не хватает")

    def unknown_window(self, bs):
        key = frozenset(b["path"] for b in bs)
        if key in self.unknown:
            return
        self.unknown.add(key)
        self.say("незнакомое окно, кнопки:\n  " + "\n  ".join(f"{b['path']} ({b['x']:.0f},{b['y']:.0f}) {'вкл' if b['on'] else 'выкл'} {b['text']}" for b in bs))
        shot = os.path.join(self.out, f"{self.role}_окно_{len(self.unknown)}.png")
        with open(shot, "wb") as f:
            f.write(subprocess.run([ADB, "-s", self.serial, "exec-out", "screencap", "-p"], capture_output=True).stdout)
        self.say(f"скриншот {shot}")

    def walk(self, kinds):
        for kind, x, y in pickups(self.port):
            key = (kind, round(x, 1), round(y, 1))
            if kind not in kinds or self.tries.get(key, 0) >= 3:
                continue
            self.tries[key] = self.tries.get(key, 0) + 1
            self.say(f"иду к {kind} ({x:.1f}, {y:.1f}): {call(self.port, f'GoTo {x} {y}')}")
            self.picked[kind] = self.picked.get(kind, 0) + 1
            time.sleep(1)
            return


def clock(port):
    c = phones.coop_state(port).get("clock", "?")
    return f"{int(c) // 60}:{int(c) % 60:02d}" if c.lstrip("-").isdigit() and int(c) >= 0 else c


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default=lab.HOST_SERIAL)
    ap.add_argument("--guest", default=lab.GUEST_SERIAL)
    ap.add_argument("--minutes", type=float, default=6, help="сколько минут боя прожить")
    ap.add_argument("--no-restart", action="store_true", help="оба уже в бою на Terra")
    ap.add_argument("--out", help="папка для журналов и скриншотов, по умолчанию dist/terra/<время>")
    a = ap.parse_args()
    a.out = a.out or os.path.join(phones.ROOT, "dist", "terra", time.strftime("%Y%m%d-%H%M%S"))
    os.makedirs(a.out, exist_ok=True)
    a.area, a.loop = phones.TERRA, False
    if not a.no_restart:
        phones.restart(a)
        phones.connect(a)
        phones.battle(a)
    for port in (HOST_PORT, GUEST_PORT):
        print(f"{'хост' if port == HOST_PORT else 'гость'}: {call(port, 'SetImmortal true')}", flush=True)
    host = Player("хост", a.host, HOST_PORT, a.out)
    guest = Player("гость", a.guest, GUEST_PORT, a.out)
    started = time.time()
    while True:
        h = phones.coop_state(HOST_PORT)
        if h.get("inRun") != "True":
            phones.check(False, f"хост вышел из боя на {clock(HOST_PORT)}")
            break
        if int(h.get("clock", 0)) >= a.minutes * 60:
            phones.check(True, f"бой дожил до {clock(HOST_PORT)}")
            break
        if phones.coop_state(GUEST_PORT).get("inRun") != "True":
            phones.check(False, f"гость вышел из боя на {clock(GUEST_PORT)}")
            break
        host.step(OBELISKS + CHESTS)
        guest.step(CHESTS)
        time.sleep(0.3)
    for p in (host, guest):
        errors = lab.unity_errors_since(p.serial, started + lab.phone_time(p.serial) - time.time())
        phones.check(not errors, f"{p.role}: исключений Unity {len(errors)}" + (": " + "; ".join(sorted(set(errors))[:3]) if errors else ""))
        print(f"{p.role}: подобрано {p.picked or 'ничего'}, незнакомых окон {len(p.unknown)}")
    phones.logs(a)
    sys.exit(1 if phones.fails else 0)


if __name__ == "__main__":
    main()
