#!/usr/bin/env bash
# Prüft deploy/install.sh in echten Debian- und Ubuntu-Containern (Erstinstallation, Update, Rollback, Deinstallation).
#
# Voraussetzungen: Docker und das .NET-SDK. Verwendung (aus beliebigem Ordner):
#   deploy/test/run-container-tests.sh [Image …]      Standard: debian:12 ubuntu:24.04
#
# Die Pakete baut dieses Script selbst: v0.0.1 und v0.0.2 (gültig), v0.0.3 (startet nicht und zerstört die Datenbank,
# prüft den Rollback) und v0.0.4 (falsche Prüfsumme). Der Dienststart läuft über einen systemctl-Ersatz, weil Container
# kein systemd haben; die Unit-Datei selbst prüft systemd-analyze.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf -- "$WORK"' EXIT

# Unter Git Bash (Windows) brauchen Docker und dotnet.exe Windows-Pfade, und Git Bash darf sie nicht umschreiben.
export MSYS_NO_PATHCONV=1
docker_path() { (cd "$1" && (pwd -W 2>/dev/null || pwd)); }
WORK_NATIVE="$(docker_path "$WORK")"
ROOT_NATIVE="$(docker_path "$ROOT")"

IMAGES=("$@")
if [[ ${#IMAGES[@]} -eq 0 ]]; then IMAGES=("debian:12" "ubuntu:24.04"); fi
PACK_IMAGE="${IMAGES[0]}"

publish() {
  local version="$1"
  echo "==> Baue Version $version"
  dotnet publish "$ROOT_NATIVE/Wunschkino/Wunschkino.csproj" --configuration Release --runtime linux-x64 --self-contained true \
    -p:Version="$version" --output "$WORK_NATIVE/pub-$version" --nologo -v quiet
}

# Packt in einem Linux-Container: Unter Windows gingen sonst die Ausführungsrechte der Programmdatei verloren.
pack() {
  local version="$1" source="$2"
  mkdir -p "$WORK/release/v$version"
  docker run --rm -v "$WORK_NATIVE:/w" "$PACK_IMAGE" bash -c "
    set -e
    chmod 755 /w/$source/Wunschkino
    tar --owner=0 --group=0 --numeric-owner -C /w/$source -czf /w/release/v$version/wunschkino-v$version-linux-x64.tar.gz .
    cd /w/release/v$version && sha256sum wunschkino-v$version-linux-x64.tar.gz > SHA256SUMS"
}

publish 0.0.1
publish 0.0.2
pack 0.0.1 pub-0.0.1
pack 0.0.2 pub-0.0.2

echo "==> Baue die Testpakete v0.0.3 (startet nicht), v0.0.5 (hängt), v0.0.6 (scheitert schon der Prüfung) und v0.0.4 (falsche Prüfsumme)"
cp -r "$WORK/pub-0.0.2" "$WORK/pub-0.0.3"
# shellcheck disable=SC2016
printf '#!/bin/sh\n# Simuliert eine Version, die die Prüfung besteht, dann aber nicht startet und die Datenbank zerstört.\n[ "$1" = "--validate-config" ] && exit 0\n: > /var/lib/wunschkino/wunschkino.db\nexit 1\n' > "$WORK/pub-0.0.3/Wunschkino"
pack 0.0.3 pub-0.0.3
cp -r "$WORK/pub-0.0.2" "$WORK/pub-0.0.5"
# shellcheck disable=SC2016
printf '#!/bin/sh\n# Simuliert eine hängende Version: Die Prüfung besteht, der Prozess läuft dann, wird aber nie gesund.\n[ "$1" = "--validate-config" ] && exit 0\nexec sleep 600\n' > "$WORK/pub-0.0.5/Wunschkino"
pack 0.0.5 pub-0.0.5
cp -r "$WORK/pub-0.0.2" "$WORK/pub-0.0.6"
# Die einfachen Anführungszeichen in den printf-Zeilen sind Absicht: $1 gehört in die erzeugten Test-Programme.
# shellcheck disable=SC2016
printf '#!/bin/sh\n# Simuliert eine Version, die schon bei der Prüfung scheitert und dabei versucht, die Datenbank zu zerstören.\nexec 2>/dev/null\n: > /var/lib/wunschkino/wunschkino.db\nexit 1\n' > "$WORK/pub-0.0.6/Wunschkino"
pack 0.0.6 pub-0.0.6
mkdir -p "$WORK/release/v0.0.4"
cp "$WORK/release/v0.0.2/wunschkino-v0.0.2-linux-x64.tar.gz" "$WORK/release/v0.0.4/wunschkino-v0.0.4-linux-x64.tar.gz"
printf '%s  %s\n' "0000000000000000000000000000000000000000000000000000000000000000" "wunschkino-v0.0.4-linux-x64.tar.gz" \
  > "$WORK/release/v0.0.4/SHA256SUMS"

failed=0
for image in "${IMAGES[@]}"; do
  echo
  echo "################ $image ################"
  if ! docker run --rm \
      -v "$WORK_NATIVE/release:/release:ro" \
      -v "$ROOT_NATIVE/deploy:/deploy:ro" \
      "$image" bash /deploy/test/container-test.sh; then
    failed=1
  fi
done

echo
if [[ $failed -eq 0 ]]; then echo "Alle Container-Tests bestanden."; else echo "Container-Tests FEHLGESCHLAGEN."; fi
exit $failed
