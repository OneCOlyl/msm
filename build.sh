#!/usr/bin/env bash
# Сборка мода «Кооператив» и прогон тестов. Нужен .NET SDK 8.
#   bash build.sh                 — сборка против заглушек Unity (без телефона)
#   REF=/путь/к/ref bash build.sh — против настоящих сборок игры (net8/ и il2cpp/ с телефона)
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$PWD"

echo "== заглушки Unity"
dotnet build stubs/UnityEngine.UI/UnityEngine.UI.csproj -c Release -o stubs/out -nologo -v q

echo "== мод"
EXTRA=()
[ -n "${REF:-}" ] && EXTRA=(-p:Ref="$REF")
dotnet build mod/coop/coop.csproj -c Release -nologo -v q "${EXTRA[@]}"
dotnet build mod/coop_probe/coop_probe.csproj -c Release -nologo -v q

echo "== упаковка"
OUT="$ROOT/dist"; mkdir -p "$OUT"; rm -f "$OUT/coop.zip"
( cd mod/coop && python3 - "$OUT/coop.zip" <<'EOF'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED) as z:
    z.write("mod.json", "mod.json")
    z.write("bin/Release/net8.0/coop.dll", "coop.dll")
    z.write("icon.png", "icon.png")
print("dist/coop.zip готов")
EOF
)
( cd mod/coop_probe && python3 - "$OUT/coop_probe.zip" <<'EOF'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED) as z:
    z.write("mod.json", "mod.json")
    z.write("bin/Release/net8.0/coop_probe.dll", "coop_probe.dll")
    z.write("icon.png", "icon.png")
print("dist/coop_probe.zip готов (разведка врагов)")
EOF
)

if [ "${SKIP_TESTS:-}" != "1" ]; then
  dotnet build tests/NetTest/NetTest.csproj -c Release -nologo -v q >/dev/null
  dotnet build tests/ModSim/ModSim.csproj -c Release -nologo -v q >/dev/null
  NET=tests/NetTest/bin/Release/net8.0/NetTest.dll
  echo "== тест сети: встроенный сервер"
  dotnet "$NET" embedded | grep -E "^==|FAIL|ИТОГ"
  echo "== тест сети: Python-сервер"
  python3 server/coop_server.py --port 18766 >/dev/null 2>&1 & PY=$!
  sleep 1
  dotnet "$NET" external 127.0.0.1 18766 | grep -E "^==|FAIL|ИТОГ" || true
  kill $PY 2>/dev/null || true
  echo "== имитация игры: два мода (хост и друг)"
  dotnet tests/ModSim/bin/Release/net8.0/ModSim.dll | grep -E "FAIL|ИТОГ"
  echo "== разведчик врагов на фальшивой игре"
  dotnet build tests/ProbeSim/ProbeSim.csproj -c Release -nologo -v q >/dev/null
  dotnet tests/ProbeSim/bin/Release/net8.0/ProbeSim.dll | grep -E "FAIL|ИТОГ"
fi
