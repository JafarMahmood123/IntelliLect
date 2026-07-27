#!/bin/sh
# Ollama model lifecycle manager for the LiveAssistant stack (dev).
#
# Ollama runs on the HOST (not in Docker), reached here over host.docker.internal. This helper:
#   1. on `docker compose up`   -> loads the chat model into RAM (keep_alive=-1) and only reports
#      healthy once it is resident, so live-assistant-service (which depends_on this,
#      service_healthy) does not start until the model is ready;
#   2. while the stack is up     -> every 60s it re-asserts the model with keep_alive=-1 (a no-op
#      refresh when resident, a RELOAD if transient memory pressure evicted it) so replies stay
#      warm;
#   3. on `docker compose down`  -> a SIGTERM trap RELEASES the model (keep_alive=0) so it does
#      not keep occupying RAM once the stack is gone.
#
# EMBED_MODEL is optional: leave it empty (embedder disabled on constrained hosts) and only the
# chat model is managed. If both chat+embedding are used, the host Ollama must allow >=2 loaded
# models (OLLAMA_MAX_LOADED_MODELS=2) AND have the RAM for both.
set -u

OLLAMA="${OLLAMA_URL:-http://host.docker.internal:11434}"
CHAT_MODEL="${CHAT_MODEL:-qwen2.5:3b-instruct}"
EMBED_MODEL="${EMBED_MODEL:-}"

load_all() {
  curl -sf "$OLLAMA/api/generate" \
    -d "{\"model\":\"$CHAT_MODEL\",\"prompt\":\"ok\",\"stream\":false,\"keep_alive\":-1}" -o /dev/null \
  || return 1
  if [ -n "${EMBED_MODEL:-}" ]; then
    curl -sf "$OLLAMA/api/embeddings" \
      -d "{\"model\":\"$EMBED_MODEL\",\"prompt\":\"ok\",\"keep_alive\":-1}" -o /dev/null || return 1
  fi
}

unload_all() {
  echo "[ollama-warmup] releasing models (keep_alive=0)..."
  curl -sf "$OLLAMA/api/generate"  -d "{\"model\":\"$CHAT_MODEL\",\"keep_alive\":0}"  -o /dev/null || true
  if [ -n "${EMBED_MODEL:-}" ]; then
    curl -sf "$OLLAMA/api/embeddings" -d "{\"model\":\"$EMBED_MODEL\",\"keep_alive\":0}" -o /dev/null || true
  fi
  echo "[ollama-warmup] models released."
}

# Release the model(s) when the stack stops (docker compose down / stop sends SIGTERM).
trap 'unload_all; exit 0' TERM INT

echo "[ollama-warmup] loading $CHAT_MODEL${EMBED_MODEL:+ + $EMBED_MODEL} (a cold load can take a minute on CPU)..."
until load_all; do
  echo "[ollama-warmup] Ollama not reachable or load failed; retrying in 5s..."
  sleep 5
done
echo "[ollama-warmup] load requests succeeded; marking ready."
loaded="$(curl -sf "$OLLAMA/api/ps" || true)"
resident=true
echo "$loaded" | grep -q "$CHAT_MODEL" || resident=false
if [ -n "${EMBED_MODEL:-}" ]; then
  echo "$loaded" | grep -q "$EMBED_MODEL" || resident=false
fi
if [ "$resident" != "true" ]; then
  echo "[ollama-warmup] WARNING: expected model(s) NOT all resident."
  echo "[ollama-warmup] If using both chat+embedding, set OLLAMA_MAX_LOADED_MODELS=2 on the HOST"
  echo "[ollama-warmup] Ollama and ensure enough free RAM, or replies pay a model-swap reload cost."
fi
touch /tmp/ready

# Stay alive so the container persists with the stack and the SIGTERM trap can fire on down.
# Every 60s, re-assert the model with keep_alive=-1: no-op if resident, RELOAD if evicted by
# transient memory pressure. `sleep & wait` so a stop signal interrupts the wait immediately.
while :; do
  sleep 60 &
  wait $!
  load_all >/dev/null 2>&1 || true
done
