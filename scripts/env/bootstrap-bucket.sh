#!/usr/bin/env bash
# Per-environment object-storage bootstrap (dev, staging, prod): create the
# bucket once, put the pending/ 1-day lifecycle rule on it, block public access
# outside dev, and verify what the store now answers.
#
# Idempotent: a bucket that already exists is accepted, and the lifecycle rule
# is replaced with the same content on every run.
#
# usage: scripts/env/bootstrap-bucket.sh <dev|staging|prod> <provision|verify> [--help]
#
#   provision  create the bucket, put the lifecycle rule, and for staging/prod
#              PUT the public-access block (all four flags true)
#   verify     read the lifecycle rule back, prove an anonymous bucket/object
#              read is refused, then HEAD the bucket with a signed request
#
# Configuration, read from the environment only (staging and prod have no
# defaults; dev falls back to the EVIDGATE_S3_* names scripts/dev/up.sh exports):
#   EVIDGATE__OBJECT__BUCKET       EVIDGATE__OBJECT__ENDPOINT
#   EVIDGATE__OBJECT__REGION       EVIDGATE__OBJECT__ACCESSKEY
#   EVIDGATE__OBJECT__SECRETKEY
#   EVIDGATE_BOOTSTRAP_CONFIRM_ENV must equal 'prod' for a prod run
#
# No credential is printed and none reaches argv: they are read from the
# environment and signed through a curl configuration on stdin.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
# shellcheck source=scripts/env/lib-bucket.sh
. "$SCRIPT_DIR/lib-bucket.sh"

LIFECYCLE_FILE="$ROOT/infra/seaweedfs/pending-lifecycle.xml"

usage() {
  cat <<'USAGE'
Environment object-storage bootstrap.

usage: scripts/env/bootstrap-bucket.sh <dev|staging|prod> <provision|verify> [--help]

  provision  create the bucket, put the pending/ 1-day lifecycle rule from
             infra/seaweedfs/pending-lifecycle.xml, and for staging/prod put the
             public-access block (BlockPublicAcls, IgnorePublicAcls,
             BlockPublicPolicy, RestrictPublicBuckets all true)
  verify     read the lifecycle rule back, require an anonymous bucket GET and
             anonymous object GET to be refused (401/403), then HEAD the bucket
             with a signed request

Environment (staging and prod require every key; dev falls back to the
EVIDGATE_S3_* names scripts/dev/up.sh exports):
  EVIDGATE__OBJECT__BUCKET       bucket name
  EVIDGATE__OBJECT__ENDPOINT     absolute http(s) S3 endpoint; empty selects the
                                 provider endpoint in a real cloud
  EVIDGATE__OBJECT__REGION       SigV4 signing region
  EVIDGATE__OBJECT__ACCESSKEY    signing identity
  EVIDGATE__OBJECT__SECRETKEY    signing secret
  EVIDGATE_BOOTSTRAP_CONFIRM_ENV must be 'prod' for a prod run

The command prints HTTP status codes and the bucket name, never a credential.
USAGE
}

die() {
  printf '%s: %s\n' "${0##*/}" "$@" >&2
  exit 1
}

info() { printf '%s: %s\n' "${0##*/}" "$*"; }

env_name=""
action=""
for arg in "$@"; do
  case "$arg" in
    -h | --help)
      usage
      exit 0
      ;;
    *)
      if [ -z "$env_name" ]; then
        env_name="$arg"
      elif [ -z "$action" ]; then
        action="$arg"
      else
        die "unexpected argument '$arg' (try --help)"
      fi
      ;;
  esac
done

if [ -z "$env_name" ] || [ -z "$action" ]; then
  die "usage: scripts/env/bootstrap-bucket.sh <dev|staging|prod> <provision|verify> [--help]"
fi

case "$env_name" in
  dev | staging | prod) ;;
  *) die "unknown environment '$env_name' (expected dev, staging or prod)" ;;
esac
case "$action" in
  provision | verify) ;;
  *) die "unknown action '$action' (expected provision or verify)" ;;
esac

# The environment name alone is too easy to leave at its default in a deploy
# shell, so prod also demands the operator to name the target explicitly.
if [ "$env_name" = "prod" ] && [ "${EVIDGATE_BOOTSTRAP_CONFIRM_ENV:-}" != "prod" ]; then
  die "refusing to bootstrap prod: set EVIDGATE_BOOTSTRAP_CONFIRM_ENV=prod (the key is read from the environment, never from a default)"
fi

if [ "$env_name" = "dev" ]; then
  bucket="${EVIDGATE__OBJECT__BUCKET:-${EVIDGATE_S3_BUCKET:-evidgate-dev}}"
  endpoint="${EVIDGATE__OBJECT__ENDPOINT:-${EVIDGATE_S3_ENDPOINT:-http://127.0.0.1:8333}}"
  region="${EVIDGATE__OBJECT__REGION:-${EVIDGATE_S3_REGION:-us-east-1}}"
  access="${EVIDGATE__OBJECT__ACCESSKEY:-${EVIDGATE_S3_ACCESS_KEY:-}}"
  sign_key="${EVIDGATE__OBJECT__SECRETKEY:-${EVIDGATE_S3_SECRET_KEY:-}}"
else
  bucket="${EVIDGATE__OBJECT__BUCKET:-}"
  endpoint="${EVIDGATE__OBJECT__ENDPOINT:-}"
  region="${EVIDGATE__OBJECT__REGION:-}"
  access="${EVIDGATE__OBJECT__ACCESSKEY:-}"
  sign_key="${EVIDGATE__OBJECT__SECRETKEY:-}"
fi

missing=""
[ -n "$bucket" ] || missing="$missing EVIDGATE__OBJECT__BUCKET"
[ -n "$endpoint" ] || missing="$missing EVIDGATE__OBJECT__ENDPOINT"
[ -n "$region" ] || missing="$missing EVIDGATE__OBJECT__REGION"
[ -n "$access" ] || missing="$missing EVIDGATE__OBJECT__ACCESSKEY"
[ -n "$sign_key" ] || missing="$missing EVIDGATE__OBJECT__SECRETKEY"
if [ -n "$missing" ]; then
  die "missing required environment:$missing (no value is used for a missing key)"
fi

# Staging and prod must not share a bucket. A file that takes the name from the
# environment declares no literal, so this check can only refuse a name that is
# literally written into the other environment's file.
evg_env_bucket_literal() {
  awk '
    /"EVIDGATE__OBJECT__BUCKET"[[:space:]]*:/ { inside = 1; next }
    inside && /"value"[[:space:]]*:/ {
      value = $0
      sub(/^[^:]*:[[:space:]]*"/, "", value)
      sub(/".*$/, "", value)
      print value
      exit
    }
    inside && /^[[:space:]]*}/ { exit }
  ' "$1"
}

if [ "$env_name" != "dev" ]; then
  if [ "$env_name" = "staging" ]; then other_env="prod"; else other_env="staging"; fi
  other_file="$ROOT/infra/$other_env/environment.json"
  if [ -r "$other_file" ]; then
    other_bucket="$(evg_env_bucket_literal "$other_file")"
    if [ -n "$other_bucket" ] && [ "$other_bucket" = "$bucket" ]; then
      die "refusing to bootstrap $env_name: EVIDGATE__OBJECT__BUCKET equals the name '$other_bucket' declared for $other_env in infra/$other_env/environment.json"
    fi
  fi
fi

evg_s3_configure "$access" "$sign_key" "$region"

if [ "$action" = "provision" ]; then
  info "provisioning bucket '$bucket' at $endpoint ($env_name)"
  evg_s3_create_bucket "$bucket" "$endpoint"
  evg_s3_put_lifecycle "$bucket" "$endpoint" "$LIFECYCLE_FILE"
  if [ "$env_name" = "dev" ]; then
    info "bucket '$bucket' ready: pending/ 1-day lifecycle rule applied"
  else
    evg_s3_put_public_access_block "$bucket" "$endpoint"
    info "bucket '$bucket' ready: pending/ 1-day lifecycle rule and public-access block applied"
  fi
  exit 0
fi

lifecycle_out="$(mktemp "${TMPDIR:-/tmp}/evidgate-bucket-verify.XXXXXX")"
trap 'rm -f "$lifecycle_out"' EXIT

evg_s3_get_lifecycle "$bucket" "$endpoint" "$lifecycle_out"
if ! grep -Fq '<Prefix>pending/</Prefix>' "$lifecycle_out"; then
  die "bucket '$bucket' has no pending/ lifecycle rule"
fi
if ! grep -Fq '<Days>1</Days>' "$lifecycle_out"; then
  die "the pending/ lifecycle rule of bucket '$bucket' is not a 1-day expiry"
fi

bucket_url="$(evg_s3_bucket_url "$endpoint" "$bucket")"
anonymous_bucket="$(evg_s3_anonymous_status GET "$bucket_url")"
case "$anonymous_bucket" in
  401 | 403) ;;
  *) die "an anonymous GET of bucket '$bucket' answered HTTP ${anonymous_bucket:-none}: the bucket must refuse unauthenticated reads" ;;
esac

probe_key="pending/env-bootstrap-verify-${RANDOM}${RANDOM}"
anonymous_object="$(evg_s3_anonymous_status GET "$bucket_url/$probe_key")"
case "$anonymous_object" in
  401 | 403) ;;
  *) die "an anonymous GET of an object key in bucket '$bucket' answered HTTP ${anonymous_object:-none}: the bucket must refuse unauthenticated reads" ;;
esac

evg_s3_signed_bucket_head "$bucket" "$endpoint"
info "bucket '$bucket' verified: pending/ 1-day lifecycle rule, anonymous reads refused, signed access accepted"
