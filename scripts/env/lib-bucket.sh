#!/usr/bin/env bash
# Shared S3 bucket helpers for the environment bootstrap and the dev stack.
# Sourced, never executed; the caller keeps `set -euo pipefail`.
#
# Every request is signed by curl's own SigV4 implementation, with the signing
# credentials read from a curl configuration passed on stdin. They therefore
# never reach argv (so `ps` cannot see them), a file or a log line; a caller
# configures them once through evg_s3_configure.

# Transport for every request. A caller that must run curl elsewhere (the dev
# stack uses the SeaweedFS container's own curl) overrides this function.
evg_s3_curl() { curl "$@"; }

evg_s3_fail() {
  printf '%s: %s\n' "${0##*/}" "object storage: $*" >&2
  exit 1
}

# Sets the signing identity. Region selects the SigV4 credential scope; the dev
# stack signs with us-east-1, the region .env.dev declares.
evg_s3_configure() {
  local access="$1" secret="$2" region="$3"

  [ -n "$access" ] || evg_s3_fail "the object-storage access key is empty"
  [ -n "$secret" ] || evg_s3_fail "the object-storage secret key is empty"
  [ -n "$region" ] || evg_s3_fail "the object-storage signing region is empty"
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

  if ! code="$(printf '%s\n' "${config[@]}" |
    evg_s3_curl --config - "${verb[@]}" --output "$output" --write-out '%{http_code}' "$url")"; then
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

# Creates the bucket. A 409 means it already exists, which is success for a
# re-run; verify's signed read then proves the existing bucket is usable with
# these credentials.
evg_s3_create_bucket() {
  local bucket="$1" endpoint="$2" code

  code="$(evg_s3_request PUT "$(evg_s3_bucket_url "$endpoint" "$bucket")" '' /dev/null)"
  case "$code" in
    2[0-9][0-9] | 409) ;;
    *) evg_s3_fail "creating bucket '$bucket' at $endpoint failed (HTTP $code)" ;;
  esac
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
  if ! code="$(evg_s3_curl "${verb[@]}" --silent --output /dev/null --write-out '%{http_code}' "$url")"; then
    evg_s3_fail "the anonymous request did not complete (curl reported the error above)"
  fi
  printf '%s' "$code"
}
