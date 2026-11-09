#!/usr/bin/env bash
# Proves the compose `observability` profile (ADR-0012) end to end on a throwaway
# project: the default services stay out of the stack, only the profile starts,
# every published port is loopback-only, the retention flags are in force, and one
# trace, one metric and one log record sent to the collector are queryable in the
# three stores. Needs docker, curl and python3; touches no host port you use
# (free ports are picked), no .env.dev and no volume of the dev stack.
#
# usage: scripts/dev/observability-smoke.sh [--project NAME]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
COMPOSE_FILE="$ROOT/infra/compose.dev.yml"
COLLECTOR_CONFIG_FILE="$ROOT/infra/observability/otel-collector.yaml"
project="evidgate-obs-smoke"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --project)
      project="${2:?--project needs a name}"
      shift 2
      ;;
    -h | --help)
      sed -n '2,9p' "$0"
      exit 0
      ;;
    *)
      echo "unexpected argument '$1'" >&2
      exit 2
      ;;
  esac
done

fail() {
  echo "observability-smoke: FAIL: $*" >&2
  exit 1
}
ok() { echo "observability-smoke: ok: $*"; }

free_port() {
  python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])'
}

export EVIDGATE_OTEL_COLLECTOR_CONFIG
EVIDGATE_OTEL_COLLECTOR_CONFIG="$(<"$COLLECTOR_CONFIG_FILE")"
export EVIDGATE_OBS_OTLP_GRPC_PORT EVIDGATE_OBS_OTLP_HTTP_PORT
export EVIDGATE_OBS_PROMETHEUS_PORT EVIDGATE_OBS_LOGS_PORT EVIDGATE_OBS_TRACES_PORT
EVIDGATE_OBS_OTLP_GRPC_PORT="$(free_port)"
EVIDGATE_OBS_OTLP_HTTP_PORT="$(free_port)"
EVIDGATE_OBS_PROMETHEUS_PORT="$(free_port)"
EVIDGATE_OBS_LOGS_PORT="$(free_port)"
EVIDGATE_OBS_TRACES_PORT="$(free_port)"

compose() { docker compose -p "$project" -f "$COMPOSE_FILE" "$@"; }

# Compose validation: without the profile the stack is exactly the two stateful
# services; with it the four observability services join them.
default_services="$(compose config --services | sort | tr '\n' ' ')"
[ "$default_services" = "postgres seaweedfs " ] \
  || fail "the default stack must be 'postgres seaweedfs', got: $default_services"
ok "default stack unchanged: $default_services"
profile_services="$(compose --profile observability config --services | sort | tr '\n' ' ')"
[ "$profile_services" = "otel-collector postgres prometheus seaweedfs victorialogs victoriatraces " ] \
  || fail "unexpected services with the profile: $profile_services"
ok "profile adds: otel-collector prometheus victorialogs victoriatraces"

cleanup() { compose --profile observability down -v >/dev/null 2>&1 || true; }
trap cleanup EXIT

compose --profile observability up -d --wait --quiet-pull \
  otel-collector prometheus victorialogs victoriatraces
ok "profile services are up and healthy"

# Loopback only: no published port may bind 0.0.0.0 or ::.
bad="$(compose --profile observability ps --format json | python3 -c '
import json, sys
bad = []
for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    for row in (json.loads(line) if line.startswith("[") else [json.loads(line)]):
        for p in row.get("Publishers") or []:
            if p.get("PublishedPort") and p.get("URL") not in ("127.0.0.1",):
                bad.append("%s %s:%s" % (row["Service"], p.get("URL"), p.get("PublishedPort")))
print("\n".join(bad))')"
[ -z "$bad" ] || fail "published off loopback: $bad"
ok "every published port is bound to 127.0.0.1"

prom="http://127.0.0.1:$EVIDGATE_OBS_PROMETHEUS_PORT"
logs="http://127.0.0.1:$EVIDGATE_OBS_LOGS_PORT"
traces="http://127.0.0.1:$EVIDGATE_OBS_TRACES_PORT"
otlp="http://127.0.0.1:$EVIDGATE_OBS_OTLP_HTTP_PORT"

# The scratch-based Victoria images have no healthcheck, so wait for /health here.
for url in "http://127.0.0.1:$EVIDGATE_OBS_LOGS_PORT/health" "http://127.0.0.1:$EVIDGATE_OBS_TRACES_PORT/health"; do
  deadline=$((SECONDS + 60))
  until curl -fsS -o /dev/null "$url" 2>/dev/null; do
    [ "$SECONDS" -lt "$deadline" ] || fail "$url did not answer within 60 s"
    sleep 1
  done
done
ok "victorialogs and victoriatraces answer /health"

# Retention is read back from each running store, not from the compose file.
flags="$(curl -fsS "$prom/api/v1/status/flags")"
echo "$flags" | python3 -c '
import json, sys
f = json.load(sys.stdin)["data"]
assert f.get("storage.tsdb.retention.time") == "90d", f.get("storage.tsdb.retention.time")
assert f.get("storage.tsdb.retention.size") == "2GiB", f.get("storage.tsdb.retention.size")
' || fail "prometheus retention is not 90d / 2GiB"
ok "prometheus retention 90d, ceiling 2GiB"
curl -fsS "$logs/metrics" | grep -E '^flag\{name="retentionPeriod".*value="30d"' >/dev/null \
  || fail "victorialogs retentionPeriod is not 30d"
curl -fsS "$logs/metrics" | grep -E '^flag\{name="retention.maxDiskSpaceUsageBytes".*value="3GiB"' >/dev/null \
  || fail "victorialogs disk ceiling is not 3GiB"
ok "victorialogs retention 30d, ceiling 3GiB"
curl -fsS "$traces/metrics" | grep -E '^flag\{name="retentionPeriod".*value="7d"' >/dev/null \
  || fail "victoriatraces retentionPeriod is not 7d"
curl -fsS "$traces/metrics" | grep -E '^flag\{name="retention.maxDiskSpaceUsageBytes".*value="2GiB"' >/dev/null \
  || fail "victoriatraces disk ceiling is not 2GiB"
ok "victoriatraces retention 7d, ceiling 2GiB"

# One span, one metric point and one log record through the collector's OTLP/HTTP
# receiver, tagged with a run id so an old record can never satisfy the query.
run_id="$(python3 -c 'import secrets; print(secrets.token_hex(8))')"
trace_id="$(python3 -c 'import secrets; print(secrets.token_hex(16))')"
now_ns="$(python3 -c 'import time; print(time.time_ns())')"
end_ns=$((now_ns + 1000000))
resource="{\"attributes\":[{\"key\":\"service.name\",\"value\":{\"stringValue\":\"evg-smoke-$run_id\"}}]}"

post() {
  curl -fsS -o /dev/null -H 'Content-Type: application/json' -d "$2" "$otlp/v1/$1" \
    || fail "collector refused the $1 payload"
}
post traces "{\"resourceSpans\":[{\"resource\":$resource,\"scopeSpans\":[{\"spans\":[{\"traceId\":\"$trace_id\",\"spanId\":\"00f067aa0ba902b7\",\"name\":\"smoke-$run_id\",\"kind\":1,\"startTimeUnixNano\":\"$now_ns\",\"endTimeUnixNano\":\"$end_ns\"}]}]}]}"
post metrics "{\"resourceMetrics\":[{\"resource\":$resource,\"scopeMetrics\":[{\"metrics\":[{\"name\":\"evidgate_smoke_probe\",\"gauge\":{\"dataPoints\":[{\"asDouble\":1,\"timeUnixNano\":\"$now_ns\",\"attributes\":[{\"key\":\"run\",\"value\":{\"stringValue\":\"$run_id\"}}]}]}}]}]}]}"
post logs "{\"resourceLogs\":[{\"resource\":$resource,\"scopeLogs\":[{\"logRecords\":[{\"timeUnixNano\":\"$now_ns\",\"severityText\":\"INFO\",\"body\":{\"stringValue\":\"smoke-$run_id\"}}]}]}]}"

# Bounded poll: the batch processor and each store's flush interval are the only
# delay, so 60 s is generous and a miss is a real failure.
await_hit() { # name, url, grep pattern
  local deadline=$((SECONDS + 60))
  while [ "$SECONDS" -lt "$deadline" ]; do
    if curl -fsS -G "${@:2:$#-2}" 2>/dev/null | grep -q -- "${!#}"; then
      ok "$1 is queryable"
      return 0
    fi
    sleep 1
  done
  fail "$1 was not queryable within 60 s"
}
await_hit "trace" "$traces/select/jaeger/api/traces/$trace_id" "smoke-$run_id"
await_hit "log record" "$logs/select/logsql/query" --data-urlencode "query=smoke-$run_id" "smoke-$run_id"
await_hit "metric" "$prom/api/v1/query" --data-urlencode "query=evidgate_smoke_probe{run=\"$run_id\"}" "$run_id"
echo "observability-smoke: PASS"
