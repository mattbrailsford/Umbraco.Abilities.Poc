#!/usr/bin/env bash
# Boots the demo site, then opens Claude Code in a new terminal window with the abilities MCP server connected.
# The site runs in this terminal until you press Ctrl+C. On macOS, Claude opens in Terminal or iTerm (whichever this
# runs in); elsewhere it runs in this terminal, and the site stops when Claude exits.
#
#   demo/start-demo.sh             start the site and Claude
#   demo/start-demo.sh --reset     delete the demo database first, so the site installs itself again
#   demo/start-demo.sh --site-only start the site and wait (Ctrl+C stops it), for another MCP client
#
# Any other arguments are passed to claude.
#
# Sign in to the backoffice at https://localhost:44399/umbraco as admin@example.com / password1234.
# The MCP API user's secret is made on the first run and kept next to the database, which git ignores.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SITE="$ROOT/demo/Umbraco.Abilities.Demo"
PROXY="$ROOT/tools/Umbraco.Abilities.McpProxy"
DATA="$SITE/umbraco/Data"
DATABASE="$DATA/Umbraco.Clean.sqlite.db"
SECRET_FILE="$DATA/demo-mcp-client-secret"
MCP_CONFIG="$DATA/demo-mcp.json"
LOG="$SITE/umbraco/Logs/demo-site.log"
URL="https://localhost:44399"

RESET=false
SITE_ONLY=false
CLAUDE_ARGS=()
for arg in "$@"; do
  case "$arg" in
    --reset) RESET=true ;;
    --site-only) SITE_ONLY=true ;;
    *) CLAUDE_ARGS+=("$arg") ;;
  esac
done

say() { printf '\033[1m%s\033[0m\n' "$*"; }

if lsof -nP -iTCP:44399 -sTCP:LISTEN >/dev/null 2>&1; then
  say "Something is already running on port 44399. Stop it first (it may be an earlier demo site)."
  exit 1
fi

if ! $SITE_ONLY && ! command -v claude >/dev/null 2>&1; then
  say "Claude Code isn't installed (no 'claude' on the PATH). Use --site-only to start just the site."
  exit 1
fi

if ! dotnet dev-certs https --check --trust >/dev/null 2>&1; then
  say "The HTTPS development certificate isn't trusted, so the MCP server can't call the site."
  say "Run: dotnet dev-certs https --trust"
  exit 1
fi

mkdir -p "$DATA" "$(dirname "$LOG")"

if $RESET; then
  say "Deleting the demo database, so the site installs itself again."
  rm -f "$DATABASE" "$DATABASE-shm" "$DATABASE-wal" "$SECRET_FILE" "$MCP_CONFIG"
fi

# The API user is made once, with the secret of the first run. A database made before this script has its own.
if [[ -f "$DATABASE" && ! -f "$SECRET_FILE" ]]; then
  say "The demo database was made without this script, so its admin password and MCP secret aren't known here."
  say "Run again with --reset to start from a fresh database."
  exit 1
fi

if [[ ! -f "$SECRET_FILE" ]]; then
  (umask 077 && openssl rand -hex 32 > "$SECRET_FILE")
fi
SECRET="$(cat "$SECRET_FILE")"

say "Building the site and the MCP server."
dotnet build "$SITE" -v quiet -nologo -clp:ErrorsOnly
dotnet build "$PROXY" -v quiet -nologo -clp:ErrorsOnly

say "Starting the site. Its log: $LOG"
Demo__Mcp__ClientSecret="$SECRET" \
  dotnet run --no-build --project "$SITE" --launch-profile Kestrel >"$LOG" 2>&1 &
SITE_PID=$!
stop_site() { kill "$SITE_PID" 2>/dev/null || true; wait "$SITE_PID" 2>/dev/null || true; }
trap stop_site EXIT

# The first run installs Umbraco, Commerce and the starter kit, which takes a minute or two.
for _ in $(seq 1 300); do
  if ! kill -0 "$SITE_PID" 2>/dev/null; then
    say "The site stopped while starting. The end of its log:"
    tail -n 30 "$LOG"
    exit 1
  fi
  if curl -sk -o /dev/null --max-time 2 "$URL/umbraco"; then
    break
  fi
  sleep 1
done
if ! curl -sk -o /dev/null --max-time 2 "$URL/umbraco"; then
  say "The site didn't answer within 5 minutes. See $LOG"
  exit 1
fi

# The site seeds itself in the background once Umbraco is up: the MCP API user, then the shop. Wait until the MCP
# server can sign in, so Claude never starts before its tools work.
say "Waiting for the demo to seed itself."
mcp_can_sign_in() {
  printf 'grant_type=client_credentials&client_id=umbraco-back-office-mcp&client_secret=%s' "$SECRET" |
    curl -sk -o /dev/null -w '%{http_code}' --max-time 5 --data @- \
      "$URL/umbraco/management/api/v1/security/back-office/token" | grep -q '^200$'
}
for _ in $(seq 1 120); do
  mcp_can_sign_in && break
  if ! kill -0 "$SITE_PID" 2>/dev/null; then
    say "The site stopped while seeding. See $LOG"
    exit 1
  fi
  sleep 1
done
if ! mcp_can_sign_in; then
  say "The MCP API user isn't there after 2 minutes. Look for 'Demo:' in the site's log in umbraco/Logs."
  exit 1
fi

say "The site is up: $URL/umbraco (admin@example.com / password1234)"

if $SITE_ONLY; then
  say "Press Ctrl+C to stop it."
  wait "$SITE_PID"
  exit 0
fi

# The MCP server for this Claude session only, so it doesn't change the user's own Claude settings.
(umask 077 && cat > "$MCP_CONFIG" <<JSON
{
  "mcpServers": {
    "umbraco-abilities": {
      "command": "dotnet",
      "args": ["$PROXY/bin/Debug/net10.0/Umbraco.Abilities.McpProxy.dll"],
      "env": {
        "UMBRACO_BASE_URL": "$URL",
        "UMBRACO_CLIENT_ID": "umbraco-back-office-mcp",
        "UMBRACO_CLIENT_SECRET": "$SECRET"
      }
    }
  }
}
JSON
)

# Claude runs in an empty folder outside the repo, so it works through the MCP server rather than reading this
# repo's code and instructions. Only this MCP server is loaded, not the user's others.
WORKDIR="${TMPDIR:-/tmp}/umbraco-abilities-demo"
mkdir -p "$WORKDIR"
CLAUDE_CMD=(claude --strict-mcp-config --mcp-config "$MCP_CONFIG" "${CLAUDE_ARGS[@]+"${CLAUDE_ARGS[@]}"}")

if [[ "$(uname)" != "Darwin" ]]; then
  say "Starting Claude with the umbraco-abilities MCP server. The site stops when Claude exits."
  cd "$WORKDIR"
  "${CLAUDE_CMD[@]}"
  exit 0
fi

# On macOS, Claude gets its own window, so this terminal keeps the site. A .command file opens in a new terminal
# window and runs there.
LAUNCHER="$WORKDIR/start-claude.command"
(umask 077 && {
  echo '#!/usr/bin/env bash'
  printf 'cd %q\n' "$WORKDIR"
  printf 'exec'; printf ' %q' "${CLAUDE_CMD[@]}"; echo
} > "$LAUNCHER")
chmod +x "$LAUNCHER"

case "${TERM_PROGRAM:-}" in
  iTerm.app) open -a iTerm "$LAUNCHER" ;;
  *) open -a Terminal "$LAUNCHER" ;;
esac

say "Claude is open in a new window, with the umbraco-abilities MCP server."
say "The site keeps running here. Press Ctrl+C to stop it."
wait "$SITE_PID"
