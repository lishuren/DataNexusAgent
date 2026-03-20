#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
LOG_DIR="$ROOT_DIR/.logs"

mkdir -p "$LOG_DIR"

kill_port() {
  local port="$1"
  local pids

  pids=$(lsof -ti:"$port" || true)
  if [[ -n "$pids" ]]; then
    echo "Killing process(es) on port $port: $pids"
    echo "$pids" | xargs kill -9
  fi
}

kill_port 5000
kill_port 5173

cd "$ROOT_DIR/backend"
ASPNETCORE_ENVIRONMENT=Development dotnet run >"$LOG_DIR/backend.log" 2>&1 &
BACKEND_PID=$!

echo "Backend started (PID $BACKEND_PID). Logs: $LOG_DIR/backend.log"

cd "$ROOT_DIR/frontend"
if [[ ! -d "node_modules" ]]; then
  npm install
fi
npm run dev >"$LOG_DIR/frontend.log" 2>&1 &
FRONTEND_PID=$!

echo "Frontend started (PID $FRONTEND_PID). Logs: $LOG_DIR/frontend.log"

echo "Press Ctrl+C to stop both processes."

cleanup() {
  echo "Stopping processes..."
  kill "$BACKEND_PID" "$FRONTEND_PID" 2>/dev/null || true
}

trap cleanup INT TERM
wait
