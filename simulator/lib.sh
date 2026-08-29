#!/usr/bin/env bash
# Shared helpers for the CKA Lab command simulator.
# Sourced by the shims in this directory — not meant to be run directly.
#
# State lives in $LAB_STATE_DIR as plain files so each shim can stay a
# small, readable script instead of sharing a database or daemon.

LAB_STATE_DIR="${LAB_STATE_DIR:-$HOME/.lab-state}"
mkdir -p "$LAB_STATE_DIR/services"

# --- package install state (fed by the apt-get/apt-mark shims) -----------

lab::pkg_mark() {         # lab::pkg_mark <name>
  touch "$LAB_STATE_DIR/pkg-$1"
}
lab::pkg_installed() {    # lab::pkg_installed <name> -> exit 0/1
  [ -f "$LAB_STATE_DIR/pkg-$1" ]
}
lab::hold_mark() {
  touch "$LAB_STATE_DIR/hold-$1"
}

# --- cluster state (fed by the kubeadm shim) ------------------------------

lab::cluster_state() {    # prints: none | initialized | upgraded
  if [ -f "$LAB_STATE_DIR/cluster-upgraded" ]; then
    echo upgraded
  elif [ -f "$LAB_STATE_DIR/cluster-initialized" ]; then
    echo initialized
  else
    echo none
  fi
}

# --- generic service state (fed by the systemctl shim) --------------------

lab::svc_set() {          # lab::svc_set <name> <active|inactive> <enabled|disabled>
  echo "$2 $3" > "$LAB_STATE_DIR/services/$1"
}
lab::svc_get() {          # prints "<active-state> <enabled-state>"
  cat "$LAB_STATE_DIR/services/$1" 2>/dev/null || echo "inactive disabled"
}

# --- progress reporting to the API (drives the frontend's live checklist) -

lab::progress() {         # lab::progress <step> <ok|error> <message>
  local step="$1" status="$2" message="$3"
  [ -n "${LAB_API_URL:-}" ] || return 0
  curl -fsS -m 2 -X POST "$LAB_API_URL/internal/progress" \
    -H "Content-Type: application/json" \
    -H "X-Lab-Session: ${LAB_SESSION_ID:-unknown}" \
    -d "{\"step\":\"$step\",\"status\":\"$status\",\"message\":$(printf '%s' "$message" | jq -Rs .)}" \
    >/dev/null 2>&1 || true   # never let a reporting failure break the shell
}
