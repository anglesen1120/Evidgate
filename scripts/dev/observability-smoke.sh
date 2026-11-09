#!/usr/bin/env bash
# Proves the compose `observability` profile (ADR-0012) end to end on a throwaway
# project: the default services stay out of the stack, only the profile starts,
# every published port is loopback-only, the retention flags are in force, and one
# trace, one metric and one log record sent to the collector are queryable in the
# three stores, the real Evidgate.Api reaches the same three stores through the
# collector, and a canary sent through the receiver never reaches a store.
# Needs docker, curl, python3 and dotnet (the Api is built and booted on a free
# port, with no database); touches no host port you use, no .env.dev and no
# volume of the dev stack.
#
# usage: scripts/dev/observability-smoke.sh [--project NAME]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
COMPOSE_FILE="$ROOT/infra/compose.dev.yml"
COLLECTOR_CONFIG_FILE="$ROOT/infra/observability/otel-collector.yaml"
project="evidgate-obs-smoke"
unset COMPOSE_PROFILES

while [ "$#" -gt 0 ]; do
  case "$1" in
    --project)
      project="${2:?--project needs a name}"
      shift 2
      ;;
    -h | --help)
      sed -n '2,12p' "$0"
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

# Bound every request: a service that accepts TCP but never answers must not hang the poll.
curl() { command curl --max-time 10 "$@"; }
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

api_pid=""
api_log=""
cleanup() {
  # Only the Api this run started: never a process the caller owns.
  if [ -n "$api_pid" ] && kill -0 "$api_pid" 2>/dev/null; then
    kill "$api_pid" 2>/dev/null || true
    for ((i = 0; i < 100; i++)); do
      kill -0 "$api_pid" 2>/dev/null || break
      sleep 0.1
    done
    kill -9 "$api_pid" 2>/dev/null || true
  fi
  if [ -n "$api_log" ]; then rm -f "$api_log"; fi
  compose --profile observability down -v >/dev/null 2>&1 || true
}
# The cleanup runs `down -v`: refuse a project that already owns containers or volumes
# (the dev stack, for one), so only what this run created is ever deleted.
existing="$(compose --profile observability ps -aq)" || fail "cannot list containers of project '$project'"
volumes="$(docker volume ls -q --filter "label=com.docker.compose.project=$project")" \
  || fail "cannot list volumes of project '$project'"
[ -z "$existing$volumes" ] \
  || fail "compose project '$project' already has containers or volumes; pick a fresh --project name"
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
# delay, so the budgets below are generous and a miss is a real failure.
await_hit_for() { # seconds, name, url, curl args..., grep pattern
  local budget=$1
  shift
  local deadline=$((SECONDS + budget))
  while [ "$SECONDS" -lt "$deadline" ]; do
    if curl -fsS -G "${@:2:$#-2}" 2>/dev/null | grep -q -- "${!#}"; then
      ok "$1 is queryable"
      return 0
    fi
    sleep 1
  done
  fail "$1 was not queryable within ${budget} s"
}
await_hit() { await_hit_for 60 "$@"; }
await_hit "trace" "$traces/select/jaeger/api/traces/$trace_id" "smoke-$run_id"
await_hit "log record" "$logs/select/logsql/query" --data-urlencode "query=smoke-$run_id" "smoke-$run_id"
await_hit "metric" "$prom/api/v1/query" --data-urlencode "query=evidgate_smoke_probe{run=\"$run_id\"}" "$run_id"

# The real Api: the producer the profile exists for. A span, a log record and a
# meter metric of the shipping host must reach the three stores through the
# collector. It boots on a free loopback port with the database-free composition
# tests/Contract.Tests uses (a dead-port connection string, the isolation
# self-check explicitly disabled): nothing asserted here needs PostgreSQL, so a
# database fixture would only make the smoke slower.
api_project="$ROOT/src/Evidgate.Api"
api_dll="$api_project/bin/Debug/net10.0/Evidgate.Api.dll"
api_port="$(free_port)"
api_log="${TMPDIR:-/tmp}/evidgate-obs-smoke-$run_id.api.log"

command -v dotnet >/dev/null 2>&1 || fail "dotnet is not on PATH (needed to build and start the Api)"
dotnet build "$api_project/Evidgate.Api.csproj" -c Debug --nologo -v quiet >"$api_log" 2>&1 \
  || fail "the Api did not build; tail of $api_log:
$(tail -n 20 "$api_log")"
[ -f "$api_dll" ] || fail "the Api build produced no $api_dll"

# An inherited EVIDGATE__* would silently override the composition below.
for key in $(compgen -e); do
  case "$key" in
    EVIDGATE__*) unset "$key" ;;
  esac
done

(
  cd "$(dirname "$api_dll")" || exit 1
  exec env \
    ASPNETCORE_URLS="http://127.0.0.1:$api_port" \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    EVIDGATE__DATABASE__CONNECTIONSTRING='Host=127.0.0.1;Port=1;Username=evidgate_app;Password=smoke;Database=evidgate;Timeout=2' \
    EVIDGATE__ISOLATION__SELFCHECK=Disabled \
    EVIDGATE__RUNS__CANCEL__SOFTSTOPDELAY=00:00:30 \
    EVIDGATE__RUNS__CANCEL__KILLDELAY=00:02:00 \
    EVIDGATE__TELEMETRY__OTLPENDPOINT="http://127.0.0.1:$EVIDGATE_OBS_OTLP_GRPC_PORT" \
    EVIDGATE__TELEMETRY__SERVICEVERSION="obs-smoke-$run_id" \
    EVIDGATE__TELEMETRY__SAMPLERATIO=1 \
    OTEL_METRIC_EXPORT_INTERVAL=2000 \
    dotnet Evidgate.Api.dll
) >>"$api_log" 2>&1 &
api_pid=$!

deadline=$((SECONDS + 120))
until curl -fsS -o /dev/null "http://127.0.0.1:$api_port/internal/health/live" 2>/dev/null; do
  kill -0 "$api_pid" 2>/dev/null || fail "the Api exited before it answered; tail of $api_log:
$(tail -n 20 "$api_log")"
  [ "$SECONDS" -lt "$deadline" ] || fail "the Api did not answer /internal/health/live within 120 s"
  sleep 1
done
ok "the Api answers /internal/health/live on 127.0.0.1:$api_port"

# The inbound traceparent picks the trace id, so the stored span is found by id,
# and the X-Request-Id it carries is this run's id: both must be in that span.
api_trace_id="$(python3 -c 'import secrets; print(secrets.token_hex(16))')"
api_span_id="$(python3 -c 'import secrets; print(secrets.token_hex(8))')"
curl -fsS -o /dev/null -H "X-Request-Id: $run_id" \
  -H "traceparent: 00-$api_trace_id-$api_span_id-01" \
  "http://127.0.0.1:$api_port/internal/health/live" \
  || fail "the Api refused the tagged health request"
status="$(curl -sS -o /dev/null -w '%{http_code}' \
  "http://127.0.0.1:$api_port/v1/auth/oidc/not-a-provider/start")"
[ "$status" = "404" ] || fail "an unknown login provider must answer 404, got $status"
ok "the Api served the tagged request and the 404 that records its meter counter"

await_hit "api span" "$traces/select/jaeger/api/traces/$api_trace_id" "$run_id"
api_trace="$(curl -fsS "$traces/select/jaeger/api/traces/$api_trace_id")"
case "$api_trace" in
  *evidgate-api*) ok "the stored span belongs to service.name evidgate-api" ;;
  *) fail "the stored span carries no service.name evidgate-api: $api_trace" ;;
esac
# The logging pipeline exports the state pairs rather than a rendered message, so
# the lifetime line is found in whichever field carries it: `*:` searches all.
await_hit "api log record" "$logs/select/logsql/query" --data-urlencode "query=*:$api_port" "$api_port"
# The meter export interval is the only reason these budgets are longer: Prometheus
# turns the resource's service.name into `job` and keeps the rest on target_info.
await_hit_for 90 "api meter metric" "$prom/api/v1/query" \
  --data-urlencode 'query=evidgate_login_attempts_total{job="evidgate-api",provider="unknown"}' 'evidgate_login_attempts_total'
await_hit_for 90 "api resource version" "$prom/api/v1/query" \
  --data-urlencode "query=target_info{job=\"evidgate-api\",service_version=\"obs-smoke-$run_id\"}" "obs-smoke-$run_id"

# Collector redaction: the contract puts the secret shapes at the collector, not
# in each application. A key that names a secret is deleted, a secret-shaped
# assignment is masked to ***, and all of it happens before the batch processor.
# The canaries are random per run and are never a real credential.
canary_authorization="$(python3 -c 'import secrets; print(secrets.token_hex(12))')"
canary_cookie="$(python3 -c 'import secrets; print(secrets.token_hex(12))')"
canary_password="$(python3 -c 'import secrets; print(secrets.token_hex(12))')"
canary_token="$(python3 -c 'import secrets; print(secrets.token_hex(12))')"
canary_session="$(python3 -c 'import secrets; print(secrets.token_hex(12))')"
redaction_id="redactionprobe$run_id"
redaction_trace_id="$(python3 -c 'import secrets; print(secrets.token_hex(16))')"
# Fresh timestamps: the Api build above can take minutes, and the earlier probes'
# stamp should not decide whether these two are inside the store's window.
redaction_now_ns="$(python3 -c 'import time; print(time.time_ns())')"
redaction_end_ns=$((redaction_now_ns + 1000000))

post logs "{\"resourceLogs\":[{\"resource\":$resource,\"scopeLogs\":[{\"logRecords\":[{\"timeUnixNano\":\"$redaction_now_ns\",\"severityText\":\"INFO\",\"body\":{\"stringValue\":\"$redaction_id password=$canary_password\"},\"attributes\":[{\"key\":\"authorization\",\"value\":{\"stringValue\":\"Bearer $canary_authorization\"}},{\"key\":\"cookie\",\"value\":{\"stringValue\":\"sid=$canary_cookie\"}},{\"key\":\"note\",\"value\":{\"stringValue\":\"token: $canary_token\"}}]}]}]}]}"
post traces "{\"resourceSpans\":[{\"resource\":$resource,\"scopeSpans\":[{\"spans\":[{\"traceId\":\"$redaction_trace_id\",\"spanId\":\"00f067aa0ba902b8\",\"name\":\"$redaction_id\",\"kind\":1,\"startTimeUnixNano\":\"$redaction_now_ns\",\"endTimeUnixNano\":\"$redaction_end_ns\",\"attributes\":[{\"key\":\"__Host-session\",\"value\":{\"stringValue\":\"$canary_session\"}}]}]}]}]}"

# Positive first: absence only means something once the record is queryable, so a
# slow flush cannot pass for a redacted value.
await_hit "redaction log record" "$logs/select/logsql/query" --data-urlencode "query=_msg:$redaction_id" "$redaction_id"
record="$(curl -fsS -G --data-urlencode "query=_msg:$redaction_id" "$logs/select/logsql/query")"
case "$record" in
  *"password=***"*) ok "the log body's password assignment is masked" ;;
  *) fail "the collector did not mask the log body's password assignment: $record" ;;
esac

# `*:` searches every field, so a value that survived in any attribute is found;
# a bare word would only ever look at the body.
assert_absent_from_logs() { # what, logsql query
  local hits
  hits="$(curl -fsS -G --data-urlencode "query=$2" "$logs/select/logsql/query")" \
    || fail "the log store did not answer the query for $1"
  [ -z "$hits" ] || fail "$1 reached the log store: $hits"
  ok "$1 is absent from the log store"
}
assert_absent_from_logs "the authorization attribute" "*:$canary_authorization"
assert_absent_from_logs "the cookie attribute" "*:$canary_cookie"
assert_absent_from_logs "the token assignment in an attribute value" "*:$canary_token"
assert_absent_from_logs "the password assignment in the log body" "*:$canary_password"

await_hit "redaction span" "$traces/select/jaeger/api/traces/$redaction_trace_id" "$redaction_id"
redaction_trace="$(curl -fsS "$traces/select/jaeger/api/traces/$redaction_trace_id")"
case "$redaction_trace" in
  *"$canary_session"*) fail "the __Host-session attribute reached the trace store: $redaction_trace" ;;
esac
ok "the __Host-session attribute is absent from the trace store"
echo "observability-smoke: PASS"
