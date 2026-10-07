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
export EVIDGATE_PROJECT='my-project'

# Exchange the credential for a short-lived Evidgate CI token. The token is written
# only to the 0600 cache, never printed.
npx --yes --package @evidgate/cli@0.0.0 -- evidgate auth login

npx --yes --package @evidgate/cli@0.0.0 -- evidgate manifests upload --file evidgate-manifest.json

# Not yet available — these steps land with their owning tasks:
#   runs create (04.2.2-T2)
#   events send + runs finalize (04.2.3-T1/T2)
#   artifacts upload (04.2.4-T1)
