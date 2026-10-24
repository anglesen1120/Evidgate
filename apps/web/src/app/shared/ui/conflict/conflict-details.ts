import type { ErrorEnvelopeDetail } from '@evidgate/sdk';

import type { ApiError } from '../../../core/http/api-error';

export interface ConflictField {
  readonly id: string;
  readonly label: string;
  readonly local: string | null;
  readonly server: string | null;
}

/**
 * The SDK detail shape is { index?, path?, reason }, not a field-value snapshot.
 * Known concurrency metadata uses reason as the identifier (PolicyErrors.Stale,
 * CatalogErrors.Stale and AuthoringVersionWriter.OrderStale). Other reasons must
 * never be presented as server values. The owner must explicitly load a current
 * snapshot before it can supply a complete comparison to the dialog.
 */
export function conflictFromError(
  error: ApiError,
  draft: Readonly<Record<string, string>>,
  labels: Readonly<Record<string, string>>,
): { currentRevision: string | null; fields: ConflictField[] } {
  const revision: ErrorEnvelopeDetail | undefined =
    error.kind === 'precondition_failed'
      ? error.details.find(
          (detail) =>
            (detail.path === 'revision_id' ||
              detail.path === 'version' ||
              detail.path === 'order') &&
            detail.reason.trim().length > 0,
        )
      : undefined;

  return {
    currentRevision: revision?.reason ?? null,
    fields: Object.entries(draft).map(([id, local]) => ({
      id,
      label: Object.hasOwn(labels, id) ? (labels[id] ?? id) : id,
      local,
      server: null,
    })),
  };
}
