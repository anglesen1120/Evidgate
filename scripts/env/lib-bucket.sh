#!/usr/bin/env bash
# Shared S3 bucket helpers for the environment bootstrap and the dev stack.
# Sourced, never executed; the caller keeps `set -euo pipefail`.
#
# Every request is signed by curl's own SigV4 implementation, with the signing
# credentials read from a curl configuration passed on stdin. They therefore
# never reach argv (so `ps` cannot see them), a file or a log line; a caller
# configures them once through evg_s3_configure.

# An endpoint that stops answering fails the run instead of hanging it.
EVG_S3_CONNECT_TIMEOUT_SECONDS=10
EVG_S3_MAX_TIME_SECONDS=60
# Set by evg_s3_configure when the environment must sign over TLS only.
EVG_S3_HTTPS_ONLY=0

# Transport for every request. A caller that must run curl elsewhere (the dev
# stack uses the SeaweedFS container's own curl) overrides this function.
evg_s3_curl() { curl "$@"; }

# Transport options every request inherits, emitted as curl configuration lines
# so a request cannot pin a pipeline, and where TLS is required the transfer
# cannot be redirected to plain http.
evg_s3_transport_config() {
  printf '%s\n' \
    "connect-timeout = $EVG_S3_CONNECT_TIMEOUT_SECONDS" \
    "max-time = $EVG_S3_MAX_TIME_SECONDS"
  if [ "$EVG_S3_HTTPS_ONLY" = "1" ]; then
    printf '%s\n' 'proto = "=https"'
  fi
}

evg_s3_fail() {
  printf '%s: %s\n' "${0##*/}" "object storage: $*" >&2
  exit 1
}

# Sets the signing identity: REGION is the SigV4 scope and, on an AWS regional
# endpoint, the CreateBucket location. REQUIRED_HTTPS=1 restricts every request
# to https, because staging and prod must not sign over a plaintext link.
evg_s3_configure() {
  local access="$1" secret="$2" region="$3" require_https="${4:-0}"

  [ -n "$access" ] || evg_s3_fail "the object-storage access key is empty"
  [ -n "$secret" ] || evg_s3_fail "the object-storage secret key is empty"
  [ -n "$region" ] || evg_s3_fail "the object-storage signing region is empty"
  # The region lands in the signing scope and in the CreateBucket body, so it is
  # restricted to the characters every region name uses.
  case "$region" in
    *[!a-z0-9-]*) evg_s3_fail "the object-storage signing region may only contain a-z, 0-9 and '-'" ;;
  esac
  case "$access" in
    *:*) evg_s3_fail "the object-storage access key contains ':' (curl splits its 'user' value at the first colon)" ;;
  esac
  # The credentials ride a double-quoted curl configuration string; these four
  # characters cannot be represented there, and quoting them instead would mean
  # guessing at curl's parser.
  case "$access$secret" in
    *[[:cntrl:]]* | *'"'* | *\\*)
      evg_s3_fail "the object-storage credentials contain a control character, a double quote or a backslash, which the signing configuration cannot carry"
      ;;
  esac

  EVG_S3_USER="$access:$secret"
  EVG_S3_SIGV4="aws:amz:$region:s3"
  EVG_S3_REGION="$region"
  if [ "$require_https" = "1" ]; then
    EVG_S3_HTTPS_ONLY=1
  else
    EVG_S3_HTTPS_ONLY=0
  fi
}

# Escapes one value for a double-quoted curl configuration string: backslash and
# quote are the parser's own escapes, and a literal newline would end the line.
evg_s3_config_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  printf '%s' "$value"
}

# One signed request. BODY is embedded in the configuration because stdin is the
# configuration itself; an empty body sends none. Writes the response body to
# OUTPUT (a path or /dev/null) and prints the HTTP status code.
evg_s3_request() {
  local method="$1" url="$2" body="${3:-}" output="${4:-/dev/null}" code
  local -a config verb

  if [ -z "${EVG_S3_USER:-}" ] || [ -z "${EVG_S3_SIGV4:-}" ]; then
    evg_s3_fail "evg_s3_configure must run before any request"
  fi
  config=(
    'silent'
    'show-error'
    "aws-sigv4 = \"$EVG_S3_SIGV4\""
    "user = \"$EVG_S3_USER\""
  )
  if [ -n "$body" ]; then
    config+=("data-binary = \"$(evg_s3_config_escape "$body")\"")
  fi
  # `curl --request HEAD` waits for a body a HEAD response never carries; --head
  # is the option that ends after the headers.
  if [ "$method" = "HEAD" ]; then
    verb=(--head)
  else
    verb=(--request "$method")
  fi

  if ! code="$(
    {
      evg_s3_transport_config
      printf '%s\n' "${config[@]}"
    } |
      evg_s3_curl --config - "${verb[@]}" --output "$output" --write-out '%{http_code}' "$url"
  )"; then
    evg_s3_fail "the S3 request did not complete (curl reported the error above)"
  fi
  code="${code##*$'\n'}"
  case "$code" in
    [0-9][0-9][0-9]) ;;
    *) evg_s3_fail "the S3 request returned no HTTP status" ;;
  esac
  printf '%s' "$code"
}

# Path-style URL of a bucket; every S3-compatible emulator and the provider
# accept it, and the configured endpoint may carry a trailing slash.
evg_s3_bucket_url() {
  local endpoint="${1%/}" bucket="$2"

  [ -n "$endpoint" ] || evg_s3_fail "the S3 endpoint is empty"
  [ -n "$bucket" ] || evg_s3_fail "the bucket name is empty"
  case "$bucket" in
    */* | *[[:space:]]*) evg_s3_fail "the bucket name '$bucket' contains a slash or whitespace" ;;
  esac
  printf '%s/%s' "$endpoint" "$bucket"
}

# Host of an absolute http(s) endpoint, without a port. Refuses the two shapes
# that would make curl read part of the endpoint as an option or send the
# credentials somewhere else: a leading '-' and credentials in the authority.
evg_s3_endpoint_host() {
  local endpoint="$1" rest authority host

  case "$endpoint" in
    -*) evg_s3_fail "the S3 endpoint must not start with '-' (curl would read it as an option)" ;;
  esac
  case "$endpoint" in
    *[[:cntrl:]]* | *[[:space:]]*)
      evg_s3_fail "the S3 endpoint contains whitespace or a control character"
      ;;
  esac
  case "$endpoint" in
    https://*) rest="${endpoint#https://}" ;;
    http://*) rest="${endpoint#http://}" ;;
    *) evg_s3_fail "the S3 endpoint must be an absolute http(s) URL" ;;
  esac
  authority="${rest%%/*}"
  [ -n "$authority" ] || evg_s3_fail "the S3 endpoint has no host"
  case "$authority" in
    *@*)
      evg_s3_fail "the S3 endpoint carries credentials before its first '/'; keep them in EVIDGATE__OBJECT__ACCESSKEY and EVIDGATE__OBJECT__SECRETKEY"
      ;;
  esac
  host="${authority%%:*}"
  printf '%s' "$host"
}

# Refuses an endpoint the environment may not use. REQUIRED_HTTPS=1 is for
# staging and prod, where a plain-http endpoint would send the signing
# credentials in clear; dev keeps http because its store is a loopback container.
evg_s3_validate_endpoint() {
  local endpoint="$1" require_https="${2:-0}"

  evg_s3_endpoint_host "$endpoint" >/dev/null
  if [ "$require_https" = "1" ]; then
    case "$endpoint" in
      https://*) ;;
      *) evg_s3_fail "the S3 endpoint of staging and prod must use https: a plain-http endpoint sends the signing credentials in clear" ;;
    esac
  fi
}

# AWS regional endpoints are the only ones that want the region in the
# CreateBucket body; an emulator or a third-party S3 reads that element as an
# error.
evg_s3_aws_host() {
  case "$(evg_s3_endpoint_host "$1")" in
    *.amazonaws.com) return 0 ;;
  esac
  return 1
}

# Creates the bucket. A 409 means it already exists, which is success for a
# re-run; verify's signed read then proves the existing bucket is usable with
# these credentials. Outside us-east-1 an AWS endpoint needs the location in the
# body; the emulator path sends none, as before.
evg_s3_create_bucket() {
  local bucket="$1" endpoint="$2" body="" code

  if [ "${EVG_S3_REGION:-}" != "us-east-1" ] && evg_s3_aws_host "$endpoint"; then
    body="<CreateBucketConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><LocationConstraint>$EVG_S3_REGION</LocationConstraint></CreateBucketConfiguration>"
  fi
  code="$(evg_s3_request PUT "$(evg_s3_bucket_url "$endpoint" "$bucket")" "$body" /dev/null)"
  case "$code" in
    2[0-9][0-9] | 409) ;;
    *) evg_s3_fail "creating bucket '$bucket' at $endpoint failed (HTTP $code)" ;;
  esac
}

# True when FILE holds one rule that is enabled, scoped to pending/ and expiring
# after one day. The three conditions are read from the same rule: a disabled
# rule, or a status that sits in another rule, does not satisfy this.
evg_s3_lifecycle_rule_enabled_pending_1day() {
  [ -r "$1" ] || evg_s3_fail "the lifecycle response '$1' is not readable"

  awk '
    { document = document $0 }
    END {
      rules = split(document, rule, "</Rule>")
      for (i = 1; i <= rules; i++) {
        if (rule[i] ~ /<Status>[[:space:]]*Enabled[[:space:]]*<\/Status>/ &&
            rule[i] ~ /<Prefix>[[:space:]]*pending\/[[:space:]]*<\/Prefix>/ &&
            rule[i] ~ /<Expiration>[[:space:]]*<Days>[[:space:]]*1[[:space:]]*<\/Days>/) {
          exit 0
        }
      }
      exit 1
    }
  ' "$1"
}

# Replaces the bucket's lifecycle configuration with FILE, verbatim.
evg_s3_put_lifecycle() {
  local bucket="$1" endpoint="$2" file="$3" code

  [ -r "$file" ] || evg_s3_fail "the lifecycle rule file '$file' is not readable"
  code="$(evg_s3_request PUT "$(evg_s3_bucket_url "$endpoint" "$bucket")?lifecycle=" "$(<"$file")" /dev/null)"
  case "$code" in
    2[0-9][0-9]) ;;
    *) evg_s3_fail "putting the lifecycle rule on '$bucket' at $endpoint failed (HTTP $code)" ;;
  esac
}

# Reads the lifecycle configuration into OUTPUT for the caller to inspect.
evg_s3_get_lifecycle() {
  local bucket="$1" endpoint="$2" output="$3" code

  code="$(evg_s3_request GET "$(evg_s3_bucket_url "$endpoint" "$bucket")?lifecycle=" '' "$output")"
  case "$code" in
    200) ;;
    *) evg_s3_fail "reading the lifecycle rule of '$bucket' at $endpoint failed (HTTP $code)" ;;
  esac
}

# Blocks every public-access path: ACLs ignored and blocked, no public policy,
# and buckets with a public policy restricted. Required for staging and prod;
# the dev gateway has no such sub-resource.
evg_s3_put_public_access_block() {
  local bucket="$1" endpoint="$2" code
  local body='<PublicAccessBlockConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><BlockPublicAcls>true</BlockPublicAcls><IgnorePublicAcls>true</IgnorePublicAcls><BlockPublicPolicy>true</BlockPublicPolicy><RestrictPublicBuckets>true</RestrictPublicBuckets></PublicAccessBlockConfiguration>'

  code="$(evg_s3_request PUT "$(evg_s3_bucket_url "$endpoint" "$bucket")?publicAccessBlock=" "$body" /dev/null)"
  case "$code" in
    2[0-9][0-9]) ;;
    *) evg_s3_fail "putting the public-access block on '$bucket' at $endpoint failed (HTTP $code)" ;;
  esac
}

# Proves the credentials sign a request the store accepts.
evg_s3_signed_bucket_head() {
  local bucket="$1" endpoint="$2" code

  code="$(evg_s3_request HEAD "$(evg_s3_bucket_url "$endpoint" "$bucket")" '' /dev/null)"
  case "$code" in
    2[0-9][0-9]) ;;
    *) evg_s3_fail "the signed HEAD of bucket '$bucket' at $endpoint failed (HTTP $code): the endpoint or the credentials are wrong" ;;
  esac
}

# Status of an unsigned request: published buckets answer it, a private bucket
# refuses it. The URL is not a credential (no presign), so it can be built by
# the caller.
evg_s3_anonymous_status() {
  local method="$1" url="$2" code
  local -a verb

  if [ "$method" = "HEAD" ]; then
    verb=(--head)
  else
    verb=(--request "$method")
  fi
  if ! code="$(evg_s3_transport_config |
    evg_s3_curl --config - "${verb[@]}" --silent --output /dev/null --write-out '%{http_code}' "$url")"; then
    evg_s3_fail "the anonymous request did not complete (curl reported the error above)"
  fi
  printf '%s' "$code"
}
