import type { CaseDto } from '@evidgate/sdk';
import type { ApiError } from '../../core/http/api-error';
import type { CaseListDataService } from '../../core/data/case-list-data.service';
import { createIntent, type Intent } from '../../core/http/intent';
import { typedFailure } from './case-list-paging';

/** One row's owner change: in flight, or the server's typed refusal of the last attempt. */
export interface CaseOwnerOp {
  readonly pending: boolean;
  readonly failure: ApiError | null;
}
const NO_OWNER_OPS: Readonly<Record<string, CaseOwnerOp>> = {};

/** The PATCH is refused before it is sent: the caller lacks `case:write`. */
const WRITE_FORBIDDEN: ApiError = {
  kind: 'forbidden',
  status: 403,
  code: 'case_write_forbidden',
  message: $localize`:@@catalog.owner-forbidden:Your role cannot change a case owner.`,
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
/** `UpdateCaseRequest.owner_membership_id` is a string: the contract has no unassign. */
const UNASSIGN_UNSUPPORTED: ApiError = {
  kind: 'validation',
  status: 0,
  code: 'owner_required',
  message: $localize`:@@catalog.owner-required:A case owner is required; the contract cannot clear it.`,
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
/**
 * The PATCH left no confirmed answer (transport, deadline or an unmapped outcome). The row keeps
 * the optimistic owner, so the copy must send the user to the server state rather than claim a
 * rollback the client cannot prove.
 */
const OWNER_RELOAD_REQUIRED: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'owner_outcome_unknown',
  message: $localize`:@@catalog.owner-reload-required:Owner change was not confirmed. Reload the list to see the stored owner.`,
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};
/** Refusals the server attaches to a rejected write: only these roll the row back. */
const OWNER_REFUSALS: ReadonlySet<number> = new Set([403, 409, 412, 422]);

export interface CaseOwnerHost {
  readonly rows: () => readonly CaseDto[];
  readonly ownerOps: () => Readonly<Record<string, CaseOwnerOp>>;
  /** The paging generation: a write that captured an older one must not write state. */
  readonly epoch: () => number;
  readonly canWrite: () => boolean;
  readonly patchOwnerOps: (ops: Readonly<Record<string, CaseOwnerOp>>) => void;
  readonly replaceRow: (caseId: string, row: CaseDto) => void;
}

export interface CaseOwnerActions {
  assign(caseId: string, membershipId: string | null): Promise<void>;
  /** Re-reads one case: the accumulated pages stay, and the next `If-Match` is fresh. */
  reload(caseId: string): Promise<void>;
  /** Forgets the snapshots, etags and intents of the pages that are gone. */
  reset(): void;
}

export function createCaseOwner(host: CaseOwnerHost, data: CaseListDataService): CaseOwnerActions {
  const snapshots = new Map<string, CaseDto>();
  /** The `ETag` a single-case read issued: the next `If-Match` must quote exactly that. */
  const etags = new Map<string, string>();
  /** One intent per row action: a retry of the same target replays its key. */
  const intents = new Map<string, { membershipId: string; intent: Intent }>();

  const op = (caseId: string, next: CaseOwnerOp): void => {
    host.patchOwnerOps({ ...host.ownerOps(), [caseId]: next });
  };
  const intentFor = (caseId: string, membershipId: string): Intent => {
    const current = intents.get(caseId);
    if (current?.membershipId === membershipId) return current.intent;
    const intent = createIntent();
    intents.set(caseId, { membershipId, intent });
    return intent;
  };
  const refuse = (caseId: string, failure: ApiError): void => {
    snapshots.delete(caseId);
    op(caseId, { pending: false, failure });
  };
  const confirmed = (caseId: string, row: CaseDto): void => {
    snapshots.delete(caseId);
    etags.delete(caseId);
    intents.delete(caseId);
    host.replaceRow(caseId, row);
    op(caseId, { pending: false, failure: null });
  };
  const assign = async (caseId: string, membershipId: string | null): Promise<void> => {
    if (!host.canWrite()) {
      op(caseId, { pending: false, failure: WRITE_FORBIDDEN });
      return;
    }
    if (membershipId === null) {
      op(caseId, { pending: false, failure: UNASSIGN_UNSUPPORTED });
      return;
    }
    if (host.ownerOps()[caseId]?.pending) return;
    const row = host.rows().find((candidate) => candidate.id === caseId);
    if (row === undefined) return;
    const epoch = host.epoch();
    const intent = intentFor(caseId, membershipId);
    const etag = etags.get(caseId) ?? `"${row.version}"`;
    snapshots.set(caseId, row);
    op(caseId, { pending: true, failure: null });
    host.replaceRow(caseId, { ...row, owner_membership_id: membershipId });
    try {
      const stored = await data.update(
        caseId,
        { owner_membership_id: membershipId },
        new AbortController().signal,
        etag,
        intent,
      );
      if (epoch !== host.epoch()) return;
      confirmed(caseId, stored);
    } catch (error) {
      if (epoch !== host.epoch()) return;
      const failure = typedFailure(error);
      if (!OWNER_REFUSALS.has(failure.status)) {
        refuse(caseId, OWNER_RELOAD_REQUIRED);
        return;
      }
      host.replaceRow(caseId, snapshots.get(caseId) ?? row);
      intents.delete(caseId);
      refuse(caseId, failure);
    }
  };
  const reload = async (caseId: string): Promise<void> => {
    const epoch = host.epoch();
    if (!host.rows().some((candidate) => candidate.id === caseId)) return;
    op(caseId, { pending: true, failure: null });
    try {
      const read = await data.get(caseId, new AbortController().signal);
      if (epoch !== host.epoch()) return;
      etags.set(caseId, read.etag);
      intents.delete(caseId);
      host.replaceRow(caseId, read.row);
      op(caseId, { pending: false, failure: null });
    } catch (error) {
      if (epoch !== host.epoch()) return;
      op(caseId, { pending: false, failure: typedFailure(error) });
    }
  };
  const reset = (): void => {
    snapshots.clear();
    etags.clear();
    intents.clear();
    host.patchOwnerOps(NO_OWNER_OPS);
  };
  return { assign, reload, reset };
}
