import type { ApiError } from '../../../core/http/api-error';

// The server writes these as top-level JSON pointers (`/version`, `/order`); the bare
// names stay accepted. Nested pointers such as `/items/0/version` locate a body
// member, not the resource's concurrency token, so they never match.
const metadataNames: Readonly<Record<string, true>> = {
  revision_id: true,
  version: true,
  order: true,
};

function metadataName(path: string): string | null {
  const name = path.startsWith('/') ? path.slice(1) : path;
  return Object.hasOwn(metadataNames, name) ? name : null;
}

export interface ConflictField {
  readonly id: string;
  readonly label: string;
  readonly local: string | null;
  readonly server: string | null;
}

/**
 * The SDK detail shape is { index?, path?, reason }, not a field-value snapshot.
 * Known concurrency metadata uses reason as the identifier (PolicyErrors.Stale,
 * CatalogErrors.Stale, AuthoringRequestScope.Stale and AuthoringVersionWriter.OrderStale),
 * so a draft field with that id receives it as the server value. Other reasons must
 * never be presented as server values: a complete comparison still needs a reload.
 */
export function conflictFromError(
  error: ApiError,
  draft: Readonly<Record<string, string>>,
  labels: Readonly<Record<string, string>>,
): { currentRevision: string | null; fields: ConflictField[] } {
  const metadata = new Map<string, string>();
  let currentRevision: string | null = null;
  if (error.kind === 'precondition_failed') {
    for (const detail of error.details) {
      if (detail.path === undefined || detail.reason.trim().length === 0) continue;
      const name = metadataName(detail.path);
      if (name === null || metadata.has(name)) continue;
      metadata.set(name, detail.reason);
      currentRevision ??= detail.reason;
    }
  }

  return {
    currentRevision,
    fields: Object.entries(draft).map(([id, local]) => ({
      id,
      label: Object.hasOwn(labels, id) ? (labels[id] ?? id) : id,
      local,
      server: metadata.get(id) ?? null,
    })),
  };
}
