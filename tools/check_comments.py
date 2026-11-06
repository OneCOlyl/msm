"""Падает, если в mod/**/*.cs есть XML-документация (строки ///): в проекте комментарии только «почему», обычным //."""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
found = [
    f"{path.relative_to(ROOT).as_posix()}:{n}: {line.strip()}"
    for path in sorted((ROOT / "mod").rglob("*.cs"))
    if not {"bin", "obj"} & set(path.relative_to(ROOT).parts)
    for n, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1)
    if re.match(r"\s*///", line)
]
if found:
    sys.stdout.reconfigure(encoding="utf-8")
    print("\n".join(found))
    sys.exit(f"строк /// в mod/: {len(found)}; оставьте «почему» обычным // в одну строку, остальное удалите")
