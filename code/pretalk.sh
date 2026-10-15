#!/usr/bin/env bash
# Run before going on stage. Checks everything the four demos need, builds,
# and loads the local models so the first live run doesn't wait on a cold load.
#
#   ./pretalk.sh          check, build, warm (models stay loaded until unloaded)
#   ./pretalk.sh unload   free the memory after the talk
set -uo pipefail
cd "$(dirname "$0")"
OLLAMA=http://localhost:11434
MODELS=(llama3.2 nomic-embed-text)
ok()   { printf '  \033[32mok\033[0m    %s\n' "$1"; }
fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; FAILED=1; }
FAILED=0

if [ "${1:-}" = "unload" ]; then
  for m in "${MODELS[@]}"; do
    curl -s "$OLLAMA/api/generate" -d "{\"model\":\"$m\",\"keep_alive\":0}" >/dev/null && ok "unloaded $m"
  done
  exit 0
fi

echo "Local (F02, F07; embeddings for F05):"
if curl -s -m 3 "$OLLAMA/api/tags" >/dev/null; then ok "ollama is running"; else fail "ollama is not running: start it with 'ollama serve'"; fi
for m in "${MODELS[@]}"; do
  if curl -s -m 3 "$OLLAMA/api/tags" | grep -q "\"$m:"; then ok "$m is pulled"; else fail "$m missing: ollama pull $m"; fi
done

echo "Cloud (F05 answers, F10):"
if ./key.sh status | grep -q "is set"; then ok "Foundry key is set"; else fail "no Foundry key: ./key.sh set"; fi
if curl -s -m 5 -o /dev/null https://trailhead-ai-workshop.openai.azure.com; then ok "Azure endpoint reachable"; else fail "Azure endpoint unreachable: check Wi-Fi or hotspot"; fi

echo "Build:"
if dotnet build -v q -nologo 2>&1 | grep -q "Build succeeded"; then ok "dotnet build"; else fail "dotnet build failed: run 'dotnet build' to see why"; fi

echo "Warm-up:"
# keep_alive -1 keeps each model in memory until './pretalk.sh unload'.
curl -s "$OLLAMA/api/generate" -d '{"model":"llama3.2","keep_alive":-1}' >/dev/null && ok "llama3.2 loaded"
curl -s "$OLLAMA/api/embed" -d '{"model":"nomic-embed-text","input":"warm","keep_alive":-1}' >/dev/null && ok "nomic-embed-text loaded"
dotnet run --no-build -- triage >/dev/null 2>&1 && ok "triage ran once" || fail "triage failed: dotnet run -- triage"

if grep -q "commercial photography" Triage.cs; then
  fail "Triage.cs still has the inq-0030 fix from last time: undo it so the demo starts at 17/20"
else
  ok "Triage.cs is back to the 17/20 starting point"
fi

echo
[ "$FAILED" = 0 ] && echo "Ready." || echo "Fix the FAIL lines above."
exit "$FAILED"
