"""Описание релиза: изменения с прошлого опубликованного релиза из .github/release_notes.md.

python3 tools/release_notes.py <release_notes.md> <версия> [<версия прошлого релиза>]

Берутся разделы «## Co-op X» сверху вниз, пока не встретится раздел прошлого релиза (его и ниже — уже были
в прошлых релизах). Прошлый релиз не указан или его раздела нет — только раздел текущей версии. Первый раздел должен
быть текущей версии, иначе ошибка. Изменения между релизами копятся в «## Не выпущено» сверху; перед релизом раздел
переименовывают в «## Co-op X». Пустой «## Не выпущено» над разделом версии пропускается.
"""
import re
import sys

UNRELEASED = "Не выпущено"


def sections(text):
    out, head, body = [], None, []
    for line in text.splitlines():
        m = re.match(r"^## .*?(\d+(?:\.\d+)+)\s*$", line)
        if m or line.strip() == f"## {UNRELEASED}":
            if head is not None:
                out.append((head, "\n".join(body).strip()))
            head, body = m.group(1) if m else UNRELEASED, [line]
        elif head is not None:
            body.append(line)
    if head is not None:
        out.append((head, "\n".join(body).strip()))
    return out


def notes(text, ver, prev=None):
    secs = sections(text)
    if secs and secs[0][0] == UNRELEASED:
        if secs[0][1] != f"## {UNRELEASED}":
            raise SystemExit(f"в начале release_notes.md раздел «## {UNRELEASED}» — переименуйте его в «## Co-op {ver}»")
        secs = secs[1:]
    if not secs or secs[0][0] != ver:
        raise SystemExit(f"в начале release_notes.md нет раздела «## Co-op {ver}» — допишите изменения этой версии")
    known = [v for v, _ in secs]
    if not prev or prev not in known:
        return secs[0][1]
    picked = []
    for v, body in secs:
        if v == prev:
            break
        picked.append(body)
    return "\n\n".join(picked)


if __name__ == "__main__":
    path, ver = sys.argv[1], sys.argv[2]
    prev = sys.argv[3].lstrip("v") if len(sys.argv) > 3 and sys.argv[3] else None
    with open(path, encoding="utf-8") as f:
        sys.stdout.reconfigure(encoding="utf-8")
        print(notes(f.read(), ver, prev))
