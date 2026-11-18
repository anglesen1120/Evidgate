// The run actions' request and answer types, taken from the generated SDK so a contract change
// reaches the create dialog through `tsc` instead of a hand-copied field list. Aliases, not a
// second declaration: the operation already names exactly the members it accepts.

import type { CreateRunRequest, RunCreated } from '@evidgate/sdk';

/** The body `POST /v1/projects/{p}/runs` accepts; only `manifest_id` is required. */
export type CreateRunInput = CreateRunRequest;

/** The 201 answer of a created run. */
export type CreatedRun = RunCreated;
