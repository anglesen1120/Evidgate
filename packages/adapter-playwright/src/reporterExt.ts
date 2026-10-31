// Builds the `ext` of test and step envelopes. Everything Playwright supplies goes through
// the allow-list first, because Playwright's own objects carry absolute paths, presigned
// URLs and secret-looking keys that must never reach the spool.
import type { TestCase, TestResult, TestStep } from '@playwright/test/reporter';

import type { AttachmentRecord } from './attach.js';
import { EXT_SCHEMA, type ExtPayload } from './envelope.js';
import { filterExt } from './extFilter.js';
import type { Identity } from './identity.js';
import { isUserAuthoredStepCategory } from './reporterSteps.js';
import type { StatusMapping } from './status.js';

/** The members Playwright objects expose; the filter decides which may become `ext`. */
function ownMembers(value: object): Record<string, unknown> {
  return { ...value };
}

export class TestExtBuilder {
  private dropped = 0;

  constructor(private readonly engineVersion: string) {}

  /** How many secret-looking `ext` keys the filter dropped for this run. */
  get secretKeysDropped(): number {
    return this.dropped;
  }

  /**
   * The `ext` of a test envelope. The status keys are set explicitly — `undefined` (absent)
   * on `test.started` — after the Playwright members, so a same-named member of `TestResult`
   * can never reach `ext` in their place.
   */
  testExt(
    test: TestCase,
    result: TestResult,
    identity: Identity,
    mapping?: StatusMapping,
    records?: readonly AttachmentRecord[],
  ): ExtPayload {
    return this.applyFilter({
      ...ownMembers(test),
      ...ownMembers(result),
      schema: EXT_SCHEMA,
      file: test.location.file,
      project: test.parent.project()?.name,
      engine: this.engineVersion,
      case_id: identity.case_id,
      variant_hash: identity.variant_hash,
      target_env: identity.target_env,
      identity_conflict: identity.identity_conflict,
      normalized: mapping?.normalized,
      skip_reason: mapping?.skip_reason,
      xfail: mapping?.xfail,
      error_summary: mapping?.error_summary,
      // Only our own records: base names, kinds, outcomes and artifact ids, so no path, no
      // URL and no token can leave the process through this member.
      attachments: records,
    });
  }

  stepExt(step: TestStep, identity: Identity): ExtPayload {
    const candidate: Record<string, unknown> = {
      ...ownMembers(step),
      schema: EXT_SCHEMA,
      case_id: identity.case_id,
      variant_hash: identity.variant_hash,
      target_env: identity.target_env,
      identity_conflict: identity.identity_conflict,
    };
    // The title is forwarded only for user-authored categories; `category` and `location`
    // still identify a Playwright-generated step.
    if (!isUserAuthoredStepCategory(step.category)) delete candidate.title;
    // A step's own attachments are raw Playwright `{name, contentType, path, body}` — an
    // absolute path plus the bytes — so they are never forwarded.
    delete candidate.attachments;
    return this.applyFilter(candidate);
  }

  private applyFilter(candidate: Record<string, unknown>): ExtPayload {
    const result = filterExt(candidate);
    this.dropped += result.secretDropped.length;
    return { schema: EXT_SCHEMA, ...result.ext };
  }
}
