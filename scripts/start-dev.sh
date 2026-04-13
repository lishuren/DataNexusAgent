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

ensure_backend_dependencies() {
  local restore_stamp="obj/project.assets.json"

  if [[ ! -f "$restore_stamp" || DataNexus.csproj -nt "$restore_stamp" || ../DataNexus.sln -nt "$restore_stamp" ]]; then
    echo "Restoring backend dependencies..."
    dotnet restore
  fi
}

ensure_frontend_dependencies() {
  local install_stamp="node_modules/.package-lock.json"

  if [[ ! -d "node_modules" ]]; then
    echo "Installing frontend dependencies..."
    npm install
    return
  fi

  if [[ ! -f "$install_stamp" || package.json -nt "$install_stamp" || package-lock.json -nt "$install_stamp" ]]; then
    echo "Refreshing frontend dependencies..."
    npm install
  fi
}

cd "$ROOT_DIR/backend"
ensure_backend_dependencies
ASPNETCORE_ENVIRONMENT=Development dotnet run >"$LOG_DIR/backend.log" 2>&1 &
BACKEND_PID=$!

echo "Backend started (PID $BACKEND_PID). Logs: $LOG_DIR/backend.log"

cd "$ROOT_DIR/frontend"
ensure_frontend_dependencies
npm run dev >"$LOG_DIR/frontend.log" 2>&1 &
FRONTEND_PID=$!

echo "Frontend started (PID $FRONTEND_PID). Logs: $LOG_DIR/frontend.log"

echo ""
echo "══════════════════════════════════════════"
echo "  Frontend:  http://localhost:5173"
echo "  Backend:   http://localhost:5000"
echo "  API:       http://localhost:5173/api/*  (proxied)"
echo "══════════════════════════════════════════"
echo ""
echo "Press Ctrl+C to stop both processes."

cleanup() {
  echo "Stopping processes..."
  kill "$BACKEND_PID" "$FRONTEND_PID" 2>/dev/null || true
}

trap cleanup INT TERM
wait
