#!/usr/bin/env bash
# Сборка мода «Кооператив» и прогон тестов. Нужен .NET SDK 8.
#   bash build.sh                 — сборка против заглушек Unity (без телефона)
#   REF=/путь/к/ref bash build.sh — против настоящих сборок игры (net8/ и il2cpp/ с телефона)
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$PWD"

echo "== заглушки Unity"
dotnet build stubs/UnityEngine.UI/UnityEngine.UI.csproj -c Release -o stubs/out -nologo -v q

echo "== версии"
V_JSON=$(python3 -c "import json;print(json.load(open('mod/coop/mod.json',encoding='utf-8'))['version'])")
V_CS=$(grep -oP 'ModVersion = "\K[^"]+' mod/coop/CoopMod.cs)
V_PROJ=$(grep -oP '<Version>\K[^<]+' mod/coop/coop.csproj)
if [ "$V_JSON" != "$V_CS" ] || [ "$V_JSON" != "$V_PROJ" ]; then echo "версии не совпадают: mod.json $V_JSON, CoopMod.cs $V_CS, coop.csproj $V_PROJ"; exit 1; fi
echo "Кооператив $V_JSON"
for m in coop_debug coop_cheat coop_lab; do
  v_json=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1],encoding='utf-8'))['version'])" "mod/$m/mod.json")
  v_proj=$(grep -oP '<Version>\K[^<]+' "mod/$m/$m.csproj")
  v_cs=$(grep -ohP '^\s*Version = "\K[^"]+' mod/"$m"/*.cs)
  if [ "$v_json" != "$v_proj" ] || [ "$v_json" != "$v_cs" ]; then echo "$m: версии не совпадают: mod.json $v_json, csproj $v_proj, код $v_cs"; exit 1; fi
  echo "$m $v_json"
done

echo "== мод"
EXTRA=()
[ -n "${REF:-}" ] && EXTRA=(-p:Ref="$REF")
dotnet build mod/coop/coop.csproj -c Release -nologo -v q "${EXTRA[@]}"
for m in coop_debug coop_cheat coop_lab; do dotnet build "mod/$m/$m.csproj" -c Release -nologo -v q; done

echo "== упаковка"
OUT="$ROOT/dist"; mkdir -p "$OUT"
# Архив мода ModKit: mod.json, dll и значок в корне
pack() {
  rm -f "$OUT/$1.zip"
  ( cd "mod/$1" && python3 - "$OUT/$1.zip" "$1" <<'EOF'
import sys, zipfile
out, name = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    z.write("mod.json", "mod.json")
    z.write(f"bin/Release/net8.0/{name}.dll", f"{name}.dll")
    z.write("icon.png", "icon.png")
print(f"dist/{name}.zip готов")
EOF
  )
}
for m in coop coop_debug coop_cheat coop_lab; do pack "$m"; done

if [ "${SKIP_TESTS:-}" != "1" ]; then
  for t in NetTest ModSim DebugSim CheatSim; do dotnet build "tests/$t/$t.csproj" -c Release -nologo -v q >/dev/null; done
  NET=tests/NetTest/bin/Release/net8.0/NetTest.dll
  FAILED=0
  LOGS=$(mktemp -d)
  TESTS=()
  # Тесты не зависят друг от друга и идут параллельно, поэтому у каждого свой порт: NetTest берёт свободные,
  # ModSim 28766–28904, Python-серверы 18766/18767. Вывод печатается после всех, в прежнем порядке
  start_test() {
    local name=$1; shift
    ( rc=0; "$@" >"$LOGS/$name.log" 2>&1 || rc=$?; echo $rc >"$LOGS/$name.rc" ) &
    TESTS+=($!)
  }
  # Выводим только заголовки, провалы и итог; провал — по коду выхода программы.
  # Провал: хвост вывода, а на GitHub — ещё и аннотации ::error:: (их видно в API без прав администратора).
  report_test() {
    local name=$1 title=$2 log="$LOGS/$1.log" rc
    rc=$(cat "$LOGS/$name.rc")
    echo "== $title"
    grep -E "^== [^=]|FAIL|ИТОГ" "$log" || true
    if [ "$rc" -ne 0 ]; then
      echo "!! провал: $name (код $rc), последние строки:"; tail -n 25 "$log"
      if [ -n "${GITHUB_ACTIONS:-}" ]; then
        grep -E "FAIL|Unhandled|Exception" "$log" | head -n 10 | while IFS= read -r l; do echo "::error::$l"; done
      fi
      FAILED=1
    fi
  }
  python3 server/coop_server.py --port 18766 >/dev/null 2>&1 & PY=$!
  # второй — с маленькими пределами, для проверок защиты (те же, что у встроенного в NetTest)
  python3 server/coop_server.py --port 18767 --max-per-ip 3 --max-connections 10 --room-ops-per-minute 4 --join-timeout 2 >/dev/null 2>&1 & PY2=$!
  start_test modsim dotnet tests/ModSim/bin/Release/net8.0/ModSim.dll main
  start_test modsim7 dotnet tests/ModSim/bin/Release/net8.0/ModSim.dll 7
  start_test embedded dotnet "$NET" embedded
  start_test pyinside python3 tests/server_test.py
  start_test debugsim dotnet tests/DebugSim/bin/Release/net8.0/DebugSim.dll
  start_test cheatsim dotnet tests/CheatSim/bin/Release/net8.0/CheatSim.dll
  sleep 1
  start_test external dotnet "$NET" external 127.0.0.1 18766 18767
  wait "${TESTS[@]}"
  kill $PY $PY2 2>/dev/null || true
  report_test embedded "тест сети: встроенный сервер"
  report_test external "тест сети: Python-сервер"
  report_test pyinside "Python-сервер изнутри: счётчик входов, обратное давление"
  report_test modsim "имитация игры: два мода (хост и друг)"
  report_test modsim7 "имитация игры: выключение мода посреди боя"
  report_test debugsim "отладочная консоль: разбор команд"
  report_test cheatsim "чит: выдача уровней и уровень героя"
  rm -rf "$LOGS"
  [ $FAILED -eq 0 ] || { echo "ТЕСТЫ НЕ ПРОШЛИ"; exit 1; }
fi
