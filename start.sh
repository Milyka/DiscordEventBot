#!/usr/bin/env bash
set -e

cd "$(dirname "$0")"

ARCH="$(uname -m)"
OS_NAME="$(uname -s)"

if [ "$OS_NAME" != "Linux" ]; then
    echo "[WARN] Скрипт предназначен для Linux (текущая ОС: $OS_NAME)"
fi

if [ "$ARCH" != "x86_64" ]; then
    echo "[WARN] Ожидалась архитектура x86_64 (x64), обнаружена: $ARCH"
fi

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools"

HAS_NET10="0"
if command -v dotnet >/dev/null 2>&1; then
    if dotnet --list-sdks 2>/dev/null | grep -q "^10\."; then
        HAS_NET10="1"
    fi
fi

if [ "$HAS_NET10" != "1" ]; then
    echo "[INFO] .NET 10 SDK не найден. Установка .NET 10 SDK для Ubuntu x64..."
    INSTALLED="0"
    if command -v apt-get >/dev/null 2>&1; then
        sudo apt-get update -y || true
        if sudo apt-get install -y dotnet-sdk-10.0; then
            INSTALLED="1"
        fi
    fi

    if [ "$INSTALLED" != "1" ]; then
        echo "[INFO] Установка .NET 10 SDK через официальный скрипт Microsoft (dotnet-install.sh)..."
        if command -v curl >/dev/null 2>&1; then
            curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
        elif command -v wget >/dev/null 2>&1; then
            wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
        else
            sudo apt-get install -y curl
            curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
        fi
        chmod +x /tmp/dotnet-install.sh
        /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_ROOT"
        rm -f /tmp/dotnet-install.sh
    fi
fi

DOTNET_VER="$(dotnet --version)"
echo "[OK] Используется .NET SDK: $DOTNET_VER ($OS_NAME $ARCH)"

if [ -f "DiscordEventBot/DiscordEventBot.csproj" ]; then
    CSPROJ_PATH="DiscordEventBot/DiscordEventBot.csproj"
    SUBDIR_SETTINGS="DiscordEventBot/settings.json"
    SUBDIR_EXAMPLE="DiscordEventBot/settings.example.json"
elif [ -f "DiscordEventBot.csproj" ]; then
    CSPROJ_PATH="DiscordEventBot.csproj"
    SUBDIR_SETTINGS="settings.json"
    SUBDIR_EXAMPLE="settings.example.json"
else
    echo "[ERROR] Не найден файл проекта DiscordEventBot.csproj!"
    exit 1
fi

if [ ! -f "settings.json" ]; then
    if [ -f "settings.example.json" ]; then
        cp "settings.example.json" "settings.json"
    elif [ -f "$SUBDIR_SETTINGS" ]; then
        cp "$SUBDIR_SETTINGS" "settings.json"
    elif [ -f "$SUBDIR_EXAMPLE" ]; then
        cp "$SUBDIR_EXAMPLE" "settings.json"
    fi
fi

CURRENT_TOKEN=""
if command -v python3 >/dev/null 2>&1 && [ -f "settings.json" ]; then
    CURRENT_TOKEN=$(python3 -c 'import json; d=json.load(open("settings.json", encoding="utf-8")); print(d.get("Token",""))' 2>/dev/null || true)
fi

if [ -z "$CURRENT_TOKEN" ] || [ "$CURRENT_TOKEN" = "BOT_TOKEN" ]; then
    read -r -p "Введите токен Discord-бота: " INPUT_TOKEN
else
    read -r -p "Токен уже задан в settings.json. Введите новый токен или нажмите Enter, чтобы оставить текущий: " INPUT_TOKEN
fi

if command -v python3 >/dev/null 2>&1; then
    python3 - "${INPUT_TOKEN:-}" "$SUBDIR_SETTINGS" << 'PYEOF'
import json
import sys
from pathlib import Path

input_token = sys.argv[1].strip()
subdir_settings = Path(sys.argv[2])

targets = [Path("settings.json")]
if subdir_settings != Path("settings.json") and subdir_settings.parent.exists():
    targets.append(subdir_settings)

src = Path("settings.json") if Path("settings.json").exists() else Path("settings.example.json")
data = json.loads(src.read_text(encoding="utf-8"))

if input_token:
    data["Token"] = input_token

if not data.get("GuildId"):
    data["GuildId"] = 1060286255629344908

ev = data.setdefault("Event", {})
if not ev.get("AnchorCategoryId"):
    ev["AnchorCategoryId"] = 1463206062424461547
if not ev.get("LogChannelId"):
    ev["LogChannelId"] = 1568748571770228917

roles = ev.setdefault("Roles", {})
role_defaults = {
    "Male": 1463204419075047606,
    "Female": 1463204426268540929,
    "EventMod": 1538176463835103323,
    "Master": 1538176463835103323,
    "Curator": 1538176463835103323,
    "Admingroup": 1538176463835103323,
    "Mentor": 1538176463835103323,
    "StaffAdmin": 1538176463835103323,
    "Developer": 1463204361583722527,
    "EventBan": 1463204461441843283,
    "Ping": 1568749136960819210,
}
for k, v in role_defaults.items():
    if not roles.get(k):
        roles[k] = v

out_str = json.dumps(data, ensure_ascii=False, indent=2) + "\n"
for t in targets:
    t.write_text(out_str, encoding="utf-8")
PYEOF
elif [ -n "$INPUT_TOKEN" ]; then
    sed -i "s/\"Token\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"Token\": \"$INPUT_TOKEN\"/" settings.json
    if [ "$SUBDIR_SETTINGS" != "settings.json" ] && [ -d "$(dirname "$SUBDIR_SETTINGS")" ]; then
        cp "settings.json" "$SUBDIR_SETTINGS"
    fi
fi

if [ -n "$INPUT_TOKEN" ]; then
    echo "[OK] Токен сохранён в settings.json"
fi

echo "[INFO] Сборка проекта под .NET 10 (Release)..."
dotnet build "$CSPROJ_PATH" -c Release --nologo

echo "[INFO] Запуск DiscordEventBot (.NET 10)..."
exec dotnet run --project "$CSPROJ_PATH" -c Release --no-build
