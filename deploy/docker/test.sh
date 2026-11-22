#!/usr/bin/env bash
# Prüft das Docker-Image: Start, Gesundheitsprüfung, Nicht-Root, schreibgeschütztes Programm, Datenerhalt über Neustart und
# Neuanlage des Containers (so läuft ein Update), Ablehnung einer ungültigen Konfiguration.
#
# Voraussetzungen: Docker und das .NET-SDK (nur ohne fertiges Paket). Verwendung (aus beliebigem Ordner):
#   deploy/docker/test.sh [Paket-linux-x64.tar.gz]
# Ohne Argument baut das Script das Paket selbst. Getestet wird die Architektur des Docker-Hosts (amd64 oder arm64).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d)"
SUFFIX="$$"
IMAGE="wunschkino-test:$SUFFIX"
NAME="wunschkino-test-$SUFFIX"
VOLUME="wunschkino-test-data-$SUFFIX"

cleanup() {
  docker rm -f "$NAME" > /dev/null 2>&1 || true
  docker volume rm "$VOLUME" > /dev/null 2>&1 || true
  docker rmi "$IMAGE" > /dev/null 2>&1 || true
  rm -rf -- "$WORK"
}
trap cleanup EXIT

# Unter Git Bash (Windows) brauchen Docker und dotnet.exe Windows-Pfade, und Git Bash darf sie nicht umschreiben.
export MSYS_NO_PATHCONV=1
native_path() { (cd "$1" && (pwd -W 2>/dev/null || pwd)); }

fail() { echo "FEHLER: $*" >&2; docker logs "$NAME" 2>&1 | tail -n 30 >&2 || true; exit 1; }
ok() { echo "  ok: $*"; }

case "$(docker version --format '{{.Server.Arch}}' 2>/dev/null || true)" in
  arm64|aarch64) ARCH=arm64 ;;
  *) ARCH=amd64 ;;
esac

mkdir -p "$WORK/context"
if [[ $# -ge 1 ]]; then
  cp "$1" "$WORK/context/package-$ARCH.tar.gz"
else
  RID="linux-x64"; [[ "$ARCH" == "arm64" ]] && RID="linux-arm64"
  echo "==> Baue das Paket ($RID)"
  dotnet publish "$(native_path "$ROOT")/Wunschkino/Wunschkino.csproj" --configuration Release --runtime "$RID" --self-contained true \
    --output "$(native_path "$WORK")/publish" --nologo -v quiet
  tar -C "$WORK/publish" -czf "$WORK/context/package-$ARCH.tar.gz" .
fi

echo "==> Baue das Image"
docker build --quiet --file "$(native_path "$ROOT")/deploy/docker/Dockerfile" --tag "$IMAGE" "$(native_path "$WORK/context")" > /dev/null

start_container() {
  docker run -d --name "$NAME" --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges:true \
    -p 127.0.0.1::8080 -v "$VOLUME:/data" \
    -e Wunschkino__TrustedProxies=172.16.0.0/12 "$IMAGE" > /dev/null
}

wait_healthy() {
  local state i
  for (( i = 0; i < 90; i++ )); do
    state="$(docker inspect --format '{{.State.Health.Status}}' "$NAME" 2>/dev/null || echo gone)"
    [[ "$state" == "healthy" ]] && return 0
    [[ "$(docker inspect --format '{{.State.Running}}' "$NAME" 2>/dev/null || echo false)" == "true" ]] || fail "Der Container läuft nicht mehr."
    sleep 1
  done
  fail "Der Container wurde nicht gesund (Status: $state)."
}

echo "==> Start und Gesundheitsprüfung"
start_container
wait_healthy
ok "Container ist gesund (Docker-HEALTHCHECK)"

port="$(docker port "$NAME" 8080/tcp | head -n 1 | sed 's/.*://')"
curl --silent --fail --noproxy '*' --max-time 5 "http://127.0.0.1:$port/health" | grep -q '"status":"ok"' || fail "/health vom Host aus nicht ok."
ok "/health antwortet vom Host aus"

echo "==> Rechte"
[[ "$(docker exec "$NAME" id -u)" != "0" ]] || fail "Der Prozess läuft als root."
ok "läuft nicht als root"
if docker exec "$NAME" bash -c ': > /app/schreibtest' 2> /dev/null; then fail "Das Programmverzeichnis ist beschreibbar."; fi
ok "Programmverzeichnis ist schreibgeschützt"
docker exec "$NAME" test -s /data/setup-token || fail "Das Setup-Token fehlt in /data."
ok "Setup-Token liegt in /data und ist lesbar (docker exec … cat /data/setup-token)"
docker exec "$NAME" bash -c 'ls /data/*.db' > /dev/null || fail "Keine Datenbank in /data."
ok "Datenbank liegt in /data"

echo "==> Daten bleiben erhalten"
docker exec "$NAME" bash -c 'echo bleibt > /data/marker'
docker restart "$NAME" > /dev/null
wait_healthy
docker exec "$NAME" grep -q bleibt /data/marker || fail "Nach dem Neustart fehlen die Daten."
ok "Daten nach docker restart vorhanden"
docker rm -f "$NAME" > /dev/null
start_container
wait_healthy
docker exec "$NAME" grep -q bleibt /data/marker || fail "Nach Neuanlage des Containers fehlen die Daten."
docker exec "$NAME" bash -c 'ls /data/*.db' > /dev/null || fail "Nach Neuanlage fehlt die Datenbank."
ok "Daten nach Neuanlage des Containers (Update) vorhanden"

echo "==> Ungültige Konfiguration"
if docker run --rm -e Wunschkino__TrustedProxies=kein-netz "$IMAGE" --validate-config > /dev/null 2>&1; then
  fail "Eine ungültige Konfiguration wurde akzeptiert."
fi
ok "ungültige Konfiguration wird abgelehnt"

echo
echo "Alle Docker-Tests bestanden."
