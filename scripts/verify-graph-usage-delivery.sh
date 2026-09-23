#!/usr/bin/env bash
# End-to-end delivery check for the graph model/token display feature.
# Runs all three component checks (core, ui, docs) and prints GRAPH_USAGE_DELIVERY_OK.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

bash "$ROOT/scripts/verify-graph-usage-core.sh"
bash "$ROOT/scripts/verify-graph-usage-ui.sh"
bash "$ROOT/scripts/verify-graph-usage-docs.sh"

echo "GRAPH_USAGE_DELIVERY_OK"
