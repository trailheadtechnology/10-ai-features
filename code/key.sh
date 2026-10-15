#!/usr/bin/env bash
# The Azure (Foundry) key for the cloud demos (rag, agent), kept in .NET
# user-secrets: a file under ~/.microsoft/usersecrets, never in this folder,
# so git cannot see it.
#
#   ./key.sh set       paste the key at the prompt (hidden, not in shell history)
#   ./key.sh remove    delete it, e.g. after the talk
#   ./key.sh status    say whether it is set, without printing it
set -euo pipefail
cd "$(dirname "$0")"

case "${1:-status}" in
  set)
    read -rsp "Foundry key: " key; echo
    [ -n "$key" ] || { echo "No key entered; nothing changed."; exit 1; }
    dotnet user-secrets set Foundry:Key "$key" >/dev/null
    echo "Key set." ;;
  remove)
    dotnet user-secrets remove Foundry:Key >/dev/null 2>&1 || true
    echo "Key removed." ;;
  status)
    if dotnet user-secrets list 2>/dev/null | grep -q '^Foundry:Key'; then
      echo "Key is set (user-secrets, outside the repo)."
    else
      echo "Key is NOT set. Run: ./key.sh set"
    fi ;;
  *)
    echo "usage: ./key.sh set | remove | status"; exit 1 ;;
esac
