import type { ErrorEnvelopeDetail } from '@evidgate/sdk';
import { sanitizeLabel } from './default-policy';

type FieldContext = 'project' | 'connection' | 'collection';

const FIELD_LABELS: Readonly<Record<string, string>> = {
  slug: $localize`:@@onboarding.create.slug:Project slug`,
  repository_id: $localize`:@@onboarding.connect.repo-id:GitHub numeric repository ID`,
  repository_full_name: $localize`:@@onboarding.connect.repo-name:Repository (owner/repo)`,
  allowed_ref_patterns: $localize`:@@onboarding.connect.refs:Allowed Git ref patterns`,
  allowed_workflow_refs: $localize`:@@onboarding.connect.workflows:Allowed workflow refs`,
  project_id: $localize`:@@onboarding.enable.project:Project`,
  policy_revision_id: $localize`:@@onboarding.enable.revision:Policy revision`,
  mode: $localize`:@@onboarding.enable.mode:Mode`,
  hash: $localize`:@@onboarding.enable.hash:Revision hash`,
  late_evidence_window_minutes: $localize`:@@onboarding.enable.late-window:Late-evidence window`,
  required_suites: $localize`:@@onboarding.enable.required-suites:Required suites`,
  required_cases: $localize`:@@onboarding.enable.required-cases:Required cases`,
  gated_branch: $localize`:@@onboarding.enable.gated-branch:Default branch gated`,
};

/** One sanitized server detail plus a localized message for inline and summary errors. */
export interface FieldDetail {
  /** The path without its JSON-pointer prefix or index: `/repository_id[0]` → `repository_id`. */
  readonly field: string;
  /** Sanitized diagnostics stay available without exposing a raw pointer in the UI. */
  readonly path: string;
  readonly reason: string;
  readonly message: string;
}

/** Resolve server paths to product labels without translating the server's diagnostic reason. */
export function fieldDetails(
  details: readonly ErrorEnvelopeDetail[],
  context: FieldContext,
): readonly FieldDetail[] {
  return details.map((detail) => {
    const path = sanitizeLabel(detail.path ?? '');
    const field =
      path
        .replace(/^\/+/, '')
        .split(/[[\]/.]/)
        .shift()
        ?.trim() ?? '';
    const nameLabel =
      context === 'project'
        ? $localize`:@@onboarding.create.name:Project name`
        : $localize`:@@onboarding.connect.token-name:Token name`;
    const knownLabel = Object.hasOwn(FIELD_LABELS, field) ? FIELD_LABELS[field] : undefined;
    const label =
      (field === 'name' && context !== 'collection' ? nameLabel : knownLabel) ??
      $localize`:@@onboarding.fields.generic:Submitted value`;
    const reason = sanitizeLabel(detail.reason);
    return {
      field,
      path,
      reason,
      message: $localize`:@@onboarding.fields.reason:${label}:FIELD_LABEL:: ${reason}:SERVER_REASON:`,
    };
  });
}

/** The first localized field message; the summary retains every server reason. */
export function reasonFor(details: readonly FieldDetail[], field: string): string | null {
  return details.find((detail) => detail.field === field)?.message ?? null;
}

/** Include only description IDs that resolve in the current rendered state. */
export function describedBy(...ids: readonly (string | null | undefined)[]): string | null {
  const present = ids.filter((id): id is string => typeof id === 'string' && id !== '');
  return present.length === 0 ? null : present.join(' ');
}
