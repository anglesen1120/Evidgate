// `evidgate manifests build` — maps a Playwright list report onto one suite revision's execution
// plan and writes the manifest `manifests upload` accepts. The flags and the report are validated
// before `getToken()`, so a bad flag or a malformed report makes no request; mapping the report
// onto the plan happens after it is read, so an unmappable report is refused after the fetch.
import { lstat, open, unlink, type FileHandle } from 'node:fs/promises';
import { basename, dirname, resolve } from 'node:path';

import { getSuiteRevisionExecutionPlan, type Client } from '@evidgate/sdk';

import type { ExchangeDeps } from '../auth/exchange.js';
import { getToken } from '../auth/getToken.js';
import { deleteCachedToken } from '../auth/tokenCache.js';
import { errnoName } from '../boundedFile.js';
import { callApi } from '../callApi.js';
import { createApiClient } from '../client.js';
import type { ResolvedConfig } from '../config.js';
import { CliError, escapeControlCharacters } from '../errors.js';
import { planPageLimit, readExecutionPlan } from '../executionPlanRead.js';
import { ExitCode } from '../exitCodes.js';
import { buildManifest, type PlannedExecution } from '../manifestEmit.js';
import {
  assertDistinctTests,
  assertMembersExercised,
  describeTest,
  mapTestsToMembers,
  memberKey,
  type ExecutionPlan,
} from '../manifestMapping.js';
import { loadPackagedContractSchemas } from '../packagedSchemas.js';
import { parsePlaywrightListWithParams, readPlaywrightList } from '../playwrightList.js';
import { serverErrorFromResponse, transportError } from '../serverError.js';
import { manifestParams, normalizeVariant, type VariantResult } from '../variantHash.js';

import { requiredFlag, uuidPattern } from './eventsSendArgs.js';

import type { ListedTest } from '../playwrightList.js';
import type { CommandDefinition } from './index.js';

export const manifestsBuildCommand: CommandDefinition = {
  name: 'manifests build',
  summary: 'build a run manifest from a suite revision and a Playwright list report',
  requireToken: true,
  options: {
    'suite-revision': { type: 'string' },
    'playwright-list': { type: 'string' },
    out: { type: 'string' },
  },
  run: async ({ config, io, values, deps }) => {
    const requested = requiredFlag(values, 'suite-revision');
    if (!uuidPattern.test(requested)) {
      throw new CliError('--suite-revision must be a UUID', { code: 'usage' });
    }
    // The route and the report both name a revision in canonical lowercase; one normalisation
    // here keeps the request, the plan comparison and the written manifest on the same value.
    const revision = requested.toLowerCase();
    const listPath = requiredFlag(values, 'playwright-list');
    const out = requiredFlag(values, 'out');
    // Report first, token second: an unreadable or malformed report must not reach the network.
    // The report's own directory is the containment root, and only the entry name is handed to
    // the reader: it resolves that name under the root's realpath and refuses a report that
    // resolves outside it, so a symlink whose target stays inside the directory is followed.
    const resolvedList = resolve(listPath);
    const tests = parsePlaywrightListWithParams(
      await readPlaywrightList(basename(resolvedList), dirname(resolvedList)),
      (params) => {
        const variant = normalizeVariant(params);
        return variant.ok ? variant.hash : undefined;
      },
    );
    assertDistinctTests(tests);

    const { token } = await getToken(config, deps);
    const client = createApiClient(config, token, deps.fetch ?? fetch);
    const members = await readExecutionPlan(revision, (cursor) =>
      readPage(client, revision, cursor, config, deps),
    );
    const plan: ExecutionPlan = { suite_revision_id: revision, members };
    const mapping = mapTestsToMembers(tests, plan);
    assertMembersExercised(plan, mapping.entries);

    const byIdentity = new Map<string, ListedTest>();
    for (const entry of mapping.entries) {
      const caseId = entry.test.caseId;
      if (caseId !== undefined) {
        byIdentity.set(memberKey(caseId, entry.test.variantHash), entry.test);
      }
    }
    const executions = planExecutions(members, byIdentity);
    const text = buildManifest(revision, executions);
    assertSchemaValid(text);
    await writeOutput(out, text);

    const summary = { manifest: out, executions: executions.length, suite_revision_id: revision };
    if (config.json) {
      io.stdout(`${JSON.stringify(summary)}\n`);
    } else {
      io.stdout(
        `manifest=${escapeControlCharacters(out)} executions=${executions.length} suite_revision_id=${revision}\n`,
      );
    }
    return ExitCode.Ok;
  },
};

/** One page of the plan, with every non-2xx answer mapped before the reader validates a body. */
async function readPage(
  client: Client,
  revision: string,
  cursor: string | undefined,
  config: ResolvedConfig,
  deps: ExchangeDeps,
): Promise<unknown> {
  const result = await callApi(
    (signal) =>
      getSuiteRevisionExecutionPlan({
        client,
        path: { r: revision },
        query: { limit: planPageLimit, ...(cursor === undefined ? {} : { cursor }) },
        signal,
      }),
    { policy: 'replay-safe', deps },
  );
  const response = result.response;
  if (response === undefined) {
    throw transportError(result.error);
  }
  if (!response.ok) {
    if (response.status === 401) {
      // Best-effort: the server's answer must surface, but a refused bearer must not be reused.
      await deleteCachedToken(config).catch(() => undefined);
    }
    throw pageRefusal(result.error, response);
  }
  return result.data;
}

/**
 * A non-2xx answer. A 403 additionally says why: `suite:plan_read` is newer than the tokens most
 * CI jobs already cached, so a token minted before it existed is refused until it is re-minted.
 */
function pageRefusal(error: unknown, response: Response): CliError {
  const refusal = serverErrorFromResponse(error, response, {
    code: 'request_failed',
    message: `the execution plan read failed (HTTP ${response.status})`,
  });
  if (response.status !== 403) {
    return refusal;
  }
  return new CliError(refusal.message, {
    code: refusal.code,
    hint: 'this token lacks suite:plan_read; re-mint it (tokens minted before that permission existed lack it)',
    ...(refusal.requestId === undefined ? {} : { requestId: refusal.requestId }),
    ...(refusal.details === undefined ? {} : { details: refusal.details }),
  });
}

/**
 * One execution per plan member, in plan order. The mapping already proved every member has
 * exactly one test; the variant is normalised again because the manifest carries the parameters
 * the server will hash, not the digest it was served. Members that share a variant hash share
 * its parameters (the hash is over them), so each distinct variant is normalised once.
 */
function planExecutions(
  members: readonly { case_id: string; variant_hash: string; case_revision_id: string }[],
  byIdentity: ReadonlyMap<string, ListedTest>,
): PlannedExecution[] {
  const executions: PlannedExecution[] = [];
  const variants = new Map<string, VariantResult>();
  for (const member of members) {
    const test = byIdentity.get(memberKey(member.case_id, member.variant_hash));
    if (test === undefined) {
      throw new CliError('the execution plan changed while it was read', {
        code: 'manifest_plan_invalid',
      });
    }
    const known = variants.get(member.variant_hash);
    const variant = known ?? normalizeVariant(test.variantParams);
    if (!variant.ok || variant.hash !== member.variant_hash) {
      throw new CliError(`the variant of ${describeTest(test)} is not the one the plan lists`, {
        code: 'manifest_plan_invalid',
      });
    }
    variants.set(member.variant_hash, variant);
    const params = manifestParams(variant.params);
    if (params === undefined) {
      throw new CliError(
        `the variant of ${describeTest(test)} uses parameters a manifest cannot carry`,
        {
          code: 'manifest_variant_unrepresentable',
          hint: 'a manifest variant holds lowercase keys and string values only; use string params',
          details: [describeTest(test)],
        },
      );
    }
    executions.push({ caseRevisionId: member.case_revision_id, params });
  }
  return executions;
}

/** The built document must pass the same packaged `manifest.v1` validator the upload path uses. */
function assertSchemaValid(text: string): void {
  const result = loadPackagedContractSchemas().validate('manifest.v1', text);
  if (result.errors.length === 0) {
    return;
  }
  throw new CliError('the built manifest failed manifest.v1 validation', {
    code: 'manifest_invalid',
    details: result.errors.map((error) => `${error.path} ${error.reason}`),
  });
}

/**
 * Creates the file exclusively: an existing path (a regular file or a symlink, since an exclusive
 * create never follows one) is refused rather than replaced. The mode is owner-only. A failure
 * before the create removes nothing. A write or close failure removes only the entry this run
 * created, identified by the device and inode recorded at creation: an entry swapped in its place
 * is kept, and a symlink's target is never touched. `write` and `open` are the seams a spec uses
 * to fail mid-write or before the create.
 */
export async function writeOutput(
  path: string,
  text: string,
  write: OutputWrite = writeThroughHandle,
  open: OutputOpen = openOutput,
): Promise<void> {
  let handle: FileHandle;
  try {
    handle = await open(path);
  } catch (error) {
    throw outputOpenError(error);
  }
  let created: OutputIdentity | null = null;
  try {
    created = identityOf(await handle.stat());
    await write(handle, text);
    await handle.close();
  } catch (error) {
    const errno = errnoName(error);
    // The descriptor stays open during the identity check so our inode cannot be recycled.
    await removeOwnEntry(path, created);
    await handle.close().catch(() => undefined);
    throw new CliError(`the output file could not be written (${errno})`, {
      code: 'manifest_output_unwritable',
    });
  }
}

type OutputWrite = (handle: FileHandle, text: string) => Promise<void>;
type OutputOpen = (path: string) => Promise<FileHandle>;
type OutputIdentity = { dev: number; ino: number };

/** The exclusive create itself; the mode keeps the manifest owner-only. */
const openOutput: OutputOpen = (path) => open(path, 'wx', 0o600);

const writeThroughHandle: OutputWrite = (handle, text) => handle.writeFile(text);

function identityOf(stats: { dev: number; ino: number }): OutputIdentity {
  return { dev: stats.dev, ino: stats.ino };
}

/** A create that fails never had a file of ours: `EEXIST` names the other entry, nothing is removed. */
function outputOpenError(error: unknown): CliError {
  const errno = errnoName(error);
  if (errno === 'EEXIST') {
    return new CliError('the output file already exists', {
      code: 'manifest_output_exists',
      hint: 'choose another --out path; an existing manifest is never overwritten',
    });
  }
  return new CliError(`the output file could not be written (${errno})`, {
    code: 'manifest_output_unwritable',
  });
}

/**
 * Removes the entry only while it still carries the inode this run created. An uninspectable or
 * replaced entry is left alone: cleanup never deletes someone else's file, and its own failure
 * never replaces the write error. Node has no unlink-by-identity call, so a swap between the
 * check and the unlink remains a residual race.
 */
async function removeOwnEntry(path: string, created: OutputIdentity | null): Promise<void> {
  if (created === null) return;
  const stats = await lstat(path).catch(() => null);
  if (stats === null || stats.dev !== created.dev || stats.ino !== created.ino) return;
  await unlink(path).catch(() => undefined);
}
