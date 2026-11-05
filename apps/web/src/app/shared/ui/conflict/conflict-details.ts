import type { ErrorEnvelopeDetail } from '@evidgate/sdk';

import type { ApiError } from '../../../core/http/api-error';

// The server writes these as top-level JSON pointers (`/version`, `/order`); the bare
// names stay accepted. Nested pointers such as `/items/0/version` locate a body
// member, not the resource's concurrency token, so they never match.
const metadataPaths: ReadonlySet<string> = new Set(
  ['revision_id', 'version', 'order'].flatMap((name) => [name, `/${name}`]),
);

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
            detail.path !== undefined &&
            metadataPaths.has(detail.path) &&
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
