#!/usr/bin/env bash
# Läuft IM Container (Debian/Ubuntu, als root) und prüft install.sh von der Erstinstallation bis zur Deinstallation.
# Gestartet wird es von run-container-tests.sh; es erwartet unter /release die gebauten Pakete und unter /deploy dieses Verzeichnis.
set -uo pipefail
cd /

PASS=0
FAIL=0
OUT=""
STATUS=0
RUN_ENV=()
INSTALL_DIR="/opt/wunschkino"
DATA_DIR="/var/lib/wunschkino"
BACKUP_DIR="/var/lib/wunschkino-backups"
CONFIG_DIR="/etc/wunschkino"
UNIT="/etc/systemd/system/wunschkino.service"
SHIM="/usr/local/bin/wk-systemctl-shim"

pass() { PASS=$((PASS + 1)); printf '  ok    %s\n' "$1"; }
fail() {
  FAIL=$((FAIL + 1)); printf '  FEHLER %s\n' "$1"
  if [[ -n "${2:-}" ]]; then printf '%s\n' "$2" | sed 's/^/          /' | head -15; fi
}
assert_status() { if [[ "$2" -eq "$3" ]]; then pass "$1"; else fail "$1" "erwarteter Status $2, erhalten $3"$'\n'"$OUT"; fi; }
assert_nonzero() { if [[ "$2" -ne 0 ]]; then pass "$1"; else fail "$1" "Status war 0, erwartet war ein Fehler"$'\n'"$OUT"; fi; }
assert_contains() { if [[ "$2" == *"$3"* ]]; then pass "$1"; else fail "$1" "'$3' fehlt in:"$'\n'"$2"; fi; }
assert_not_contains() { if [[ "$2" != *"$3"* ]]; then pass "$1"; else fail "$1" "'$3' darf nicht vorkommen in:"$'\n'"$2"; fi; }
assert_equal() { if [[ "$2" == "$3" ]]; then pass "$1"; else fail "$1" "erwartet '$2', erhalten '$3'"; fi; }
assert_true() { local name="$1"; shift; if "$@"; then pass "$name"; else fail "$name" "Bedingung nicht erfüllt: $*"; fi; }

# RUN_ENV nimmt zusätzliche Umgebungsvariablen (zum Beispiel WK_PORT=8110) für genau einen Aufruf.
run_install() {
  # Die Tests wechseln oft zwischen Versionen hin und her; der Schutz vor Downgrades hat einen eigenen Abschnitt (ALLOW_DOWNGRADE=0).
  OUT="$(env "${RUN_ENV[@]}" WK_SYSTEMCTL="$SHIM" WK_DOWNLOAD_BASE="${DOWNLOAD_BASE_OVERRIDE:-file:///release}" \
    WK_ALLOW_DOWNGRADE="${ALLOW_DOWNGRADE:-1}" \
    WK_HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-60}" bash /deploy/install.sh "$@" 2>&1)"
  STATUS=$?
}
health_body() { curl -s --noproxy '*' --max-time 3 -H "Host: ${2:-localhost}" "http://127.0.0.1:$1/health"; }
health_code() { curl -s --noproxy '*' -o /dev/null -w '%{http_code}' --max-time 3 -H "Host: ${2:-localhost}" "http://127.0.0.1:$1/health"; }
mode_of() { stat -c '%a' "$1"; }
owner_of() { stat -c '%U:%G' "$1"; }
env_value() { grep -m 1 "^$1=" "${2:-$CONFIG_DIR}/wunschkino.env" | cut -d= -f2- || true; }

echo "== Vorbereitung =="
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq --no-install-recommends curl ca-certificates tar >/dev/null
HAVE_SYSTEMD_ANALYZE=0
if apt-get install -y -qq --no-install-recommends systemd >/dev/null 2>&1 && command -v systemd-analyze >/dev/null; then HAVE_SYSTEMD_ANALYZE=1; fi
# /deploy ist schreibgeschützt eingebunden; der Ersatz muss ausführbar sein, also liegt eine Kopie im Container.
cp /deploy/test/systemctl-shim.sh "$SHIM"
chmod +x "$SHIM"

echo "== Hilfe und ungültige Eingaben =="
run_install --help
assert_status "--help endet mit 0" 0 "$STATUS"
assert_contains "--help nennt --proxy" "$OUT" "--proxy"
assert_contains "--help nennt --proxy-mode" "$OUT" "--proxy-mode"
assert_contains "--help nennt --require-attestation" "$OUT" "--require-attestation"
OUT="$(cat /deploy/install.sh | bash -s -- --help 2>&1)"; STATUS=$?
assert_status "Aufruf über eine Pipe (curl | bash -s --) funktioniert" 0 "$STATUS"
assert_contains "Die Hilfe kommt auch über die Pipe" "$OUT" "--proxy"

run_install --port 0 --version v0.0.1
assert_nonzero "Port 0 wird abgelehnt" "$STATUS"
assert_contains "Port-Fehler ist verständlich" "$OUT" "Ungültiger Port"
run_install --install-dir /usr/bin/x --version v0.0.1
assert_nonzero "Systemordner als Installationsziel wird abgelehnt" "$STATUS"
run_install --install-dir /home/jemand/wk --version v0.0.1
assert_nonzero "Ein Ordner unter /home wird abgelehnt" "$STATUS"
run_install --data-dir /etc/ssl/wk --version v0.0.1
assert_nonzero "Ein Datenordner unter /etc wird abgelehnt" "$STATUS"
run_install --config-dir /var/tmp/wk --version v0.0.1
assert_nonzero "Ein Konfigurationsordner unter /var/tmp wird abgelehnt" "$STATUS"
run_install --install-dir /opt/wk --data-dir /opt/wk/daten --version v0.0.1
assert_nonzero "Ineinander liegende Ordner werden abgelehnt" "$STATUS"
run_install --version latest2
assert_nonzero "Ungültige Version wird abgelehnt" "$STATUS"
run_install --proxy 'a;rm -rf /' --version v0.0.1
assert_nonzero "Schadhafte Proxy-Angabe wird abgelehnt" "$STATUS"
run_install --bogus
assert_nonzero "Unbekannte Option wird abgelehnt" "$STATUS"
run_install --port
assert_nonzero "Option ohne Wert wird abgelehnt" "$STATUS"
run_install --user root --version v0.0.1
assert_nonzero "Der Dienst darf nicht als root laufen" "$STATUS"
run_install --user daemon --version v0.0.1
assert_nonzero "Ein fremdes Systemkonto wird nicht übernommen" "$STATUS"
assert_contains "Die Ablehnung nennt den Grund" "$OUT" "gehört nicht zu Wunschkino"
assert_true "Nach abgelehnten Eingaben ist nichts installiert" test ! -e "$INSTALL_DIR"

echo "== Fremde Ordner werden nie angefasst =="
mkdir -p /opt/andere /srv/foreign
echo wichtig > /opt/andere/datei.txt
echo wichtig > /srv/foreign/datei.txt
run_install --install-dir /opt/andere --version v0.0.1
assert_nonzero "Fremder Installationsordner wird abgelehnt" "$STATUS"
assert_true "Der fremde Installationsordner blieb unverändert" test "$(cat /opt/andere/datei.txt)" = wichtig
run_install --data-dir /srv/foreign --version v0.0.1
assert_nonzero "Fremder Datenordner wird abgelehnt" "$STATUS"
assert_equal "Der fremde Datenordner behält seine Rechte" "755" "$(mode_of /srv/foreign)"
assert_equal "Der fremde Datenordner behält seinen Besitzer" "root:root" "$(owner_of /srv/foreign)"
assert_true "Nach der Ablehnung gibt es keinen Systemnutzer" bash -c '! id wunschkino >/dev/null 2>&1'
assert_true "Nach der Ablehnung gibt es keine Konfiguration" test ! -e "$CONFIG_DIR"

echo "== Erstinstallation v0.0.1 =="
run_install --version v0.0.1 --port 8099 --proxy 10.1.2.3 --domain wunsch.example.org
assert_status "Installation endet mit 0" 0 "$STATUS"
assert_contains "Ausgabe nennt das Setup-Token" "$OUT" "Setup-Token"
assert_contains "Ausgabe warnt vor der Anmeldung ohne HTTPS" "$OUT" "nur über HTTPS"
assert_contains "Ausgabe schlägt eine Firewall-Regel für den Proxy vor" "$OUT" "ufw allow from 10.1.2.3 to any port 8099"
assert_contains "Ausgabe meldet die fehlende Firewall" "$OUT" "Keine aktive Firewall gefunden"
assert_true "Systembibliotheken (ICU) wurden nachinstalliert" bash -c "ldconfig -p | grep -q libicuuc"
assert_true "Systemnutzer existiert" id wunschkino
assert_equal "Datenordner gehört dem Dienstnutzer" "wunschkino:wunschkino" "$(owner_of "$DATA_DIR")"
assert_equal "Datenordner hat Rechte 700" "700" "$(mode_of "$DATA_DIR")"
assert_equal "Programmordner gehört root" "root:root" "$(owner_of "$INSTALL_DIR")"
assert_equal "Programm ist für den Dienstnutzer nicht beschreibbar" "root:root" "$(owner_of "$INSTALL_DIR/Wunschkino")"
assert_equal "Konfiguration hat Rechte 640" "640" "$(mode_of "$CONFIG_DIR/wunschkino.env")"
assert_equal "Konfiguration gehört root:wunschkino" "root:wunschkino" "$(owner_of "$CONFIG_DIR/wunschkino.env")"
assert_equal "Port in der Konfiguration" "8099" "$(env_value Wunschkino__Port)"
assert_equal "Proxy in der Konfiguration" "10.1.2.3" "$(env_value Wunschkino__TrustedProxies)"
assert_equal "Domain in der Konfiguration" "wunsch.example.org" "$(env_value Wunschkino__Domain)"
assert_equal "Datenordner in der Konfiguration" "$DATA_DIR" "$(env_value Wunschkino__DataDirectory)"
assert_equal "Produktionsmodus" "Production" "$(env_value ASPNETCORE_ENVIRONMENT)"
assert_true "Das Repository wurde für spätere Updates gemerkt" grep -q '/' "$CONFIG_DIR/repo"
assert_equal "Die Quelltext-Adresse wurde aus dem Repository abgeleitet" "https://github.com/dahamalp/Wunschkino" "$(env_value Wunschkino__SourceUrl)"
assert_contains "Health meldet Version 0.0.1 (lokaler Aufruf)" "$(health_body 8099)" '"version":"0.0.1"'
assert_equal "Fremder Host-Header wird abgewiesen" "400" "$(health_code 8099 evil.example)"
assert_equal "Konfigurierte Domain wird akzeptiert" "200" "$(health_code 8099 wunsch.example.org)"
assert_true "Setup-Token liegt im Datenordner" test -s "$DATA_DIR/setup-token"
assert_equal "Setup-Token ist nur für den Dienst lesbar" "600" "$(mode_of "$DATA_DIR/setup-token")"
assert_contains "Das Token steht in der Ausgabe" "$OUT" "$(cat "$DATA_DIR/setup-token")"
if [[ $HAVE_SYSTEMD_ANALYZE -eq 1 ]]; then
  verify="$(systemd-analyze verify "$UNIT" 2>&1)"
  assert_not_contains "systemd-analyze findet keine unbekannten Direktiven" "$verify" "Unknown"
  assert_not_contains "systemd-analyze findet keine Fehler in der Unit" "$verify" "Failed"
fi
assert_contains "Unit enthält die Härtung" "$(cat "$UNIT")" "NoNewPrivileges=yes"
assert_contains "Unit begrenzt die beschreibbaren Pfade" "$(cat "$UNIT")" "ReadWritePaths=$DATA_DIR"
assert_contains "Unit erlaubt Netlink für die Namensauflösung" "$(cat "$UNIT")" "AF_NETLINK"
assert_contains "Unit schaltet Coredumps ab" "$(cat "$UNIT")" "LimitCORE=0"
assert_not_contains "Port über 1024 braucht keine Capability" "$(grep '^AmbientCapabilities' "$UNIT")" "CAP_"
echo canary > "$DATA_DIR/canary.txt"

echo "== Ungültige Einstellungen lassen den laufenden Dienst unberührt =="
run_install --version v0.0.1 --proxy 192.168.1.5/24
assert_nonzero "Proxy-Netz mit Host-Bits wird abgelehnt" "$STATUS"
assert_contains "Die App-Regeln liefern die Meldung" "$OUT" "Proxy-Netz"
assert_equal "Proxy blieb unverändert" "10.1.2.3" "$(env_value Wunschkino__TrustedProxies)"
assert_contains "Dienst läuft unverändert weiter" "$(health_body 8099)" '"version":"0.0.1"'
run_install --version v0.0.1 --domain 'http://x'
assert_nonzero "Domain mit Schema wird von der App abgelehnt" "$STATUS"
assert_contains "Meldung nennt die Domain" "$OUT" "Domain"
assert_equal "Domain blieb unverändert" "wunsch.example.org" "$(env_value Wunschkino__Domain)"
assert_contains "Dienst läuft weiterhin" "$(health_body 8099)" '"version":"0.0.1"'
run_install --version v0.0.1 --proxy 192.168.1
assert_nonzero "Abgekürzte IP-Adresse wird abgelehnt" "$STATUS"
assert_true "Keine Konfigurationsreste (.bak) bleiben nach abgelehnten Einstellungen liegen" bash -c "! ls $CONFIG_DIR/*.bak >/dev/null 2>&1"

echo "== Einstellung mit Wirkung, aber ohne Funktion: Rückholen der alten Einstellungen =="
HEALTH_TIMEOUT=8 run_install --version v0.0.1 --bind 10.9.9.9
assert_nonzero "Eine nicht bindbare Adresse wird nicht übernommen" "$STATUS"
assert_contains "Die alten Einstellungen sind wieder aktiv" "$OUT" "bisherigen sind wieder aktiv"
assert_equal "Bind-Adresse wurde zurückgesetzt" "" "$(env_value Wunschkino__BindAddress)"
assert_contains "Dienst läuft wieder mit den alten Einstellungen" "$(health_body 8099)" '"version":"0.0.1"'

echo "== Erneuter Aufruf mit gleicher Version und geänderter Einstellung =="
run_install --version v0.0.1 --port 8100
assert_status "Zweiter Aufruf endet mit 0" 0 "$STATUS"
assert_contains "Bereits aktuell wird gemeldet" "$OUT" "bereits installiert"
assert_equal "Neuer Port ist gesetzt" "8100" "$(env_value Wunschkino__Port)"
assert_equal "Proxy blieb erhalten" "10.1.2.3" "$(env_value Wunschkino__TrustedProxies)"
assert_equal "Domain blieb erhalten" "wunsch.example.org" "$(env_value Wunschkino__Domain)"
assert_contains "Dienst läuft auf dem neuen Port" "$(health_body 8100)" '"version":"0.0.1"'

echo "== Einstellungen per Umgebungsvariable =="
RUN_ENV=(WK_PORT=8110)
run_install --version v0.0.1
RUN_ENV=()
assert_status "WK_PORT wird akzeptiert" 0 "$STATUS"
assert_equal "WK_PORT ist übernommen" "8110" "$(env_value Wunschkino__Port)"
run_install --version v0.0.1 --port 8100
assert_equal "Zurück auf Port 8100" "8100" "$(env_value Wunschkino__Port)"

echo "== Update auf v0.0.2 (ohne die Optionen zu wiederholen) =="
run_install --version v0.0.2
assert_status "Update endet mit 0" 0 "$STATUS"
assert_contains "Update wird gemeldet" "$OUT" "Update von v0.0.1 auf v0.0.2"
assert_equal "Installierte Version" "v0.0.2" "$(cat "$INSTALL_DIR/VERSION")"
assert_contains "Health meldet Version 0.0.2" "$(health_body 8100)" '"version":"0.0.2"'
assert_true "Datensicherung liegt außerhalb des Datenordners" bash -c "ls -d $BACKUP_DIR/*-v0.0.1 >/dev/null 2>&1"
assert_true "Sicherung enthält die Datenbank" bash -c "ls $BACKUP_DIR/*-v0.0.1/wunschkino.db >/dev/null 2>&1"
assert_equal "Sicherungsordner gehört root und ist nicht lesbar für andere" "root:root 700" "$(owner_of "$BACKUP_DIR") $(mode_of "$BACKUP_DIR")"
assert_true "Eigene Daten blieben erhalten" test "$(cat "$DATA_DIR/canary.txt")" = canary
assert_true "Alte Version wurde aufgeräumt" test ! -e "${INSTALL_DIR}.prev"
assert_equal "Proxy blieb nach dem Update erhalten" "10.1.2.3" "$(env_value Wunschkino__TrustedProxies)"
db_size_before="$(stat -c '%s' "$DATA_DIR/wunschkino.db")"
assert_true "Datenbank ist nicht leer" test "$db_size_before" -gt 0

echo "== Update v0.0.6: Die neue Version scheitert schon bei der Prüfung der Einstellungen =="
db_size_guard="$(stat -c '%s' "$DATA_DIR/wunschkino.db")"
run_install --version v0.0.6
assert_nonzero "Eine Version, die nicht startet, wird nicht installiert" "$STATUS"
assert_contains "Die Meldung sagt, dass sich die Version nicht starten lässt" "$OUT" "lässt sich nicht starten"
assert_equal "Die installierte Version blieb unverändert" "v0.0.2" "$(cat "$INSTALL_DIR/VERSION")"
assert_contains "Der Dienst läuft unverändert weiter" "$(health_body 8100)" '"version":"0.0.2"'
assert_equal "Die neue Version konnte die Datenbank bei der Prüfung nicht beschädigen" "$db_size_guard" "$(stat -c '%s' "$DATA_DIR/wunschkino.db")"
assert_true "Das entpackte Paket blieb nicht liegen" test ! -e "${INSTALL_DIR}.new"
assert_true "Keine Konfigurationsreste (.bak) bleiben liegen" bash -c "! ls $CONFIG_DIR/*.bak >/dev/null 2>&1"

echo "== Fehlgeschlagenes Update v0.0.3 (besteht die Prüfung, startet dann nicht und zerstört die Datenbank) =="
HEALTH_TIMEOUT=6 run_install --version v0.0.3
assert_nonzero "Fehlgeschlagenes Update endet mit Fehler" "$STATUS"
assert_contains "Rollback wird gemeldet" "$OUT" "läuft wieder"
assert_equal "Alte Version ist wieder installiert" "v0.0.2" "$(cat "$INSTALL_DIR/VERSION")"
assert_contains "Alte Version antwortet wieder" "$(health_body 8100)" '"version":"0.0.2"'
assert_true "Datenbank wurde aus der Sicherung wiederhergestellt" test "$(stat -c '%s' "$DATA_DIR/wunschkino.db")" -gt 0
assert_true "Fehlgeschlagene Version blieb nicht liegen" test ! -e "${INSTALL_DIR}.prev"
assert_true "Neue Version blieb nicht liegen" test ! -e "${INSTALL_DIR}.new"

echo "== Fehlgeschlagenes Update v0.0.5 (Prozess läuft, wird aber nie gesund) =="
HEALTH_TIMEOUT=5 run_install --version v0.0.5
assert_nonzero "Hängende Version wird zurückgerollt" "$STATUS"
assert_contains "Rollback wird gemeldet" "$OUT" "läuft wieder"
assert_equal "Alte Version ist wieder installiert" "v0.0.2" "$(cat "$INSTALL_DIR/VERSION")"
assert_contains "Alte Version antwortet wieder" "$(health_body 8100)" '"version":"0.0.2"'

echo "== Beschädigter Download v0.0.4 (falsche Prüfsumme) =="
run_install --version v0.0.4
assert_nonzero "Falsche Prüfsumme bricht ab" "$STATUS"
assert_contains "Prüfsummenfehler ist verständlich" "$OUT" "Prüfsumme"
assert_equal "Installierte Version bleibt unverändert" "v0.0.2" "$(cat "$INSTALL_DIR/VERSION")"
assert_contains "Dienst läuft weiter" "$(health_body 8100)" '"version":"0.0.2"'
assert_equal "Proxy blieb nach dem Fehlversuch unverändert" "10.1.2.3" "$(env_value Wunschkino__TrustedProxies)"

echo "== Nicht vorhandene Version =="
run_install --version v9.9.9
assert_nonzero "Unbekannte Version bricht ab" "$STATUS"
assert_equal "Installierte Version bleibt unverändert" "v0.0.2" "$(cat "$INSTALL_DIR/VERSION")"

echo "== Einstellung entfernen =="
run_install --version v0.0.2 --domain ''
assert_status "Leerer Wert endet mit 0" 0 "$STATUS"
assert_equal "Domain wurde entfernt" "" "$(env_value Wunschkino__Domain)"
assert_equal "Fremder Host ist jetzt erlaubt" "200" "$(health_code 8100 evil.example)"
run_install --version v0.0.2 --domain 'wunsch.example.org'
run_install --version v0.0.2 --domain '   '
assert_status "Nur Leerzeichen entfernen die Einstellung ebenfalls" 0 "$STATUS"
assert_equal "Domain ist leer" "" "$(env_value Wunschkino__Domain)"

echo "== Port unter 1024 =="
run_install --version v0.0.2 --port 80
assert_status "Port 80 wird installiert" 0 "$STATUS"
assert_contains "Unit erlaubt das Binden niedriger Ports" "$(grep '^AmbientCapabilities' "$UNIT")" "CAP_NET_BIND_SERVICE"

echo "== Betriebsart des Proxys =="
run_install --version v0.0.2 --proxy-mode sonstwas
assert_nonzero "Eine unbekannte Betriebsart wird abgelehnt" "$STATUS"
run_install --version v0.0.2 --proxy-mode local --proxy 10.1.2.3
assert_nonzero "local mit --proxy wird abgelehnt" "$STATUS"
assert_contains "Die Meldung nennt den Widerspruch" "$OUT" "passt nicht zu --proxy"
run_install --version v0.0.2 --proxy-mode local --bind '*'
assert_nonzero "local mit einer offenen Bind-Adresse wird abgelehnt" "$STATUS"
assert_contains "Die Meldung nennt die Bind-Adresse" "$OUT" "passt nicht zu --bind"
run_install --version v0.0.2 --proxy-mode remote --bind 127.0.0.1
assert_nonzero "remote mit Loopback-Bind wird abgelehnt" "$STATUS"
run_install --version v0.0.2 --proxy-mode remote --proxy ''
assert_nonzero "remote ohne Proxy-Adresse (leeres --proxy) wird abgelehnt" "$STATUS"
run_install --version v0.0.2 --proxy-mode remote --proxy 0.0.0.0/0
assert_nonzero "Ein viel zu großes Proxy-Netz wird abgelehnt" "$STATUS"
assert_contains "Die Meldung nennt das zu große Netz" "$OUT" "viel zu groß"
assert_equal "Der Proxy blieb nach den abgelehnten Aufrufen unverändert" "10.1.2.3" "$(env_value Wunschkino__TrustedProxies)"

run_install --version v0.0.2 --proxy-mode local
assert_status "local endet mit 0" 0 "$STATUS"
assert_equal "local bindet nur an 127.0.0.1" "127.0.0.1" "$(env_value Wunschkino__BindAddress)"
assert_equal "local entfernt die Proxy-Liste" "" "$(env_value Wunschkino__TrustedProxies)"
assert_contains "Die Zusammenfassung nennt die lokale Erreichbarkeit" "$OUT" "nur von diesem Server aus erreichbar"
assert_not_contains "local verlangt keinen Proxy-Eintrag" "$OUT" "Proxy vertrauen"
assert_not_contains "local warnt nicht vor einer offenen Einrichtung" "$OUT" "Einrichtung ist offen"
assert_not_contains "local braucht keinen Firewall-Hinweis" "$OUT" "Keine aktive Firewall"
assert_contains "Der Dienst läuft im local-Modus" "$(health_body 80)" '"status":"ok"'

run_install --version v0.0.2 --proxy-mode remote
assert_nonzero "remote ohne bekannte Proxy-Adresse wird abgelehnt" "$STATUS"
assert_contains "Die Meldung nennt --proxy" "$OUT" "--proxy <IP-des-Proxy-Servers>"
assert_equal "Die Bind-Adresse blieb nach der Ablehnung unverändert" "127.0.0.1" "$(env_value Wunschkino__BindAddress)"

run_install --version v0.0.2 --proxy-mode remote --proxy 10.1.2.3
assert_status "remote mit Proxy endet mit 0" 0 "$STATUS"
assert_equal "remote setzt den Proxy" "10.1.2.3" "$(env_value Wunschkino__TrustedProxies)"
assert_equal "remote hebt die Loopback-Bindung wieder auf" "" "$(env_value Wunschkino__BindAddress)"
assert_contains "Die Zusammenfassung nennt die Adresse im Netz" "$OUT" "Adresse im Netz"
assert_not_contains "Mit Proxy gibt es keine Warnung vor der offenen Einrichtung" "$OUT" "Einrichtung ist offen"
assert_contains "Ohne Domain kommt der Hinweis zur Domain" "$OUT" "Hinweis zur Domain"

echo "== Offene Einrichtung ohne Proxy-Angabe: deutliche Warnung =="
run_install --version v0.0.2 --proxy ''
assert_status "Entfernen des Proxys endet mit 0" 0 "$STATUS"
assert_contains "Die offene Einrichtung wird gemeldet" "$OUT" "Einrichtung ist offen"
assert_contains "Der Ausweg --proxy-mode local wird genannt" "$OUT" "--proxy-mode local"
run_install --version v0.0.2 --proxy 10.1.2.3

echo "== Herkunftsprüfung des Pakets =="
run_install --version v0.0.1 --require-attestation vielleicht
assert_nonzero "Ein ungültiger Wert für --require-attestation wird abgelehnt" "$STATUS"
run_install --version v0.0.1
assert_status "Ohne gh gelingt die Installation mit Hinweis (auto)" 0 "$STATUS"
assert_contains "Der Hinweis nennt die fehlende GitHub-CLI" "$OUT" "GitHub-CLI (gh) fehlt"
run_install --version v0.0.2 --require-attestation true
assert_nonzero "true ohne gh bricht ab" "$STATUS"
assert_contains "Die Meldung nennt die fehlende GitHub-CLI" "$OUT" "GitHub-CLI (gh) fehlt"
assert_equal "Die Version blieb nach dem Abbruch unverändert" "v0.0.1" "$(cat "$INSTALL_DIR/VERSION")"

GH_STUB=/usr/local/bin/gh
# Die einfachen Anführungszeichen sind Absicht: $@ und GH_STUB_EXIT gehören in den erzeugten gh-Ersatz.
# shellcheck disable=SC2016
printf '#!/bin/sh\ncase "$*" in *--help*) exit 0 ;; esac\necho "$@" > /tmp/gh-args\nexit "${GH_STUB_EXIT:-0}"\n' > "$GH_STUB"
chmod 755 "$GH_STUB"
rm -f /tmp/gh-args
run_install --version v0.0.2
assert_status "Mit erfolgreicher Prüfung gelingt das Update (auto)" 0 "$STATUS"
assert_contains "Die bestätigte Herkunft wird gemeldet" "$OUT" "Herkunft bestätigt"
assert_contains "gh wird mit attestation verify aufgerufen" "$(cat /tmp/gh-args)" "attestation verify"
assert_contains "gh bekommt das Repository" "$(cat /tmp/gh-args)" "--repo dahamalp/Wunschkino"
assert_contains "Die Prüfung ist an den Release-Workflow gebunden" "$(cat /tmp/gh-args)" "--signer-workflow dahamalp/Wunschkino/.github/workflows/release.yml"
assert_contains "Die Prüfung ist an den Tag der Version gebunden" "$(cat /tmp/gh-args)" "--source-ref refs/tags/v0.0.2"
assert_contains "Selbst betriebene Runner werden abgelehnt" "$(cat /tmp/gh-args)" "--deny-self-hosted-runners"
assert_equal "Die Version ist aktualisiert" "v0.0.2" "$(cat "$INSTALL_DIR/VERSION")"

RUN_ENV=(GH_STUB_EXIT=1)
run_install --version v0.0.1
assert_nonzero "Eine fehlgeschlagene Prüfung bricht ab (auto)" "$STATUS"
assert_contains "Die Meldung sagt, dass die Herkunft nicht bestätigt wurde" "$OUT" "nicht bestätigt werden"
assert_contains "Die Meldung nennt den Ausweg" "$OUT" "--require-attestation false"
assert_equal "Die Version blieb nach der fehlgeschlagenen Prüfung unverändert" "v0.0.2" "$(cat "$INSTALL_DIR/VERSION")"
assert_true "Es blieb kein entpacktes Paket liegen" test ! -e "${INSTALL_DIR}.new"
rm -f /tmp/gh-args
run_install --version v0.0.1 --require-attestation false
assert_status "false überspringt die Prüfung" 0 "$STATUS"
assert_contains "Das Abschalten wird gemeldet" "$OUT" "abgeschaltet"
assert_true "gh wurde bei false nicht aufgerufen" test ! -e /tmp/gh-args
RUN_ENV=(GH_STUB_EXIT=1 WK_REQUIRE_ATTESTATION=false)
run_install --version v0.0.2
RUN_ENV=()
assert_status "WK_REQUIRE_ATTESTATION=false wird übernommen" 0 "$STATUS"
assert_equal "Die Version ist wieder v0.0.2" "v0.0.2" "$(cat "$INSTALL_DIR/VERSION")"
# Ein gh ohne „attestation“ (ältere Distributionspakete) kann nicht prüfen: Das ist etwas anderes als eine negative Prüfung.
printf '#!/bin/sh\nexit 1\n' > "$GH_STUB"
chmod 755 "$GH_STUB"
run_install --version v0.0.1 --require-attestation true
assert_nonzero "Ein zu altes gh bricht bei true ab" "$STATUS"
assert_contains "Die Meldung nennt das zu alte gh" "$OUT" "zu alt"
run_install --version v0.0.1
assert_status "Ein zu altes gh genügt bei auto, mit Hinweis" 0 "$STATUS"
assert_contains "Der Hinweis nennt das zu alte gh" "$OUT" "fehlt oder zu alt ist"
run_install --version v0.0.2 --require-attestation false
assert_equal "Die Version ist wieder v0.0.2 (nach dem Test mit dem alten gh)" "v0.0.2" "$(cat "$INSTALL_DIR/VERSION")"
rm -f "$GH_STUB" /tmp/gh-args

echo "== Hilfsbefehl wunschkino-admin =="
ADMIN=/usr/local/bin/wunschkino-admin
assert_true "Der Hilfsbefehl ist angelegt" test -x "$ADMIN"
assert_true "Der Hilfsbefehl wechselt ins Programmverzeichnis" grep -qxF "cd '$INSTALL_DIR'" "$ADMIN"
assert_true "Die Markierung steht in einer eigenen Zeile" grep -qxF "# wunschkino-managed-admin-command" "$ADMIN"
assert_true "Es blieb keine Hilfsdatei im Zielordner liegen" bash -c '! ls /usr/local/bin/.wunschkino-admin.* >/dev/null 2>&1'
assert_equal "Der Hilfsbefehl gehört root und ist nicht beschreibbar für andere" "root:root 755" "$(owner_of "$ADMIN") $(mode_of "$ADMIN")"
assert_contains "Die Zusammenfassung nennt den Hilfsbefehl" "$OUT" "wunschkino-admin"
OUT="$("$ADMIN" --list-blocks 2>&1)"; STATUS=$?
assert_status "--list-blocks läuft über den Hilfsbefehl" 0 "$STATUS"
assert_contains "Die Liste ist leer" "$OUT" "Keine Adresse"
OUT="$("$ADMIN" --unblock 203.0.113.9 2>&1)"; STATUS=$?
assert_status "Das Entsperren einer unbekannten Adresse meldet einen Fehler" 1 "$STATUS"
assert_contains "Die Meldung nennt die fehlende Adresse" "$OUT" "steht nicht in der Liste"
OUT="$("$ADMIN" --unblock-all 2>&1)"; STATUS=$?
assert_status "--unblock-all läuft" 0 "$STATUS"
assert_contains "Die Zahl der entsperrten Adressen steht in der Ausgabe" "$OUT" "0 Adressen entsperrt"
OUT="$("$ADMIN" --list-users 2>&1)"; STATUS=$?
assert_status "--list-users läuft über den Hilfsbefehl" 0 "$STATUS"
assert_contains "Ohne abgeschlossene Einrichtung gibt es noch keine Benutzer" "$OUT" "noch keine Benutzer"
OUT="$(printf 'ein-ausreichend-langes-passwort\n' | "$ADMIN" --reset-password niemand 2>&1)"; STATUS=$?
assert_status "Das Zurücksetzen für einen unbekannten Benutzer meldet einen Fehler" 1 "$STATUS"
assert_contains "Die Meldung nennt den fehlenden Benutzer" "$OUT" "gibt es nicht"
OUT="$("$ADMIN" --reset-password 2>&1)"; STATUS=$?
assert_status "--reset-password ohne Namen zeigt die Verwendung" 2 "$STATUS"
assert_true "Die Datenbank gehört nach den Befehlen weiter dem Dienstnutzer" test "$(stat -c '%U' "$DATA_DIR/wunschkino.db")" = wunschkino
OUT="$(runuser -u nobody -- "$ADMIN" --list-blocks 2>&1)"; STATUS=$?
assert_nonzero "Ohne root ist der Hilfsbefehl nicht nutzbar" "$STATUS"
printf '#!/bin/sh\necho fremd\n' > /usr/local/bin/wk-fremd
chmod 755 /usr/local/bin/wk-fremd
RUN_ENV=(WK_ADMIN_COMMAND=/usr/local/bin/wk-fremd)
run_install --version v0.0.2
RUN_ENV=()
assert_contains "Ein fremder Befehl gleichen Namens bleibt unberührt" "$(cat /usr/local/bin/wk-fremd)" "echo fremd"
assert_contains "Die Warnung nennt den fremden Befehl" "$OUT" "stammt nicht von diesem Script"
rm -f /usr/local/bin/wk-fremd

echo "== Schutz vor Downgrades =="
ALLOW_DOWNGRADE=0 run_install --version v0.0.1
assert_nonzero "Eine ältere Version wird ohne Erlaubnis abgelehnt" "$STATUS"
assert_contains "Die Meldung nennt den Downgrade" "$OUT" "Downgrade"
assert_equal "Die installierte Version blieb unverändert" "v0.0.2" "$(cat "$INSTALL_DIR/VERSION")"
ALLOW_DOWNGRADE=0 run_install --version v0.0.2
assert_status "Dieselbe Version darf erneut eingespielt werden" 0 "$STATUS"
ALLOW_DOWNGRADE=0 run_install --version v0.0.1 --allow-downgrade
assert_status "Mit --allow-downgrade geht die ältere Version" 0 "$STATUS"
assert_equal "Die ältere Version ist installiert" "v0.0.1" "$(cat "$INSTALL_DIR/VERSION")"
ALLOW_DOWNGRADE=0 run_install --version v0.0.2
assert_status "Ein Update auf eine neuere Version braucht keine Erlaubnis" 0 "$STATUS"

echo "== Pakete mit Sondereinträgen werden abgelehnt =="
mkdir -p /tmp/special/src /tmp/special/rel/v0.0.7
tar -xzf /release/v0.0.2/wunschkino-v0.0.2-linux-x64.tar.gz -C /tmp/special/src
mkfifo /tmp/special/src/pipe
tar --owner=0 --group=0 --numeric-owner -C /tmp/special/src -czf /tmp/special/rel/v0.0.7/wunschkino-v0.0.7-linux-x64.tar.gz .
(cd /tmp/special/rel/v0.0.7 && sha256sum wunschkino-v0.0.7-linux-x64.tar.gz > SHA256SUMS)
DOWNLOAD_BASE_OVERRIDE=file:///tmp/special/rel run_install --version v0.0.7
assert_nonzero "Ein Paket mit einer Pipe wird abgelehnt" "$STATUS"
assert_contains "Die Meldung nennt die Sondereinträge" "$OUT" "Sondereinträge"
assert_equal "Die installierte Version blieb unverändert" "v0.0.2" "$(cat "$INSTALL_DIR/VERSION")"
rm -rf /tmp/special

echo "== Gleichzeitige Läufe =="
exec 8> /run/wunschkino-install.lock
flock -n 8
run_install --version v0.0.2
assert_nonzero "Ein zweiter Lauf wird abgewiesen" "$STATUS"
assert_contains "Die Meldung nennt den aktiven Lauf" "$OUT" "noch aktiv"
exec 8>&-

echo "== Deinstallation =="
run_install --uninstall
assert_status "Deinstallation endet mit 0" 0 "$STATUS"
assert_true "Programmdateien sind entfernt" test ! -e "$INSTALL_DIR"
assert_true "Unit ist entfernt" test ! -e "$UNIT"
assert_true "Der Hilfsbefehl ist entfernt" test ! -e /usr/local/bin/wunschkino-admin
assert_true "Daten bleiben erhalten" test -e "$DATA_DIR/canary.txt"
assert_true "Konfiguration bleibt erhalten" test -e "$CONFIG_DIR/wunschkino.env"
assert_true "Systemnutzer bleibt erhalten" id wunschkino

echo "== Löschen nur mit Markierung =="
mkdir -p /srv/fremd/daten
echo wichtig > /srv/fremd/daten/datei.txt
run_install --uninstall --purge --data-dir /srv/fremd/daten
assert_status "Purge mit fremdem Datenordner endet mit 0" 0 "$STATUS"
assert_true "Ordner ohne Markierung wird nicht gelöscht" test -e /srv/fremd/daten/datei.txt
assert_contains "Warnung nennt die fehlende Markierung" "$OUT" "Markierung"

echo "== Vollständiges Entfernen =="
run_install --uninstall --purge
assert_status "Purge endet mit 0" 0 "$STATUS"
assert_true "Datenordner ist gelöscht" test ! -e "$DATA_DIR"
assert_true "Sicherungen sind gelöscht" test ! -e "$BACKUP_DIR"
assert_true "Konfiguration ist gelöscht" test ! -e "$CONFIG_DIR"
assert_true "Systemnutzer ist gelöscht" bash -c '! id wunschkino >/dev/null 2>&1'
assert_true "Fremde Ordner blieben unberührt" test "$(cat /opt/andere/datei.txt)" = wichtig

echo "== Eigene Pfade: Updates und Deinstallation übernehmen das bestehende Layout =="
run_install --version v0.0.1 --install-dir /opt/wk-app --data-dir /var/lib/wk-data --config-dir /etc/wk-conf --user wkuser --port 8120 --repo someone/Wunschkino
assert_status "Installation mit eigenen Pfaden endet mit 0" 0 "$STATUS"
assert_true "Eigener Installationsordner" test -x /opt/wk-app/Wunschkino
assert_equal "Eigener Datenordner gehört dem eigenen Konto" "wkuser:wkuser" "$(owner_of /var/lib/wk-data)"
assert_equal "Der Datenordner steht in der Konfiguration" "/var/lib/wk-data" "$(env_value Wunschkino__DataDirectory /etc/wk-conf)"
assert_equal "Das Repository wurde gemerkt" "someone/Wunschkino" "$(cat /etc/wk-conf/repo)"
assert_equal "Ein Fork zeigt automatisch auf sein eigenes Repository" "https://github.com/someone/Wunschkino" "$(env_value Wunschkino__SourceUrl /etc/wk-conf)"

run_install --version v0.0.2
assert_status "Update ohne Pfad-Optionen endet mit 0" 0 "$STATUS"
assert_true "Es entstand keine zweite Installation unter den Standardpfaden" test ! -e "$INSTALL_DIR"
assert_true "Der Standard-Datenordner wurde nicht angelegt" test ! -e "$DATA_DIR"
assert_true "Der Standard-Systemnutzer wurde nicht angelegt" bash -c '! id wunschkino >/dev/null 2>&1'
assert_equal "Eigene Installation ist aktualisiert" "v0.0.2" "$(cat /opt/wk-app/VERSION)"
assert_contains "Dienst läuft auf dem gemerkten Port" "$(health_body 8120)" '"version":"0.0.2"'
assert_equal "Das Repository blieb gemerkt" "someone/Wunschkino" "$(cat /etc/wk-conf/repo)"
assert_equal "Die Unit verweist weiter auf den eigenen Ordner" "ExecStart=/opt/wk-app/Wunschkino" "$(grep '^ExecStart=' "$UNIT")"

assert_equal "Die abgeleitete Quelltext-Adresse blieb beim Update erhalten" "https://github.com/someone/Wunschkino" "$(env_value Wunschkino__SourceUrl /etc/wk-conf)"
run_install --version v0.0.2 --source-url https://codeberg.org/jemand/fork
assert_status "Eine eigene Quelltext-Adresse wird übernommen" 0 "$STATUS"
assert_equal "Die eigene Quelltext-Adresse steht in der Konfiguration" "https://codeberg.org/jemand/fork" "$(env_value Wunschkino__SourceUrl /etc/wk-conf)"
run_install --version v0.0.2
assert_equal "Eine ausdrücklich gesetzte Adresse bleibt bei Updates erhalten" "https://codeberg.org/jemand/fork" "$(env_value Wunschkino__SourceUrl /etc/wk-conf)"
run_install --version v0.0.2 --source-url http://unsicher.example/x
assert_nonzero "Eine Quelltext-Adresse ohne https wird abgelehnt" "$STATUS"
assert_contains "Die Meldung nennt die Quelltext-Adresse" "$OUT" "Quelltext-Adresse"
run_install --version v0.0.2 --source-url 'https://a.example/x y'
assert_nonzero "Eine Quelltext-Adresse mit Leerzeichen wird abgelehnt" "$STATUS"
run_install --version v0.0.2 --source-url ''
assert_equal "Ein leerer Wert leitet die Adresse wieder aus dem Repository ab" "https://github.com/someone/Wunschkino" "$(env_value Wunschkino__SourceUrl /etc/wk-conf)"
run_install --version v0.0.2 --source-url https://codeberg.org/jemand/fork
run_install --version v0.0.2 --repo someone/Wunschkino
assert_equal "Eine eigene Adresse wird auch bei erneuter Angabe von --repo nicht überschrieben" "https://codeberg.org/jemand/fork" "$(env_value Wunschkino__SourceUrl /etc/wk-conf)"
run_install --version v0.0.2 --source-url ''
RUN_ENV=(WK_SOURCE_URL=https://git.example.org/env/test)
run_install --version v0.0.2
RUN_ENV=()
assert_equal "WK_SOURCE_URL wird übernommen" "https://git.example.org/env/test" "$(env_value Wunschkino__SourceUrl /etc/wk-conf)"
run_install --version v0.0.2 --source-url ''
assert_equal "Zurück auf die abgeleitete Adresse" "https://github.com/someone/Wunschkino" "$(env_value Wunschkino__SourceUrl /etc/wk-conf)"

run_install --version v0.0.2 --install-dir /opt/anderer-ort
assert_nonzero "Ein abweichender Pfad bei bestehender Installation wird abgelehnt" "$STATUS"
assert_contains "Die Meldung nennt den Ausweg" "$OUT" "--uninstall"
assert_equal "Die Installation blieb unverändert" "v0.0.2" "$(cat /opt/wk-app/VERSION)"

run_install --uninstall --purge
assert_status "Entfernen ohne Pfad-Optionen endet mit 0" 0 "$STATUS"
assert_true "Eigener Installationsordner ist gelöscht" test ! -e /opt/wk-app
assert_true "Eigener Datenordner ist gelöscht" test ! -e /var/lib/wk-data
assert_true "Eigene Konfiguration ist gelöscht" test ! -e /etc/wk-conf
assert_true "Eigenes Konto ist gelöscht" bash -c '! id wkuser >/dev/null 2>&1'

echo
echo "Ergebnis: $PASS bestanden, $FAIL fehlgeschlagen"
[[ $FAIL -eq 0 ]]
