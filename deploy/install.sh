#!/usr/bin/env bash
# Wunschkino – Installation, Update und Deinstallation (Debian/Ubuntu mit systemd, x64 und arm64)
#
#   curl -fsSL https://github.com/dahamalp/Wunschkino/releases/latest/download/install.sh | sudo bash -s -- [Optionen]
#
# Derselbe Aufruf installiert beim ersten Mal und aktualisiert danach. Die Optionen stehen unten in usage()
# (oder: install.sh --help). Jede Option gibt es auch als Umgebungsvariable WK_<NAME>, zum Beispiel WK_PORT.

set -Eeuo pipefail
# Vorsichtiger Standard: Was dieses Script anlegt, ist zunächst nur für root lesbar. Wo andere lesen müssen, setzt es die Rechte ausdrücklich.
umask 077

readonly DEFAULT_REPO="dahamalp/Wunschkino"
readonly SERVICE_NAME="wunschkino"
readonly MARKER=".wunschkino-managed"
readonly KEEP_BACKUPS=5
readonly LOCK_FILE="/run/wunschkino-install.lock"
# Wartezeit auf einen gesunden Dienst; per WK_HEALTH_TIMEOUT kürzbar (die Tests brauchen das für den Rollback).
HEALTH_TIMEOUT_SECONDS="${WK_HEALTH_TIMEOUT:-60}"

# --- Einstellungen (Standardwerte; Umgebung und Optionen überschreiben sie) -------------------------------------
ACTION="install"
PURGE=0
REPO="${WK_REPO:-$DEFAULT_REPO}"
VERSION="${WK_VERSION:-latest}"
SERVICE_USER="${WK_USER:-wunschkino}"
INSTALL_DIR="${WK_INSTALL_DIR:-/opt/wunschkino}"
DATA_DIR="${WK_DATA_DIR:-/var/lib/wunschkino}"
CONFIG_DIR="${WK_CONFIG_DIR:-/etc/wunschkino}"
# Nur für Tests: anderer Download-Ort (zum Beispiel file:///release) und anderer systemctl-Aufruf.
DOWNLOAD_BASE="${WK_DOWNLOAD_BASE:-}"
SYSTEMCTL="${WK_SYSTEMCTL:-systemctl}"
# Betriebsart des Reverse-Proxys (local oder remote); leer = nichts ändern bzw. bei einer Neuinstallation nur warnen.
PROXY_MODE="${WK_PROXY_MODE:-}"
# Herkunftsprüfung des Pakets (GitHub-Attestierung): auto, true oder false.
REQUIRE_ATTESTATION="${WK_REQUIRE_ATTESTATION:-auto}"
# Ein Update auf eine ältere Version als die installierte lehnt das Script ab (--allow-downgrade erlaubt es bewusst).
ALLOW_DOWNGRADE="${WK_ALLOW_DOWNGRADE:-0}"
# Hilfsbefehl für den Betreiber (gesperrte Adressen entsperren); der Pfad ist nur für Tests änderbar.
ADMIN_COMMAND="${WK_ADMIN_COMMAND:-/usr/local/bin/wunschkino-admin}"
readonly ADMIN_COMMAND_MARKER="# wunschkino-managed-admin-command"

# App-Einstellungen: bewusst als assoziatives Feld, damit „nicht angegeben“ (bestehende Werte behalten) und
# „leer angegeben“ (Wert entfernen) unterscheidbar bleiben. GIVEN merkt sich, welche Pfade ausdrücklich genannt wurden.
declare -A APP_OPT=()
declare -A GIVEN=()
for opt_name in port bind domain proxy secure_cookies source_url; do
  env_name="WK_$(echo "$opt_name" | tr '[:lower:]' '[:upper:]')"
  if [[ -v "$env_name" ]]; then APP_OPT[$opt_name]="${!env_name}"; fi
done
for opt_name in install_dir data_dir config_dir user repo; do
  env_name="WK_$(echo "$opt_name" | tr '[:lower:]' '[:upper:]')"
  if [[ -v "$env_name" ]]; then GIVEN[$opt_name]=1; fi
done

ENV_FILE=""
ENV_BACKUP=""
ENV_IS_NEW=0
BACKUP_DIR=""
UNIT_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
RID=""
TAG=""
TMP_DIR=""
ASSET_PATH=""
BACKUP_PATH=""
NEW_DIR=""
VALIDATION_ERROR=""

# --- Ausgabe ----------------------------------------------------------------------------------------------------
if [[ -t 1 ]]; then
  C_RED=$'\033[31m'; C_YELLOW=$'\033[33m'; C_GREEN=$'\033[32m'; C_BOLD=$'\033[1m'; C_OFF=$'\033[0m'
else
  C_RED=""; C_YELLOW=""; C_GREEN=""; C_BOLD=""; C_OFF=""
fi
info() { printf '%s\n' "${C_GREEN}==>${C_OFF} $*"; }
warn() { printf '%s\n' "${C_YELLOW}Warnung:${C_OFF} $*" >&2; }
die() { printf '%s\n' "${C_RED}Fehler:${C_OFF} $*" >&2; exit 1; }

usage() {
  cat <<'EOF'
Wunschkino installieren, aktualisieren oder entfernen.

Verwendung (Debian/Ubuntu, als root):
  curl -fsSL https://github.com/dahamalp/Wunschkino/releases/latest/download/install.sh | sudo bash -s -- [Optionen]

Installation / Update (Standard):
  --version vX.Y.Z      Bestimmte Version statt der neuesten                    (WK_VERSION)
  --allow-downgrade     Erlaubt, eine ältere Version als die installierte zu
                        installieren. Ohne die Option bricht das Script ab: Ein
                        altes Paket mit bekannten Lücken soll sich nicht
                        unbemerkt einspielen lassen.                            (WK_ALLOW_DOWNGRADE=1)
  --port N              HTTP-Port der App, Standard 8080                         (WK_PORT)
  --bind ADRESSE        Lausch-Adresse: IP, localhost oder * (alle, Standard)    (WK_BIND)
  --domain HOSTS        Öffentliche Domain(s), kommagetrennt; beschränkt die
                        erlaubten Host-Header (localhost bleibt erlaubt)         (WK_DOMAIN)
  --proxy LISTE         IP-Adresse(n) oder Netz(e) (CIDR, mit Netzadresse) Deines
                        Reverse-Proxys, zum Beispiel 192.168.1.10 oder
                        10.0.0.0/24. Nur von ihnen übernimmt die App die echte
                        Client-IP. WICHTIG hinter einem Proxy, sonst teilen sich
                        alle das Login-Limit.                                    (WK_PROXY)
  --proxy-mode MODUS    local: Dein Reverse-Proxy läuft auf diesem Server. Die App
                        lauscht nur auf 127.0.0.1, kein Port ist von außen offen.
                        remote: Der Proxy läuft auf einem anderen Server; dann ist
                        --proxy Pflicht und die App lauscht im Netz.             (WK_PROXY_MODE)
  --require-attestation M  auto (Standard): Prüft mit der GitHub-CLI (gh), ob das
                        Paket wirklich aus dem Release-Workflow stammt, wenn gh
                        installiert ist; sonst nur ein Hinweis. true: ohne
                        erfolgreiche Prüfung abbrechen. false: nicht prüfen.     (WK_REQUIRE_ATTESTATION)
  --source-url URL      Adresse des Quelltexts, die die App allen Nutzern zeigt
                        (AGPL-3.0, Abschnitt 13). Standard: https://github.com/<--repo>.
                        Nur für andere Hosts nötig, zum Beispiel Codeberg.      (WK_SOURCE_URL)
  --secure-cookies B    true (Standard) oder false. Mit true funktioniert die
                        Anmeldung nur über HTTPS (also über den Proxy). Nur bei
                        bewusstem Betrieb ohne HTTPS im eigenen Netz false.      (WK_SECURE_COOKIES)

  Bei einem Update bleiben nicht angegebene Einstellungen und die Pfade der
  bestehenden Installation unverändert. Ein leerer Wert (zum Beispiel
  --domain '') entfernt eine Einstellung.

Pfade und Quelle (selten nötig):
  --repo OWNER/NAME     GitHub-Repository der Releases (für Forks)               (WK_REPO)
  --user NAME           Systemnutzer, Standard wunschkino                        (WK_USER)
  --install-dir PFAD    Standard /opt/wunschkino   (unter /opt, /srv, /usr/local)  (WK_INSTALL_DIR)
  --data-dir PFAD       Standard /var/lib/wunschkino (unter /var/lib, /srv, /opt)  (WK_DATA_DIR)
  --config-dir PFAD     Standard /etc/wunschkino   (unter /etc, /opt, /srv)        (WK_CONFIG_DIR)
  Datensicherungen liegen neben dem Datenordner in <Datenordner>-backups.

Entfernen:
  --uninstall           Dienst und Programmdateien entfernen; Daten und Konfiguration bleiben
  --purge               Zusammen mit --uninstall: auch Daten (Datenbank, Schlüssel, Sicherungen),
                        Konfiguration und Systemnutzer löschen

  -h, --help            Diese Hilfe
EOF
}

# --- Aufräumen und Fehlerbehandlung -----------------------------------------------------------------------------
cleanup() {
  if [[ -n "$TMP_DIR" && -d "$TMP_DIR" ]]; then rm -rf -- "$TMP_DIR"; fi
  # Ein entpacktes, aber nie in Betrieb genommenes Paket (abgelehnte Prüfung, Abbruch) soll nicht liegen bleiben.
  if [[ -n "$NEW_DIR" && -d "$NEW_DIR" ]]; then rm -rf -- "$NEW_DIR"; fi
}
on_error() {
  printf '%s\n' "${C_RED}Fehler:${C_OFF} Das Script ist in Zeile $1 abgebrochen." >&2
}
trap cleanup EXIT
trap 'on_error $LINENO' ERR

# --- Prüfungen --------------------------------------------------------------------------------------------------
# Alles, was später in Dateien, Befehle oder rm-Aufrufe fließt, wird hier streng geprüft.
valid_repo() { [[ "$1" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]]; }
valid_tag() { [[ "$1" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]]; }
# Wahr, wenn Version $1 älter als $2 ist (vMAJOR.MINOR.PATCH[-Zusatz]). Ein Zusatz („-rc.1“) macht eine Version älter als dieselbe ohne Zusatz.
version_older() {
  local a="${1#v}" b="${2#v}" core_a core_b pre_a pre_b
  core_a="${a%%-*}"; core_b="${b%%-*}"
  pre_a=""; pre_b=""
  [[ "$a" == *-* ]] && pre_a="${a#*-}"
  [[ "$b" == *-* ]] && pre_b="${b#*-}"
  if [[ "$core_a" != "$core_b" ]]; then
    [[ "$(printf '%s\n%s\n' "$core_a" "$core_b" | sort -V | head -n1)" == "$core_a" ]]
    return
  fi
  [[ "$pre_a" == "$pre_b" ]] && return 1
  [[ -z "$pre_a" ]] && return 1
  [[ -z "$pre_b" ]] && return 0
  [[ "$(printf '%s\n%s\n' "$pre_a" "$pre_b" | sort -V | head -n1)" == "$pre_a" ]]
}
valid_user() { [[ "$1" =~ ^[a-z_][a-z0-9_-]{0,30}$ ]]; }
valid_port() { [[ "$1" =~ ^[0-9]{1,5}$ ]] && (( 10#$1 >= 1 && 10#$1 <= 65535 )); }
valid_bind() { [[ "$1" == "*" || "$1" == "localhost" || "$1" =~ ^[0-9A-Fa-f:.]+$ ]]; }
valid_list() { [[ -z "$1" || "$1" =~ ^[A-Za-z0-9.:/*,_-]+$ ]]; }
valid_bool() { [[ "$1" == "true" || "$1" == "false" ]]; }
# Nur ein https-Link ohne Leerzeichen, Anführungszeichen und Dollarzeichen (die App prüft ihn beim Start noch einmal vollständig).
valid_https_url() {
  local pattern="^https://[A-Za-z0-9._~:/?#@!&()*+,;=%-]+\$"
  [[ -z "$1" || "$1" =~ $pattern ]]
}

# Absoluter Pfad ohne Sonderzeichen, direkt unter einem der erlaubten Ordner (darunter wird später gelöscht).
valid_dir_under() {
  local path="$1" prefix
  shift
  [[ "$path" =~ ^/[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)+$ ]] || return 1
  [[ "$path" != *".."* ]] || return 1
  for prefix in "$@"; do
    if [[ "$path" == "$prefix"/?* ]]; then return 0; fi
  done
  return 1
}

# Wahr, wenn einer der Pfade im anderen liegt (dann würde das Löschen des einen den anderen mitreißen).
nested() { [[ "$1" == "$2" || "$1" == "$2"/* || "$2" == "$1"/* ]]; }

require_root() {
  [[ "${EUID}" -eq 0 ]] || die "Bitte als root ausführen, zum Beispiel mit sudo."
}

# Verhindert, dass zwei Läufe gleichzeitig Dienst, Dateien und Konfiguration umbauen.
acquire_lock() {
  exec 9> "$LOCK_FILE"
  flock -n 9 || die "Ein anderer Lauf von install.sh ist noch aktiv."
}

detect_arch() {
  case "$(uname -m)" in
    x86_64|amd64) RID="linux-x64" ;;
    aarch64|arm64) RID="linux-arm64" ;;
    *) die "Nicht unterstützte Architektur: $(uname -m). Unterstützt werden x86_64 und aarch64." ;;
  esac
}

check_system() {
  [[ "$(uname -s)" == "Linux" ]] || die "Dieses Script läuft nur unter Linux."
  local id="" id_like=""
  if [[ -r /etc/os-release ]]; then
    # shellcheck source=/dev/null
    id="$(. /etc/os-release && printf '%s' "${ID:-}")"
    # shellcheck source=/dev/null
    id_like="$(. /etc/os-release && printf '%s' "${ID_LIKE:-}")"
  fi
  case " ${id} ${id_like} " in
    *" debian "*|*" ubuntu "*) ;;
    *) die "Unterstützt werden Debian, Ubuntu und davon abgeleitete Systeme (gefunden: ${id:-unbekannt}). Das Script nutzt apt." ;;
  esac
  if [[ "$SYSTEMCTL" == "systemctl" ]]; then
    [[ -d /run/systemd/system ]] || die "systemd ist auf diesem System nicht aktiv."
  fi
}

# --- Argumente --------------------------------------------------------------------------------------------------
parse_args() {
  local arg value
  while [[ $# -gt 0 ]]; do
    arg="$1"
    value=""
    if [[ "$arg" == --*=* ]]; then
      value="${arg#*=}"
      arg="${arg%%=*}"
      shift
    else
      case "$arg" in
        --uninstall|--purge|--allow-downgrade|-h|--help) shift ;;
        *)
          [[ $# -ge 2 ]] || die "Die Option $arg braucht einen Wert (--help zeigt alle Optionen)."
          value="$2"
          shift 2
          ;;
      esac
    fi
    case "$arg" in
      --version) VERSION="$value" ;;
      --repo) REPO="$value"; GIVEN[repo]=1 ;;
      --user) SERVICE_USER="$value"; GIVEN[user]=1 ;;
      --install-dir) INSTALL_DIR="$value"; GIVEN[install_dir]=1 ;;
      --data-dir) DATA_DIR="$value"; GIVEN[data_dir]=1 ;;
      --config-dir) CONFIG_DIR="$value"; GIVEN[config_dir]=1 ;;
      --port) APP_OPT[port]="$value" ;;
      --bind) APP_OPT[bind]="$value" ;;
      --domain) APP_OPT[domain]="$value" ;;
      --proxy) APP_OPT[proxy]="$value" ;;
      --proxy-mode) PROXY_MODE="$value" ;;
      --require-attestation) REQUIRE_ATTESTATION="$value" ;;
      --secure-cookies) APP_OPT[secure_cookies]="$value" ;;
      --source-url) APP_OPT[source_url]="$value" ;;
      --allow-downgrade) ALLOW_DOWNGRADE=1 ;;
      --uninstall) ACTION="uninstall" ;;
      --purge) PURGE=1 ;;
      -h|--help) usage; exit 0 ;;
      *) die "Unbekannte Option: $arg (--help zeigt alle Optionen)." ;;
    esac
  done
}

# Eine bestehende Installation bestimmt die Pfade: Ein Update ohne Pfad-Optionen soll sie übernehmen, statt eine zweite,
# leere Installation unter den Standardpfaden anzulegen. Wer einen anderen Pfad ausdrücklich nennt, bekommt einen Fehler.
unit_field() { grep -m 1 "^$1=" "$UNIT_FILE" | cut -d= -f2- || true; }

adopt() {
  local key="$1" variable="$2" existing="$3"
  [[ -n "$existing" ]] || return 0
  if [[ -n "${GIVEN[$key]:-}" ]]; then
    [[ "${!variable}" == "$existing" ]] \
      || die "Es ist bereits eine Installation vorhanden (${key//_/-}: ${existing}). Um sie zu verschieben, zuerst mit --uninstall entfernen (die Daten bleiben erhalten)."
  else
    printf -v "$variable" '%s' "$existing"
  fi
}

adopt_existing_layout() {
  [[ -f "$UNIT_FILE" ]] || return 0
  local exec_start env_file
  exec_start="$(unit_field ExecStart)"
  env_file="$(unit_field EnvironmentFile)"
  adopt install_dir INSTALL_DIR "${exec_start%/Wunschkino}"
  adopt config_dir CONFIG_DIR "$(dirname "${env_file:-/}")"
  adopt data_dir DATA_DIR "$(unit_field ReadWritePaths)"
  adopt user SERVICE_USER "$(unit_field User)"
  if [[ -z "${GIVEN[repo]:-}" && -r "$CONFIG_DIR/repo" ]]; then REPO="$(cat "$CONFIG_DIR/repo")"; fi
}

validate_settings() {
  valid_repo "$REPO" || die "Ungültiges Repository '$REPO'. Erwartet wird OWNER/NAME."
  valid_user "$SERVICE_USER" || die "Ungültiger Systemnutzer '$SERVICE_USER'."
  valid_dir_under "$INSTALL_DIR" /opt /srv /usr/local \
    || die "Ungültiger Installationsordner '$INSTALL_DIR' (erlaubt: ein Ordner unter /opt, /srv oder /usr/local)."
  valid_dir_under "$DATA_DIR" /var/lib /srv /opt \
    || die "Ungültiger Datenordner '$DATA_DIR' (erlaubt: ein Ordner unter /var/lib, /srv oder /opt)."
  valid_dir_under "$CONFIG_DIR" /etc /opt /srv \
    || die "Ungültiger Konfigurationsordner '$CONFIG_DIR' (erlaubt: ein Ordner unter /etc, /opt oder /srv)."
  if [[ "$INSTALL_DIR" == *.new || "$INSTALL_DIR" == *.prev || "$DATA_DIR" == *-backups ]]; then
    die "Ungültiger Ordnername: .new, .prev und -backups sind für interne Zwecke reserviert."
  fi
  if nested "$INSTALL_DIR" "$DATA_DIR" || nested "$INSTALL_DIR" "$CONFIG_DIR" || nested "$DATA_DIR" "$CONFIG_DIR"; then
    die "Installations-, Daten- und Konfigurationsordner dürfen nicht ineinander liegen."
  fi
  BACKUP_DIR="${DATA_DIR}-backups"

  if [[ "$VERSION" != "latest" ]]; then
    valid_tag "$VERSION" || die "Ungültige Version '$VERSION'. Erwartet wird zum Beispiel v1.2.3."
  fi
  if [[ -n "$DOWNLOAD_BASE" && ! "$DOWNLOAD_BASE" =~ ^(https://|file:///)[A-Za-z0-9._/:@%+~-]+$ ]]; then
    die "Ungültiger Download-Ort."
  fi
  [[ "$HEALTH_TIMEOUT_SECONDS" =~ ^[0-9]{1,4}$ ]] || die "WK_HEALTH_TIMEOUT erwartet eine Zahl in Sekunden."

  local value name
  if [[ -v "APP_OPT[port]" ]]; then
    valid_port "${APP_OPT[port]}" || die "Ungültiger Port '${APP_OPT[port]}'. Erlaubt sind 1 bis 65535."
  fi
  if [[ -v "APP_OPT[bind]" && -n "${APP_OPT[bind]}" ]]; then
    valid_bind "${APP_OPT[bind]}" || die "Ungültige Bind-Adresse '${APP_OPT[bind]}'. Erlaubt sind eine IP-Adresse, localhost oder *."
  fi
  for name in domain proxy; do
    if [[ -v "APP_OPT[$name]" ]]; then
      # Leerzeichen als Trenner zulassen, mehrfache Kommas und Kommas am Rand entfernen: Nur Leerzeichen heißt „leer“.
      value="${APP_OPT[$name]//[[:space:]]/,}"
      value="$(printf '%s' "$value" | tr -s ',' | sed -e 's/^,//' -e 's/,$//')"
      valid_list "$value" || die "Ungültiger Wert für --$name: '${APP_OPT[$name]}'."
      APP_OPT[$name]="$value"
    fi
  done
  if [[ -v "APP_OPT[secure_cookies]" && -n "${APP_OPT[secure_cookies]}" ]]; then
    valid_bool "${APP_OPT[secure_cookies]}" || die "--secure-cookies erwartet true oder false."
  fi
  if [[ -v "APP_OPT[source_url]" ]]; then
    valid_https_url "${APP_OPT[source_url]}" || die "Ungültige Quelltext-Adresse '${APP_OPT[source_url]}'. Erwartet wird ein https://-Link."
  fi

  case "$REQUIRE_ATTESTATION" in
    auto|true|false) ;;
    *) die "--require-attestation erwartet auto, true oder false (erhalten: '$REQUIRE_ATTESTATION')." ;;
  esac
  case "$PROXY_MODE" in
    ""|local|remote) ;;
    *) die "--proxy-mode erwartet local oder remote (erhalten: '$PROXY_MODE')." ;;
  esac
  [[ "$ADMIN_COMMAND" =~ ^/(usr/local|opt|srv)/[A-Za-z0-9._/-]+$ && "$ADMIN_COMMAND" != *..* ]] \
    || die "Ungültiger Pfad für den Hilfsbefehl (erlaubt: unter /usr/local, /opt oder /srv)."
  apply_proxy_mode
}

# Bind-Adresse, die nur vom Server selbst erreichbar ist.
is_loopback_bind() { [[ "$1" == localhost || "$1" == ::1 || "$1" == 0:0:0:0:0:0:0:1 || "$1" == 127.* || "$1" == ::ffff:127.* ]]; }

# --proxy-mode legt Bind-Adresse und Proxy-Angabe fest und lehnt Widersprüche ab, bevor irgendetwas am System geändert wird.
apply_proxy_mode() {
  [[ -n "$PROXY_MODE" && "$ACTION" == "install" ]] || return 0
  ENV_FILE="$CONFIG_DIR/wunschkino.env"
  local given_bind="" given_proxy="" existing_proxy existing_bind
  if [[ -v "APP_OPT[bind]" ]]; then given_bind="${APP_OPT[bind]}"; fi
  if [[ -v "APP_OPT[proxy]" ]]; then given_proxy="${APP_OPT[proxy]}"; fi
  existing_proxy="$(env_get "Wunschkino__TrustedProxies")"
  existing_bind="$(env_get "Wunschkino__BindAddress")"

  if [[ "$PROXY_MODE" == "local" ]]; then
    [[ -z "$given_proxy" ]] || die "--proxy-mode local passt nicht zu --proxy: Bei einem Proxy auf demselben Server kommt die Anfrage von 127.0.0.1, das die App immer vertraut."
    if [[ -n "$given_bind" ]] && ! is_loopback_bind "$given_bind"; then
      die "--proxy-mode local passt nicht zu --bind '$given_bind': Die App soll nur auf 127.0.0.1 lauschen."
    fi
    APP_OPT[bind]="127.0.0.1"
    APP_OPT[proxy]=""
    return 0
  fi

  # remote: Ohne die Proxy-Adresse sähen alle Nutzer wie der Proxy aus und teilten sich Login-Limit und Sperre.
  if [[ -v "APP_OPT[proxy]" ]]; then
    [[ -n "$given_proxy" ]] || die "--proxy-mode remote braucht eine Proxy-Adresse; ein leeres --proxy entfernt sie."
    # Ein riesiges „Proxy-Netz“ (zum Beispiel 0.0.0.0/0) ließe jeden die Client-Adresse fälschen und damit Limit und Sperre umgehen.
    local entry
    for entry in ${given_proxy//,/ }; do
      if [[ "$entry" =~ ^[0-9.]+/([0-9]+)$ && "${BASH_REMATCH[1]}" -lt 8 ]] || [[ "$entry" =~ ^[0-9A-Fa-f:]+/([0-9]+)$ && "${BASH_REMATCH[1]}" -lt 16 ]]; then
        die "Das Proxy-Netz '$entry' ist viel zu groß: Jeder darin könnte die Client-Adresse fälschen. Trage die Adresse Deines Proxy-Servers ein."
      fi
    done
  else
    [[ -n "$existing_proxy" ]] || die "--proxy-mode remote braucht --proxy <IP-des-Proxy-Servers>. Läuft der Proxy auf diesem Server, nimm --proxy-mode local."
  fi
  if [[ -n "$given_bind" ]] && is_loopback_bind "$given_bind"; then
    die "--proxy-mode remote passt nicht zu --bind '$given_bind': Der Proxy-Server erreicht eine Adresse nur dieses Servers nicht."
  fi
  if [[ -z "$given_bind" ]] && is_loopback_bind "${existing_bind:-*}"; then
    APP_OPT[bind]=""
  fi
}

# --- Systemvorbereitung -----------------------------------------------------------------------------------------
# Wahr, wenn die Systembibliothek fehlt. (Ausgabe erst einlesen: grep -q schließt früh, ldconfig bekäme SIGPIPE.)
lib_missing() {
  local libraries
  libraries="$(ldconfig -p 2>/dev/null || true)"
  ! grep -q -- "$1" <<< "$libraries"
}

# Paketname einer versionierten Bibliothek (libicu72, libssl3t64, …); leer, wenn apt keinen kennt.
find_lib_package() {
  apt-cache pkgnames "$1" 2>/dev/null | grep -E "^$1[0-9]+(t64)?$" | sort -V | tail -n 1 || true
}

ensure_packages() {
  local missing=() need_icu=0 need_ssl=0 package
  command -v curl >/dev/null 2>&1 || missing+=("curl")
  command -v tar >/dev/null 2>&1 || missing+=("tar")
  [[ -e /etc/ssl/certs/ca-certificates.crt ]] || missing+=("ca-certificates")
  if lib_missing 'libz\.so'; then missing+=("zlib1g"); fi
  if lib_missing 'libstdc++\.so'; then missing+=("libstdc++6"); fi
  if lib_missing 'libicuuc\.so'; then need_icu=1; fi
  if lib_missing 'libssl\.so'; then need_ssl=1; fi

  if [[ ${#missing[@]} -eq 0 && $need_icu -eq 0 && $need_ssl -eq 0 ]]; then
    return 0
  fi

  info "Fehlende Pakete werden installiert …"
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq < /dev/null
  if [[ $need_icu -eq 1 ]]; then
    package="$(find_lib_package libicu)"
    [[ -n "$package" ]] || die "Kein libicu-Paket gefunden. Bitte libicu manuell installieren."
    missing+=("$package")
  fi
  if [[ $need_ssl -eq 1 ]]; then
    package="$(find_lib_package libssl)"
    [[ -n "$package" ]] || die "Kein libssl-Paket gefunden. Bitte libssl manuell installieren."
    missing+=("$package")
  fi
  apt-get install -y -qq --no-install-recommends "${missing[@]}" < /dev/null > /dev/null
}

# Prüft, ob ein Ordner verwendet werden darf: neu, leer oder von diesem Script angelegt. Ein bestehender Ordner mit fremdem
# Inhalt wird nie angefasst: Sonst würden Besitzer und Rechte eines fremden Verzeichnisses geändert, und --purge könnte es
# später löschen. Das passiert vor allen Änderungen am System, damit eine Ablehnung nichts hinterlässt.
check_claimable() {
  local dir="$1"
  if [[ -L "$dir" ]]; then die "$dir ist ein symbolischer Link und wird nicht verwendet."; fi
  if [[ -e "$dir" ]]; then
    [[ -d "$dir" ]] || die "$dir existiert und ist kein Ordner."
    if [[ ! -e "$dir/$MARKER" && -n "$(ls -A "$dir")" ]]; then
      die "$dir existiert bereits und enthält Dateien, die nicht von Wunschkino stammen. Bitte einen anderen Pfad wählen."
    fi
  fi
}

claim_dir() {
  local dir="$1" mode="$2" owner="$3" group="$4"
  check_claimable "$dir"
  install -d -m "$mode" -o "$owner" -g "$group" "$dir"
  if [[ ! -e "$dir/$MARKER" ]]; then install -m 0600 -o "$owner" -g "$group" /dev/null "$dir/$MARKER"; fi
}

# Ein vorhandenes Konto wird nur übernommen, wenn es von diesem Script stammt (sein Home ist der Datenordner).
precheck_user() {
  getent passwd "$SERVICE_USER" > /dev/null || return 0
  local uid home
  uid="$(id -u "$SERVICE_USER")"
  home="$(getent passwd "$SERVICE_USER" | cut -d: -f6)"
  [[ "$uid" -ne 0 ]] || die "Der Dienst darf nicht als root laufen."
  [[ "$home" == "$DATA_DIR" ]] \
    || die "Das Konto '$SERVICE_USER' existiert bereits und gehört nicht zu Wunschkino (Home: $home). Bitte mit --user einen anderen Namen wählen."
}

ensure_user() {
  precheck_user
  if getent passwd "$SERVICE_USER" > /dev/null; then return 0; fi
  info "Systemnutzer '$SERVICE_USER' wird angelegt …"
  local nologin
  nologin="$(command -v nologin || printf '/usr/sbin/nologin')"
  useradd --system --user-group --home-dir "$DATA_DIR" --no-create-home --shell "$nologin" "$SERVICE_USER"
}

ensure_dirs() {
  claim_dir "$CONFIG_DIR" 0755 root root
  claim_dir "$DATA_DIR" 0700 "$SERVICE_USER" "$SERVICE_USER"
}

# --- Konfigurationsdatei ----------------------------------------------------------------------------------------
env_get() {
  [[ -f "$ENV_FILE" ]] || return 0
  grep -m 1 "^$1=" "$ENV_FILE" | cut -d= -f2- || true
}

# Setzt einen Wert; ein leerer Wert entfernt die Zeile. Schreibt in eine Hilfsdatei und übernimmt sie mit festen Rechten.
env_set() {
  local key="$1" value="$2" tmp
  tmp="$(mktemp "$CONFIG_DIR/.env.XXXXXX")"
  if [[ -f "$ENV_FILE" ]]; then grep -v "^${key}=" "$ENV_FILE" > "$tmp" || true; fi
  if [[ -n "$value" ]]; then printf '%s=%s\n' "$key" "$value" >> "$tmp"; fi
  install -m 0640 -o root -g "$SERVICE_USER" "$tmp" "$ENV_FILE"
  rm -f "$tmp"
}

# Sichert die bisherige Konfiguration und schreibt die neue. Schlägt später etwas fehl, stellt restore_env den alten Stand wieder her.
write_config() {
  ENV_FILE="$CONFIG_DIR/wunschkino.env"
  ENV_BACKUP="$CONFIG_DIR/wunschkino.env.bak"
  if [[ -f "$ENV_FILE" ]]; then
    cp -a -- "$ENV_FILE" "$ENV_BACKUP"
    ENV_IS_NEW=0
  else
    rm -f -- "$ENV_BACKUP"
    ENV_IS_NEW=1
    info "Konfiguration wird angelegt: $ENV_FILE"
    env_set "ASPNETCORE_ENVIRONMENT" "Production"
    env_set "DOTNET_NOLOGO" "1"
    env_set "DOTNET_CLI_TELEMETRY_OPTOUT" "1"
    env_set "Wunschkino__Port" "8080"
  fi
  # Der Datenordner ist Teil der Installation, nicht der Einstellungen: Er folgt immer --data-dir.
  env_set "Wunschkino__DataDirectory" "$DATA_DIR"

  if [[ -v "APP_OPT[port]" ]]; then env_set "Wunschkino__Port" "${APP_OPT[port]}"; fi
  if [[ -v "APP_OPT[bind]" ]]; then env_set "Wunschkino__BindAddress" "${APP_OPT[bind]}"; fi
  if [[ -v "APP_OPT[domain]" ]]; then env_set "Wunschkino__Domain" "${APP_OPT[domain]}"; fi
  if [[ -v "APP_OPT[proxy]" ]]; then env_set "Wunschkino__TrustedProxies" "${APP_OPT[proxy]}"; fi
  if [[ -v "APP_OPT[secure_cookies]" ]]; then env_set "Wunschkino__SecureCookies" "${APP_OPT[secure_cookies]}"; fi
  # Die App zeigt allen Nutzern einen Link auf ihren Quelltext (AGPL-3.0, Abschnitt 13). Ohne ausdrückliche Angabe leiten wir ihn
  # aus dem Release-Repository ab: So zeigt ein Fork, der mit --repo installiert wird, automatisch auf sein eigenes Repository.
  # Ein ausdrücklich gesetzter oder von Hand eingetragener Wert wird nie überschrieben: Abgeleitet wird nur, wenn noch keiner
  # da ist, wenn er leer angegeben wurde oder wenn der bisherige Wert selbst nur aus dem bisherigen Repository abgeleitet war
  # (dann folgt er einem neuen --repo).
  local existing_source old_repo=""
  existing_source="$(env_get "Wunschkino__SourceUrl")"
  if [[ -r "$CONFIG_DIR/repo" ]]; then old_repo="$(cat "$CONFIG_DIR/repo")"; fi
  if [[ -v "APP_OPT[source_url]" && -n "${APP_OPT[source_url]}" ]]; then
    env_set "Wunschkino__SourceUrl" "${APP_OPT[source_url]}"
  elif [[ -v "APP_OPT[source_url]" || -z "$existing_source" || "$existing_source" == "https://github.com/${old_repo}" ]]; then
    env_set "Wunschkino__SourceUrl" "https://github.com/${REPO}"
  fi

  # Das Repository merkt sich die Installation, damit ein Update ohne --repo dieselbe Quelle nutzt (Forks).
  install -m 0644 -o root -g root /dev/null "$CONFIG_DIR/repo"
  printf '%s\n' "$REPO" > "$CONFIG_DIR/repo"
}

restore_env() {
  if [[ -f "$ENV_BACKUP" ]]; then
    cp -a -- "$ENV_BACKUP" "$ENV_FILE"
    rm -f -- "$ENV_BACKUP"
  elif [[ $ENV_IS_NEW -eq 1 ]]; then
    rm -f -- "$ENV_FILE"
  fi
}

# Lässt die App selbst prüfen, ob die Einstellungen gültig sind (dieselben Regeln wie beim Start), bevor ein laufender
# Dienst damit neu gestartet wird. Die Regeln stehen so nur einmal, in der App.
# Die neue Version läuft dafür als „nobody“, nicht als Dienstnutzer: Sie wird gestartet, bevor es eine Datensicherung gibt,
# und darf deshalb den Datenordner nicht beschreiben können. (Ein Fehler in einer neuen Version soll keine Daten zerstören.)
validate_app_config() {
  local binary="$1" line uid gid
  uid="$(id -u nobody 2> /dev/null || printf '65534')"
  gid="$(id -g nobody 2> /dev/null || printf '65534')"
  local -a settings=("PATH=$PATH" "HOME=/nonexistent" "DOTNET_NOLOGO=1" "DOTNET_CLI_TELEMETRY_OPTOUT=1")
  while IFS= read -r line; do
    if [[ "$line" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]]; then settings+=("$line"); fi
  done < "$ENV_FILE"
  if VALIDATION_ERROR="$(cd "$(dirname "$binary")" \
        && setpriv --reuid "$uid" --regid "$gid" --clear-groups \
             env -i "${settings[@]}" "$binary" --validate-config 2>&1 < /dev/null)"; then
    return 0
  fi
  return 1
}

fail_invalid_config() {
  restore_env
  if [[ -z "$VALIDATION_ERROR" ]]; then
    die "Die Version lässt sich nicht starten (die Prüfung der Einstellungen endete ohne Meldung). Es wurde nichts verändert."
  fi
  die "Die Einstellungen sind ungültig, es wurde nichts verändert: ${VALIDATION_ERROR}"
}

app_port() { env_get "Wunschkino__Port"; }

# --- systemd ----------------------------------------------------------------------------------------------------
write_unit() {
  local port capabilities=""
  port="$(app_port)"
  if [[ -n "$port" ]] && (( 10#$port < 1024 )); then
    capabilities="CAP_NET_BIND_SERVICE"
  fi
  info "systemd-Unit wird eingerichtet: $UNIT_FILE"
  install -d -m 0755 "$(dirname "$UNIT_FILE")"
  cat > "$UNIT_FILE" <<EOF
[Unit]
Description=Wunschkino – Wunschliste für Jellyfin
Documentation=https://github.com/${REPO}
After=network-online.target
Wants=network-online.target

[Service]
Type=exec
User=${SERVICE_USER}
Group=${SERVICE_USER}
WorkingDirectory=${INSTALL_DIR}
EnvironmentFile=${ENV_FILE}
ExecStart=${INSTALL_DIR}/Wunschkino
Restart=on-failure
RestartSec=5
TimeoutStopSec=30
SyslogIdentifier=${SERVICE_NAME}

# Härtung. MemoryDenyWriteExecute fehlt bewusst: .NET (JIT) braucht ausführbaren, schreibbaren Speicher.
# AF_NETLINK ist nötig: Die Namensauflösung (getaddrinfo) fragt darüber die Netzwerkadressen ab.
NoNewPrivileges=yes
PrivateTmp=yes
PrivateDevices=yes
ProtectSystem=strict
ProtectHome=yes
ProtectProc=invisible
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
ProtectClock=yes
ProtectHostname=yes
LockPersonality=yes
RestrictRealtime=yes
RestrictSUIDSGID=yes
RestrictNamespaces=yes
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK
RemoveIPC=yes
SystemCallArchitectures=native
SystemCallFilter=@system-service
SystemCallErrorNumber=EPERM
CapabilityBoundingSet=${capabilities}
AmbientCapabilities=${capabilities}
LimitCORE=0
UMask=0077
ReadWritePaths=${DATA_DIR}

[Install]
WantedBy=multi-user.target
EOF
  chmod 0644 "$UNIT_FILE"
  "$SYSTEMCTL" daemon-reload
}

# Hält den Dienst unbedingt an, auch im Zustand „activating (auto-restart)“ eines abstürzenden Dienstes.
stop_service() { "$SYSTEMCTL" stop "$SERVICE_NAME" 2> /dev/null || true; }

show_logs() {
  if command -v journalctl > /dev/null 2>&1; then journalctl -u "$SERVICE_NAME" -n 15 --no-pager 2> /dev/null || true; fi
}

# Adresse, unter der der Health-Check die App erreicht.
health_url() {
  local bind port host
  bind="$(env_get "Wunschkino__BindAddress")"
  port="$(app_port)"
  case "$bind" in
    ""|"*"|localhost) host="127.0.0.1" ;;
    *:*) host="[${bind}]" ;;
    *) host="$bind" ;;
  esac
  printf 'http://%s:%s/health' "$host" "$port"
}

# Wartet, bis der Dienst gesund ist UND die erwartete Version läuft (sonst könnte ein alter Prozess antworten).
# Ohne Argument gilt die gerade installierte Version; ein leeres Argument akzeptiert jede Version.
# Die Version nennt /health nur lokalen Aufrufern – dieser Check kommt vom Server selbst.
wait_healthy() {
  local url expected="${1-${TAG#v}}" body i
  url="$(health_url)"
  for (( i = 0; i < HEALTH_TIMEOUT_SECONDS; i++ )); do
    # „Host: localhost“ ist auch dann erlaubt, wenn --domain die Host-Header beschränkt; --noproxy: kein Umweg über einen Firmenproxy.
    if body="$(curl --silent --fail --noproxy '*' --max-time 3 -H 'Host: localhost' "$url" 2> /dev/null)"; then
      if [[ -z "$expected" && "$body" == *'"status":"ok"'* ]]; then return 0; fi
      if [[ -n "$expected" && "$body" == *"\"version\":\"${expected}\""* ]]; then return 0; fi
    fi
    sleep 1
  done
  return 1
}

# --- Download ---------------------------------------------------------------------------------------------------
fetch() {
  local proto="=https"
  if [[ "$DOWNLOAD_BASE" == file://* ]]; then proto="=https,file"; fi
  curl --fail --silent --show-error --location --proto "$proto" --proto-redir "$proto" --tlsv1.2 \
    --retry 3 --connect-timeout 15 --max-time 900 --output "$2" "$1"
}

resolve_tag() {
  if [[ "$VERSION" != "latest" ]]; then
    TAG="$VERSION"
    return 0
  fi
  [[ -z "$DOWNLOAD_BASE" ]] || die "Mit WK_DOWNLOAD_BASE ist --version erforderlich."
  info "Neueste Version wird ermittelt …"
  local final
  final="$(curl --silent --show-error --location --proto '=https' --proto-redir '=https' --tlsv1.2 --retry 3 --connect-timeout 15 \
    --output /dev/null --write-out '%{url_effective}' "https://github.com/${REPO}/releases/latest")" \
    || die "GitHub ist nicht erreichbar."
  TAG="${final##*/}"
  valid_tag "$TAG" || die "Im Repository ${REPO} wurde noch kein Release gefunden (erhalten: '${TAG}')."
}

download_release() {
  local asset="wunschkino-${TAG}-${RID}.tar.gz" base
  if [[ -n "$DOWNLOAD_BASE" ]]; then
    base="${DOWNLOAD_BASE}/${TAG}"
  else
    base="https://github.com/${REPO}/releases/download/${TAG}"
  fi

  TMP_DIR="$(mktemp -d)"
  info "Lade ${asset} …"
  fetch "${base}/${asset}" "$TMP_DIR/$asset" || die "Download von ${base}/${asset} fehlgeschlagen."
  fetch "${base}/SHA256SUMS" "$TMP_DIR/SHA256SUMS" || die "Die Prüfsummen (SHA256SUMS) konnten nicht geladen werden."

  # Schützt vor beschädigten oder abgebrochenen Downloads. Die Prüfsumme kommt aus derselben Quelle wie das Paket
  # und schützt daher nicht vor einem kompromittierten Release.
  awk -v f="$asset" '$2 == f { print }' "$TMP_DIR/SHA256SUMS" > "$TMP_DIR/expected"
  [[ -s "$TMP_DIR/expected" ]] || die "In SHA256SUMS steht keine Prüfsumme für ${asset}."
  (cd "$TMP_DIR" && sha256sum --check --status expected) || die "Die Prüfsumme von ${asset} stimmt nicht – Download wird verworfen."
  info "Prüfsumme stimmt."
  verify_attestation "$TMP_DIR/$asset"
  ASSET_PATH="$TMP_DIR/$asset"
}

# Die Prüfsumme kommt aus derselben Quelle wie das Paket. Die Attestierung dagegen belegt (über Sigstore und GitHub), dass das Paket
# im Release-Workflow dieses Repositories gebaut wurde. Geprüft wird mit der GitHub-CLI; ohne sie gibt es einen Hinweis.
verify_attestation() {
  local file="$1" log
  if [[ "$REQUIRE_ATTESTATION" == "false" ]]; then
    info "Die Herkunftsprüfung ist abgeschaltet (--require-attestation false)."
    return 0
  fi
  # Ohne gh oder mit einem gh, das „attestation“ nicht kennt (ältere Distributionspakete), kann die Prüfung gar nicht laufen.
  # Das ist etwas anderes als eine negative Prüfung und wird getrennt gemeldet.
  if ! command -v gh > /dev/null 2>&1 || ! gh attestation verify --help > /dev/null 2>&1; then
    [[ "$REQUIRE_ATTESTATION" != "true" ]] \
      || die "Die Herkunft des Pakets lässt sich nicht prüfen: Die GitHub-CLI (gh) fehlt oder ist zu alt (gh attestation verify wird gebraucht). Installiere eine aktuelle gh oder lass --require-attestation weg."
    warn "Die Herkunft des Pakets wurde nicht geprüft, weil die GitHub-CLI (gh) fehlt oder zu alt ist. Selbst prüfen: gh attestation verify <Paket> --repo ${REPO}. Mit --require-attestation true bricht das Script ohne Prüfung ab."
    return 0
  fi
  info "Prüfe die Herkunft des Pakets (gh attestation verify) …"
  log="$TMP_DIR/attestation.log"
  # Gebunden an den Release-Workflow dieses Repositories und an den Tag dieser Version: Eine Attestierung eines anderen Workflows
  # oder Zweigs im selben Repository genügt nicht, ebenso keine von einem selbst betriebenen Runner.
  if env GH_PROMPT_DISABLED=1 GH_NO_UPDATE_NOTIFIER=1 GH_CONFIG_DIR="$TMP_DIR/gh" \
      gh attestation verify "$file" --repo "$REPO" \
        --signer-workflow "${REPO}/.github/workflows/release.yml" \
        --source-ref "refs/tags/${TAG}" \
        --deny-self-hosted-runners > "$log" 2>&1; then
    info "Herkunft bestätigt: Das Paket wurde vom Release-Workflow von ${REPO} für ${TAG} gebaut."
    return 0
  fi
  tail -n 8 "$log" >&2 || true
  die "Die Herkunft des Pakets konnte nicht bestätigt werden. Das Paket wird verworfen. Prüfe zuerst die Ausgabe oben. Nur wenn die Prüfung bei Dir technisch nicht laufen kann (zum Beispiel ohne Netz oder Anmeldung bei GitHub), geht --require-attestation false."
}

# Das Paket wird als root entpackt: Pfade außerhalb des Zielordners, Links und Sondereinträge darin werden vorher abgelehnt.
# Erlaubt sind nur normale Dateien („-“) und Ordner („d“); Geräte, Pipes und Sockets haben in einem Programmpaket nichts zu suchen.
validate_archive() {
  local listing names
  listing="$(tar -tzvf "$ASSET_PATH")" || die "Das Paket lässt sich nicht lesen."
  names="$(tar -tzf "$ASSET_PATH")"
  if grep -qvE '^[-d]' <<< "$listing"; then die "Das Paket enthält Links oder Sondereinträge und wird abgelehnt."; fi
  if grep -qE '(^/|(^|/)\.\.(/|$))' <<< "$names"; then die "Das Paket enthält unzulässige Pfade und wird abgelehnt."; fi
}

# --- Installation und Update ------------------------------------------------------------------------------------
installed_version() {
  if [[ -f "$INSTALL_DIR/VERSION" ]]; then cat "$INSTALL_DIR/VERSION"; fi
}

# Entpackt das Paket in <Installationsordner>.new, ohne die laufende Installation oder die Konfiguration anzufassen.
prepare_release() {
  download_release
  validate_archive
  NEW_DIR="${INSTALL_DIR}.new"
  rm -rf -- "$NEW_DIR"
  install -d -m 0755 -o root -g root "$NEW_DIR"
  (umask 022 && tar -xzf "$ASSET_PATH" -C "$NEW_DIR" --no-same-owner --no-same-permissions)
  # Setuid- und Setgid-Bits haben in dem Paket nichts zu suchen; als root entpackt blieben sie sonst erhalten.
  find "$NEW_DIR" -perm /6000 -exec chmod ug-s {} +
  [[ -x "$NEW_DIR/Wunschkino" ]] || die "Das Paket enthält keine ausführbare Datei 'Wunschkino'."
  chown -R root:root "$NEW_DIR"
  printf '%s\n' "$TAG" > "$NEW_DIR/VERSION"
  install -m 0644 -o root -g root /dev/null "$NEW_DIR/$MARKER"
  chmod 0644 "$NEW_DIR/VERSION"
}

backup_data() {
  local stamp backup f old
  stamp="$(date +%Y%m%d-%H%M%S)"
  backup="$BACKUP_DIR/${stamp}-${1:-unbekannt}"
  # Die Sicherungen liegen bewusst außerhalb des Datenordners: Der gehört dem Dienstnutzer, und als root dort zu arbeiten,
  # hieße, einem kompromittierten Dienst Links unterschieben zu lassen.
  claim_dir "$BACKUP_DIR" 0700 root root || return 1
  install -d -m 0700 -o root -g root "$backup" || return 1
  # Datenbank samt WAL und der Schlüsselring: Ohne die Schlüssel sind die gespeicherten API-Keys nicht mehr lesbar.
  for f in "$DATA_DIR"/wunschkino.db "$DATA_DIR"/wunschkino.db-wal "$DATA_DIR"/wunschkino.db-shm; do
    if [[ -L "$f" ]]; then warn "$f ist ein Link und wird nicht gesichert."; return 1; fi
    if [[ -f "$f" ]]; then cp -a -- "$f" "$backup/" || return 1; fi
  done
  if [[ -L "$DATA_DIR/keys" ]]; then warn "$DATA_DIR/keys ist ein Link und wird nicht gesichert."; return 1; fi
  if [[ -d "$DATA_DIR/keys" ]]; then cp -a -- "$DATA_DIR/keys" "$backup/" || return 1; fi
  BACKUP_PATH="$backup"
  info "Datensicherung: $backup"

  # Nur die letzten Sicherungen behalten.
  while IFS= read -r old; do
    if [[ "$old" == "$BACKUP_DIR/"* && -d "$old" ]]; then rm -rf -- "$old"; fi
  done < <(find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d | sort | head -n "-${KEEP_BACKUPS}")
}

restore_backup() {
  [[ -n "$BACKUP_PATH" && -d "$BACKUP_PATH" ]] || return 0
  warn "Datenbank und Schlüssel werden aus der Sicherung wiederhergestellt …"
  local f
  rm -f -- "$DATA_DIR"/wunschkino.db "$DATA_DIR"/wunschkino.db-wal "$DATA_DIR"/wunschkino.db-shm
  for f in "$BACKUP_PATH"/wunschkino.db*; do
    if [[ -f "$f" ]]; then cp -a -- "$f" "$DATA_DIR/"; fi
  done
  if [[ -d "$BACKUP_PATH/keys" ]]; then
    rm -rf -- "$DATA_DIR/keys"
    cp -a -- "$BACKUP_PATH/keys" "$DATA_DIR/keys"
  fi
}

# Startet den alten Dienst wieder und bricht ab (für Fehler nach dem Anhalten, aber vor dem Tausch).
resume_old_version() {
  warn "$1"
  "$SYSTEMCTL" start "$SERVICE_NAME" || true
  die "Das Update wurde abgebrochen, die bisherige Version läuft weiter."
}

apply_release() {
  local prev="${INSTALL_DIR}.prev" old_version had_install=0
  old_version="$(installed_version)"

  write_config
  validate_app_config "$NEW_DIR/Wunschkino" || fail_invalid_config

  if [[ -d "$INSTALL_DIR" ]]; then
    had_install=1
    [[ -e "$INSTALL_DIR/$MARKER" ]] \
      || { restore_env; die "$INSTALL_DIR existiert bereits und stammt nicht von diesem Script. Bitte einen anderen Pfad wählen (--install-dir)."; }
    info "Dienst wird für das Update angehalten …"
    stop_service
    backup_data "${old_version:-unbekannt}" || { restore_env; resume_old_version "Die Datensicherung ist fehlgeschlagen (Speicherplatz?)."; }
    rm -rf -- "$prev"
    mv -- "$INSTALL_DIR" "$prev" || { restore_env; resume_old_version "Die bisherige Version ließ sich nicht beiseite schieben."; }
  fi
  if ! mv -- "$NEW_DIR" "$INSTALL_DIR"; then
    if [[ $had_install -eq 1 ]]; then mv -- "$prev" "$INSTALL_DIR" || true; restore_env; resume_old_version "Die neue Version ließ sich nicht an Ort und Stelle bringen."; fi
    die "Die neue Version ließ sich nicht installieren."
  fi

  write_unit
  "$SYSTEMCTL" enable "$SERVICE_NAME" > /dev/null 2>&1 || true
  info "Dienst wird gestartet …"
  if "$SYSTEMCTL" restart "$SERVICE_NAME" && wait_healthy; then
    rm -rf -- "$prev" "$ENV_BACKUP"
    return 0
  fi

  warn "Der Dienst wurde nicht gesund (Wartezeit ${HEALTH_TIMEOUT_SECONDS} Sekunden)."
  show_logs
  if [[ $had_install -eq 1 ]]; then
    warn "Die vorherige Version ${old_version:-?} wird wiederhergestellt …"
    stop_service
    rm -rf -- "$INSTALL_DIR"
    mv -- "$prev" "$INSTALL_DIR"
    restore_backup
    restore_env
    "$SYSTEMCTL" restart "$SERVICE_NAME" || true
    if wait_healthy "${old_version#v}"; then
      die "Update auf ${TAG} fehlgeschlagen, ${old_version:-die vorherige Version} läuft wieder (mit den bisherigen Einstellungen). Logs: journalctl -u ${SERVICE_NAME} -n 100"
    fi
    die "Update auf ${TAG} fehlgeschlagen und auch ${old_version:-die vorherige Version} startet nicht. Sicherung: ${BACKUP_PATH}. Logs: journalctl -u ${SERVICE_NAME} -n 100"
  fi
  restore_env
  die "Wunschkino startet nicht. Logs: journalctl -u ${SERVICE_NAME} -n 100"
}

# Gleiche Version, neue Einstellungen: prüfen, übernehmen, neu starten – und bei einem Fehler den alten Stand zurückholen.
reconfigure_installed() {
  write_config
  validate_app_config "$INSTALL_DIR/Wunschkino" || fail_invalid_config
  write_unit
  "$SYSTEMCTL" enable "$SERVICE_NAME" > /dev/null 2>&1 || true
  if "$SYSTEMCTL" restart "$SERVICE_NAME" && wait_healthy; then
    rm -f -- "$ENV_BACKUP"
    return 0
  fi
  warn "Der Dienst wurde mit den neuen Einstellungen nicht gesund."
  show_logs
  restore_env
  write_unit
  "$SYSTEMCTL" restart "$SERVICE_NAME" || true
  if wait_healthy; then
    die "Die neuen Einstellungen funktionieren nicht (zum Beispiel ein belegter Port oder eine fremde Bind-Adresse). Die bisherigen sind wieder aktiv."
  fi
  die "Wunschkino startet nicht mehr. Logs: journalctl -u ${SERVICE_NAME} -n 100"
}

# Hilfsbefehl für den Betreiber: führt die Verwaltungsbefehle der App (zum Beispiel gesperrte Adressen entsperren) als
# Dienstnutzer und mit der Konfiguration des Dienstes aus. Ein fremder Befehl gleichen Namens wird nie überschrieben.
install_admin_command() {
  local directory tmp
  directory="$(dirname "$ADMIN_COMMAND")"
  if [[ -e "$ADMIN_COMMAND" ]] && ! grep -qxF "$ADMIN_COMMAND_MARKER" "$ADMIN_COMMAND" 2> /dev/null; then
    warn "$ADMIN_COMMAND existiert und stammt nicht von diesem Script. Der Hilfsbefehl wird nicht angelegt."
    return 0
  fi
  if [[ ! -d "$directory" ]]; then
    warn "Der Ordner $directory fehlt. Der Hilfsbefehl wird nicht angelegt."
    return 0
  fi
  tmp="$(mktemp "$directory/.wunschkino-admin.XXXXXX")"
  # Bricht eines der folgenden Kommandos ab, soll die Hilfsdatei nicht im Zielordner liegen bleiben.
  cat > "$tmp" <<EOF || { rm -f -- "$tmp"; die "Der Hilfsbefehl konnte nicht angelegt werden."; }
#!/usr/bin/env bash
${ADMIN_COMMAND_MARKER}
# Verwaltungsbefehle von Wunschkino, zum Beispiel: sudo wunschkino-admin --list-blocks
set -Eeuo pipefail
if [[ \$EUID -ne 0 ]]; then
  echo "Bitte als root ausführen: sudo wunschkino-admin \$*" >&2
  exit 1
fi
settings=()
while IFS= read -r line; do
  if [[ "\$line" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]]; then settings+=("\$line"); fi
done < '${ENV_FILE}'
# Ins Programmverzeichnis wechseln: Die App liest Dateien aus dem Arbeitsverzeichnis, und das darf kein vom Dienstnutzer beschreibbarer Ordner sein.
cd '${INSTALL_DIR}'
exec runuser -u '${SERVICE_USER}' -- env "\${settings[@]}" '${INSTALL_DIR}/Wunschkino' "\$@"
EOF
  if ! install -m 0755 -o root -g root "$tmp" "$ADMIN_COMMAND"; then
    rm -f -- "$tmp"
    die "Der Hilfsbefehl konnte nicht angelegt werden."
  fi
  rm -f -- "$tmp"
}

# Ermittelt, ob eine Firewall den App-Port abschirmt. Das Script ändert die Firewall nie selbst, es weist nur darauf hin.
firewall_state() {
  local port="$1" status
  if command -v ufw > /dev/null 2>&1; then
    status="$(ufw status 2> /dev/null || true)"
    if [[ "$status" == *"Status: active"* ]]; then
      if grep -qE "^${port}(/tcp)?[[:space:]]+ALLOW([[:space:]]+IN)?[[:space:]]+Anywhere" <<< "$status"; then echo "ufw-open"; else echo "ufw"; fi
      return
    fi
  fi
  if command -v firewall-cmd > /dev/null 2>&1 && firewall-cmd --state > /dev/null 2>&1; then echo "firewalld"; return; fi
  if command -v nft > /dev/null 2>&1 && nft list ruleset 2> /dev/null | grep -qE "hook input .*policy drop"; then echo "nftables"; return; fi
  echo "none"
}

# Öffentliche (nicht private) Adressen dieses Servers; über sie wäre ein offener Port direkt aus dem Internet erreichbar.
public_addresses() {
  command -v ip > /dev/null 2>&1 || return 0
  ip -o addr show scope global 2> /dev/null | awk '{print $4}' | cut -d/ -f1 | grep -vE \
    '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.|100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.|f[cd][0-9a-f]{2}:)' || true
}

print_firewall_notice() {
  local port="$1" proxy="$2" state public example_ip
  state="$(firewall_state "$port")"
  public="$(public_addresses | paste -sd ' ' -)"
  example_ip="${proxy%%,*}"
  if [[ -z "$example_ip" ]]; then example_ip="<IP-des-Proxy-Servers>"; fi

  cat <<EOF

${C_BOLD}Firewall:${C_OFF} Port ${port} sollte nur vom Proxy-Server aus erreichbar sein. Das Script ändert die Firewall nicht.
Mit ufw zum Beispiel:
  sudo ufw allow from ${example_ip} to any port ${port} proto tcp
EOF
  case "$state" in
    none) warn "Keine aktive Firewall gefunden (ufw, firewalld, nftables): Port ${port} ist von jedem Rechner aus erreichbar, der diesen Server erreicht." ;;
    ufw-open) warn "ufw gibt Port ${port} für alle frei. Beschränke die Regel auf die Adresse des Proxy-Servers." ;;
    *) info "Eine Firewall (${state}) ist aktiv. Prüfe, dass Port ${port} nur für den Proxy-Server offen ist." ;;
  esac
  if [[ -n "$public" && ( "$state" == none || "$state" == ufw-open ) ]]; then
    warn "Dieser Server hat eine öffentliche Adresse (${public}). Port ${port} ist damit direkt aus dem Internet erreichbar."
  fi
}

print_summary() {
  local first_install="$1" port ip domain proxy bind local_only=0 token=""
  port="$(app_port)"
  domain="$(env_get "Wunschkino__Domain")"
  proxy="$(env_get "Wunschkino__TrustedProxies")"
  bind="$(env_get "Wunschkino__BindAddress")"
  ip="$(hostname -I 2> /dev/null | awk '{print $1}' || true)"
  if [[ -z "$ip" ]]; then ip="<Server-IP>"; fi
  if is_loopback_bind "${bind:-*}"; then local_only=1; ip="127.0.0.1"; fi
  if [[ -f "$DATA_DIR/setup-token" && ! -L "$DATA_DIR/setup-token" ]]; then token="$(cat "$DATA_DIR/setup-token")"; fi

  printf '\n%s\n' "${C_BOLD}Wunschkino ${TAG} läuft.${C_OFF}"
  if [[ $local_only -eq 1 ]]; then
    printf '  Adresse:          http://%s:%s (nur von diesem Server aus erreichbar)\n' "$ip" "$port"
  else
    printf '  Adresse im Netz:  http://%s:%s\n' "$ip" "$port"
  fi
  if [[ -n "$domain" ]]; then printf '  Domain:           %s\n' "$domain"; fi
  printf '  Konfiguration:    %s\n' "$ENV_FILE"
  printf '  Daten:            %s\n' "$DATA_DIR"
  printf '  Sicherungen:      %s\n' "$BACKUP_DIR"
  printf '  Logs:             journalctl -u %s -f\n' "$SERVICE_NAME"
  if [[ -x "$ADMIN_COMMAND" ]]; then
    printf '  Hilfsbefehl:      sudo wunschkino-admin --list-blocks | --unblock <Adresse> | --unblock-all | --list-users | --reset-password <Benutzer> | --reset-2fa <Benutzer>\n'
  fi

  if [[ -n "$token" ]]; then
    printf '\n%s\n' "${C_BOLD}Einrichtung:${C_OFF} Öffne die Seite im Browser und gib dieses Setup-Token ein:"
    printf '  %s\n' "$token"
    printf '  (Das Token wird nach der Einrichtung gelöscht. Es liegt auch in %s/setup-token.)\n' "$DATA_DIR"
    if [[ $local_only -eq 0 && -z "$proxy" ]]; then
      cat <<EOF

${C_YELLOW}Achtung, die Einrichtung ist offen:${C_OFF} Bis Du sie abgeschlossen hast, kann jeder, der Port ${port} dieses Servers erreicht,
die Einrichtungsseite aufrufen. Schließe sie jetzt ab. Läuft Dein Reverse-Proxy auf diesem Server, installiere mit
--proxy-mode local: Dann lauscht die App nur auf 127.0.0.1 und der Port ist von außen nicht offen.
EOF
    fi
  fi

  if [[ "$(env_get "Wunschkino__SecureCookies")" != "false" ]]; then
    cat <<EOF

${C_YELLOW}Hinweis zur Anmeldung:${C_OFF} Das Anmelde-Cookie wird nur über HTTPS gespeichert. Öffne Wunschkino deshalb über Deinen
Reverse-Proxy mit HTTPS. Über http://${ip}:${port} (ohne HTTPS) gelingt die Anmeldung nicht. Läuft die App bewusst ohne HTTPS
im eigenen Netz, installiere mit --secure-cookies false.
EOF
  fi

  if [[ "$first_install" == "1" || -z "$proxy" ]]; then
    cat <<EOF

${C_BOLD}Nginx Proxy Manager einrichten${C_OFF} (oder einen anderen Reverse-Proxy mit HTTPS):
  1. Hosts → Proxy Hosts → Add Proxy Host
  2. Domain Names: Deine Domain, zum Beispiel wunsch.example.org
  3. Scheme: http   Forward Hostname/IP: ${ip}   Forward Port: ${port}
  4. Websockets Support: EIN (zwingend, sonst funktioniert die Oberfläche nicht)
  5. SSL: neues Zertifikat (Let's Encrypt) anfordern, Force SSL: EIN
EOF
    if [[ $local_only -eq 0 ]]; then
      printf '  6. Der Port %s muss vom Proxy-Server aus erreichbar sein (siehe Firewall unten).\n' "$port"
    fi
  fi

  if [[ $local_only -eq 0 ]]; then
    print_firewall_notice "$port" "$proxy"
  fi

  if [[ -z "$proxy" && $local_only -eq 0 ]]; then
    cat <<EOF

${C_YELLOW}Wichtig – Proxy vertrauen:${C_OFF} Die App kennt Deinen Reverse-Proxy noch nicht. Ohne diese Angabe sehen alle
Nutzer wie die Adresse des Proxys aus und teilen sich das Login-Limit (10 Versuche pro Minute). Trage die IP-Adresse
des Proxy-Servers ein (bei Docker die des Servers, auf dem der Container läuft):

  curl -fsSL https://github.com/${REPO}/releases/latest/download/install.sh | sudo bash -s -- --proxy <IP-des-Proxy-Servers>

Optional beschränkt --domain wunsch.example.org die erlaubten Host-Header.
EOF
  elif [[ -z "$domain" ]]; then
    cat <<EOF

${C_YELLOW}Hinweis zur Domain:${C_OFF} Ohne --domain akzeptiert die App jeden Host-Header. Trage Deine öffentliche Domain ein,
zum Beispiel mit --domain wunsch.example.org.
EOF
  fi
}

do_install() {
  require_root
  acquire_lock
  adopt_existing_layout
  validate_settings
  check_system
  detect_arch
  # Ablehnungen kommen vor jeder Änderung am System: Danach soll nichts (kein Konto, kein Ordner) zurückbleiben.
  check_claimable "$CONFIG_DIR"
  check_claimable "$DATA_DIR"
  if [[ -d "$INSTALL_DIR" && ! -e "$INSTALL_DIR/$MARKER" && -n "$(ls -A "$INSTALL_DIR")" ]]; then
    die "$INSTALL_DIR existiert bereits und stammt nicht von diesem Script. Bitte einen anderen Pfad wählen (--install-dir)."
  fi
  precheck_user
  ensure_packages
  ensure_user
  ensure_dirs
  resolve_tag

  local current first_install=1
  current="$(installed_version)"
  if [[ -n "$current" ]]; then first_install=0; fi

  if [[ "$current" == "$TAG" ]]; then
    info "Version ${TAG} ist bereits installiert – Einstellungen werden übernommen, der Dienst neu gestartet."
    reconfigure_installed
  else
    if [[ -n "$current" ]] && valid_tag "$current" && version_older "$TAG" "$current" && [[ "$ALLOW_DOWNGRADE" != "1" ]]; then
      die "Installiert ist ${current}; ${TAG} wäre älter. Ein Downgrade lehnt das Script ab (alte Versionen können bekannte Lücken haben). Mit --allow-downgrade geht es bewusst."
    fi
    if [[ -n "$current" ]]; then info "Update von ${current} auf ${TAG}."; fi
    prepare_release
    apply_release
  fi
  install_admin_command
  print_summary "$first_install"
}

# --- Deinstallation ---------------------------------------------------------------------------------------------
remove_managed_dir() {
  local dir="$1"
  if [[ -d "$dir" ]]; then
    if [[ -e "$dir/$MARKER" ]]; then
      rm -rf -- "$dir"
    else
      warn "$dir wird nicht gelöscht: Es fehlt die Markierung $MARKER (der Ordner stammt nicht von diesem Script)."
    fi
  fi
}

do_uninstall() {
  require_root
  acquire_lock
  adopt_existing_layout
  validate_settings
  ENV_FILE="$CONFIG_DIR/wunschkino.env"
  info "Dienst wird entfernt …"
  stop_service
  "$SYSTEMCTL" disable "$SERVICE_NAME" > /dev/null 2>&1 || true
  rm -f -- "$UNIT_FILE"
  "$SYSTEMCTL" daemon-reload || true
  # Nur den eigenen Hilfsbefehl entfernen, nie einen fremden gleichen Namens.
  if [[ -f "$ADMIN_COMMAND" ]] && grep -qxF "$ADMIN_COMMAND_MARKER" "$ADMIN_COMMAND" 2> /dev/null; then rm -f -- "$ADMIN_COMMAND"; fi

  remove_managed_dir "$INSTALL_DIR"
  rm -rf -- "${INSTALL_DIR}.new" "${INSTALL_DIR}.prev"

  if [[ $PURGE -eq 1 ]]; then
    info "Daten, Sicherungen, Konfiguration und Systemnutzer werden gelöscht (--purge) …"
    remove_managed_dir "$DATA_DIR"
    remove_managed_dir "$BACKUP_DIR"
    remove_managed_dir "$CONFIG_DIR"
    # Nur ein Konto löschen, das dieses Script angelegt hat (sein Home ist der Datenordner).
    if getent passwd "$SERVICE_USER" > /dev/null \
        && [[ "$(id -u "$SERVICE_USER")" -ne 0 ]] \
        && [[ "$(getent passwd "$SERVICE_USER" | cut -d: -f6)" == "$DATA_DIR" ]]; then
      userdel "$SERVICE_USER" > /dev/null 2>&1 || true
    fi
    info "Wunschkino wurde vollständig entfernt."
  else
    info "Wunschkino wurde entfernt. Daten (${DATA_DIR}) und Konfiguration (${CONFIG_DIR}) bleiben erhalten;"
    info "mit --uninstall --purge werden auch sie gelöscht."
  fi
}

main() {
  parse_args "$@"
  case "$ACTION" in
    install) do_install ;;
    uninstall) do_uninstall ;;
  esac
}

# Alles steht in Funktionen und wird erst hier aufgerufen: Bricht ein Download des Scripts ab, läuft nichts halb.
main "$@"
