#!/usr/bin/env bash
# The observability profile part of up.sh (compose profile `observability`,
# docs/runbooks/telemetry.md §5). Sourced by up.sh, never executed; it relies on
# lib.sh, info/die and port_conflict from up.sh.

# Defaults, validation and exports for the profile. Without --observability the
# Api and Worker keep exporting nowhere unless .env.dev sets an endpoint.
evg_obs_configure() {
  : "${EVIDGATE_OBS_OTLP_GRPC_PORT:=4317}"
  : "${EVIDGATE_OBS_OTLP_HTTP_PORT:=4318}"
  : "${EVIDGATE_OBS_PROMETHEUS_PORT:=9090}"
  : "${EVIDGATE_OBS_LOGS_PORT:=9428}"
  : "${EVIDGATE_OBS_TRACES_PORT:=10428}"
  local obs_port
  for obs_port in EVIDGATE_OBS_OTLP_GRPC_PORT EVIDGATE_OBS_OTLP_HTTP_PORT \
    EVIDGATE_OBS_PROMETHEUS_PORT EVIDGATE_OBS_LOGS_PORT EVIDGATE_OBS_TRACES_PORT; do
    evg_require_port "$obs_port" "${!obs_port}"
  done
  EVIDGATE_OTEL_COLLECTOR_CONFIG="$(<"$EVG_ROOT/infra/observability/otel-collector.yaml")"
  : "${EVIDGATE__TELEMETRY__OTLPENDPOINT:=http://127.0.0.1:${EVIDGATE_OBS_OTLP_GRPC_PORT}}"
  export COMPOSE_PROFILES=observability
  export EVIDGATE_OTEL_COLLECTOR_CONFIG EVIDGATE__TELEMETRY__OTLPENDPOINT
  export EVIDGATE_OBS_OTLP_GRPC_PORT EVIDGATE_OBS_OTLP_HTTP_PORT
  export EVIDGATE_OBS_PROMETHEUS_PORT EVIDGATE_OBS_LOGS_PORT EVIDGATE_OBS_TRACES_PORT
}

# Before compose starts: refuse a taken port and restart running hosts.
evg_obs_preflight() {
  # A taken port under colima silently drops the publish, so the app would ship
  # telemetry to a foreign listener while the stack reports ready.
  local pair obs_svc obs_num obs_var
  for pair in "otel-collector:$EVIDGATE_OBS_OTLP_GRPC_PORT:EVIDGATE_OBS_OTLP_GRPC_PORT" \
    "otel-collector:$EVIDGATE_OBS_OTLP_HTTP_PORT:EVIDGATE_OBS_OTLP_HTTP_PORT" \
    "prometheus:$EVIDGATE_OBS_PROMETHEUS_PORT:EVIDGATE_OBS_PROMETHEUS_PORT" \
    "victorialogs:$EVIDGATE_OBS_LOGS_PORT:EVIDGATE_OBS_LOGS_PORT" \
    "victoriatraces:$EVIDGATE_OBS_TRACES_PORT:EVIDGATE_OBS_TRACES_PORT"; do
    IFS=: read -r obs_svc obs_num obs_var <<<"$pair"
    if port_conflict "$obs_svc" "$obs_num"; then
      die "port $obs_num on 127.0.0.1 is already in use, so $obs_svc cannot be published there.
     Set $obs_var in .env.dev or stop the process holding the port."
    fi
  done
  # Running hosts keep the environment they started with: restart them so they
  # pick up the collector endpoint.
  if evg_host_is_running api || evg_host_is_running worker; then
    info "restarting the Api and Worker so they export to the collector"
    evg_stop_hosts
  fi
}
