#!/bin/sh
# Evidgate quickstart — generic CI recipe (rendered by `evidgate quickstart`).
#
# Set EVIDGATE_API_URL in your CI's non-secret variables. Nothing else is stored:
# the CLI authenticates with a short-lived credential the CI injects.
#
# Credential, by name only — never put a value in this file:
#   EVIDGATE_OIDC_TOKEN    a short-lived OIDC JWT the project binding accepts, or
#   EVIDGATE_INGEST_TOKEN  a project-scoped, revocable, rotatable ingest token
#                          created with an expiry (02.4.2), injected by the CI's
#                          masked secret store. Never committed, never echoed.
set -eu

: "${EVIDGATE_API_URL:?EVIDGATE_API_URL must be set in the CI variables}"
export EVIDGATE_API_URL
export EVIDGATE_PROJECT='my-project'

# Exchange the credential for a short-lived Evidgate CI token. The token is written
# only to the 0600 cache, never printed.
npx --yes --package @evidgate/cli@0.0.0 -- evidgate auth login

# Upload the manifest and keep its ids in shell variables for the run step.
out=$(npx --yes --package @evidgate/cli@0.0.0 -- evidgate manifests upload --file evidgate-manifest.json --json)
manifest_id=$(node -p 'JSON.parse(process.argv[1]).manifest_id' "$out")
manifest_hash=$(node -p 'JSON.parse(process.argv[1]).manifest_hash' "$out")

# Create the run for that manifest and keep its id for the steps below. Add --ci-ref
# refs/heads/<branch> (or refs/tags/<tag>) here to record where the run came from.
out=$(npx --yes --package @evidgate/cli@0.0.0 -- evidgate runs create --manifest-id "$manifest_id" --manifest-hash "$manifest_hash" --json)
run_id=$(node -p 'JSON.parse(process.argv[1]).run_id' "$out")

# The reporter writes one envelope per result into this directory, and the two commands
# below read it. The reporter ships with the Playwright adapter (04.3.1-T1, 04.3.3-T1):
# run your suite through it before this point. Any absolute path works.
EVIDGATE_SPOOL_DIR="${EVIDGATE_SPOOL_DIR:-$PWD/.evidgate-spool}"
export EVIDGATE_SPOOL_DIR

# Send every spooled envelope for the run: batches of at most 500 events / 1 MiB, each
# with an Idempotency-Key, retried inside EVIDGATE_SEND_DEADLINE. It never deletes the
# spool, so a re-run after a failure resumes from the first unacked sequence.
npx --yes --package @evidgate/cli@0.0.0 -- evidgate events send --run "$run_id"

# Declare this producer done: last_sequence and count come from the spool's ack cursor.
# A 403 producer_slot_taken or 422 unknown_producer exits 3 with the server code; the
# command is idempotent, so re-running it is safe. One producer per shard: shard-<index>
# with --shard its 1-based manifest index.
npx --yes --package @evidgate/cli@0.0.0 -- evidgate runs finalize --run "$run_id" --producer shard-1 --shard 1 --manifest-hash "$manifest_hash"

# Not yet available — this step lands with its owning task:
#   artifacts upload (04.2.4-T1)
