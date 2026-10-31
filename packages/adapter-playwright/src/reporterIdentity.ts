// Execution-identity bookkeeping: the resolver, its per-project memo, and the per-cause
// counts of tests whose identity is incomplete. The counts exist because `onEnd` must fail a
// run that emitted envelopes the server cannot attribute, and a CI log has to say which fix
// applies without the reader opening the spool.
import type { TestCase } from '@playwright/test/reporter';

import {
  createIdentityResolver,
  testKey,
  type Identity,
  type IdentityResolver,
  type VariantOutcome,
  type WarningPayload,
} from './identity.js';

export interface IdentityBookkeepingDeps {
  /** `EVIDGATE_TARGET_ENV`, validated in the constructor; undefined means project name. */
  readonly targetEnv: string | undefined;
  /** An injected resolver wins over the built-in mapping and gets no memo. */
  readonly injected: IdentityResolver | undefined;
  /** `EVIDGATE_ALLOW_UNMAPPED=1` turns the `onEnd` identity gate off. */
  readonly allowUnmapped: boolean;
  readonly warn: (warning: WarningPayload) => void;
}

export class IdentityBookkeeping {
  /** Per-project variant memo: normalising the same params twice is wasted work. */
  readonly variants = new WeakMap<object, VariantOutcome>();
  /** Projects already warned about an invalid target env, so the warning stays once. */
  readonly targetEnvWarned = new WeakSet<object>();
  private readonly seenTests = new Set<string>();
  private resolver: IdentityResolver | undefined;
  private unidentified = 0;
  private conflicted = 0;
  private noCaseId = 0;
  private noVariantHash = 0;
  private noTargetEnv = 0;

  constructor(private readonly deps: IdentityBookkeepingDeps) {}

  /** Installs the resolver built at `onBegin`, which carries the suite's sidecar index. */
  install(resolver: IdentityResolver): void {
    this.resolver = resolver;
  }

  /** The resolved identity: the injected resolver wins, else the built-in mapping. */
  identityOf(test: TestCase): Identity {
    if (this.deps.injected !== undefined) return this.deps.injected(test);
    this.resolver ??= createIdentityResolver({
      targetEnv: this.deps.targetEnv,
      warn: this.deps.warn,
      variantCache: this.variants,
      targetEnvWarned: this.targetEnvWarned,
    });
    return this.resolver(test);
  }

  /**
   * Counts each distinct test once. The per-cause counters are independent, so one test can
   * be counted in several of them — a conflicted test has no `case_id` and is counted in
   * both the conflict and the missing-`case_id` counter.
   */
  note(test: TestCase, identity: Identity): void {
    const key = testKey(test);
    if (this.seenTests.has(key)) return;
    this.seenTests.add(key);
    if (identity.identity_conflict === true) this.conflicted += 1;
    if (identity.case_id === undefined) this.noCaseId += 1;
    if (identity.variant_hash === undefined) this.noVariantHash += 1;
    if (identity.target_env === undefined) this.noTargetEnv += 1;
    if (
      identity.case_id === undefined ||
      identity.variant_hash === undefined ||
      identity.target_env === undefined
    ) {
      this.unidentified += 1;
    }
  }

  get unidentifiedCount(): number {
    return this.unidentified;
  }

  /** True when an incomplete identity must fail the run rather than pass silently. */
  get gateTripped(): boolean {
    return !this.deps.allowUnmapped && this.unidentified > 0;
  }

  /** The per-cause breakdown, so a CI log reader can tell which fix applies. */
  causes(): string {
    return (
      `${this.conflicted} conflicted, ${this.noCaseId} no case_id, ` +
      `${this.noVariantHash} no variant_hash, ${this.noTargetEnv} no target_env`
    );
  }
}
