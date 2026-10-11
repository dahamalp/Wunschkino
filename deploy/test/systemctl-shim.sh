#!/usr/bin/env bash
# Mini-Ersatz für systemctl in Containern ohne systemd – NUR für die Tests des Install-Scripts.
# Liest die von install.sh erzeugte Unit-Datei und startet/stoppt den Prozess selbst.
set -euo pipefail

readonly UNIT="/etc/systemd/system/wunschkino.service"
readonly PID_FILE="/run/wunschkino-shim.pid"
readonly LOG_FILE="/tmp/wunschkino-shim.log"

unit_value() { grep -m 1 "^$1=" "$UNIT" | cut -d= -f2-; }
is_running() { [[ -f "$PID_FILE" ]] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; }

stop() {
  if is_running; then
    local pid; pid="$(cat "$PID_FILE")"
    kill "$pid" 2>/dev/null || true
    for _ in $(seq 1 100); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.1
    done
    kill -9 "$pid" 2>/dev/null || true
  fi
  rm -f "$PID_FILE"
}

start() {
  local user dir envfile exec_start
  user="$(unit_value User)"; dir="$(unit_value WorkingDirectory)"
  envfile="$(unit_value EnvironmentFile)"; exec_start="$(unit_value ExecStart)"
  : > "$LOG_FILE"
  # setpriv ersetzt sich selbst durch den Prozess: Die PID gehört direkt der App.
  # Die einfachen Anführungszeichen sind Absicht: $0, $1 und $2 sollen erst in der inneren Shell expandieren.
  # „9>&-“ schließt die Sperrdatei von install.sh: Mit echtem systemd erbt ein Dienst sie nicht, dieser Ersatz würde sie sonst festhalten.
  # shellcheck disable=SC2016
  setsid setpriv --reuid "$user" --regid "$user" --init-groups \
    bash -c 'set -a; . "$0"; set +a; cd "$1"; exec "$2"' "$envfile" "$dir" "$exec_start" 9>&- >> "$LOG_FILE" 2>&1 < /dev/null &
  echo $! > "$PID_FILE"
}

command="${1:-}"
case "$command" in
  daemon-reload|enable|disable) exit 0 ;;
  is-active) if is_running; then exit 0; else exit 3; fi ;;
  stop) stop ;;
  start|restart)
    stop
    start
    # Wie bei Type=exec: Startet der Prozess nicht (kaputte Datei, belegte Adresse), meldet der Befehl einen Fehler.
    sleep 1
    if ! is_running; then exit 1; fi
    ;;
  *) echo "systemctl-shim: Befehl '$command' wird nicht unterstützt" >&2; exit 1 ;;
esac
