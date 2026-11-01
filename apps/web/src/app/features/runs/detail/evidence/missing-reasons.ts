import type { MissingReason } from '../../../../shared/ui/annotations/missing-reason-list.component';
import { escapeFormatChars } from '../../list/ref-name-display';
import { MISSING_CODES, type MissingCode } from '../decision/decision-view-model';
import { evidenceHref, type EvidenceTarget } from './run-evidence-anchors';

/**
 * A relation the caller established from run data (manifest slot, attempt row) between one wire
 * literal and one identity. A reason code alone cannot name its target, so nothing links without
 * an entry here, and every entry is rendered rather than the first match.
 */
export type ConfirmedMissingTarget = {
  /** The wire literal this identity belongs to. */
  readonly code: string;
  readonly target: EvidenceTarget;
};

/** Which identity a literal may name, per the closed missing-code vocabulary. */
type TargetKind = EvidenceTarget['kind'] | 'none';

const TARGET_KIND: Record<MissingCode, TargetKind> = {
  missing_shard: 'producer',
  count_mismatch: 'producer',
  artifact_missing: 'none',
  build_mismatch: 'none',
  unknown_status: 'attempt',
  setup_error: 'attempt',
  stale_epoch: 'producer',
  missing_result: 'attempt',
};

/** Translated explanation; the wire literal itself is never replaced by it. */
function explanation(code: MissingCode): string {
  switch (code) {
    case 'missing_shard':
      return $localize`:@@runs.evidence.reason.missing-shard:Shard evidence is missing.`;
    case 'count_mismatch':
      return $localize`:@@runs.evidence.reason.count-mismatch:Collected and expected counts do not match.`;
    case 'artifact_missing':
      return $localize`:@@runs.evidence.reason.artifact-missing:A required artifact is missing.`;
    case 'build_mismatch':
      return $localize`:@@runs.evidence.reason.build-mismatch:The reported build does not match the manifest.`;
    case 'unknown_status':
      return $localize`:@@runs.evidence.reason.unknown-status:A reported result status is not recognized.`;
    case 'setup_error':
      return $localize`:@@runs.evidence.reason.setup-error:Test setup reported an error.`;
    case 'stale_epoch':
      return $localize`:@@runs.evidence.reason.stale-epoch:Evidence belongs to a stale producer epoch.`;
    case 'missing_result':
      return $localize`:@@runs.evidence.reason.missing-result:An expected test result is missing.`;
  }
}

function isMissingCode(code: string): code is MissingCode {
  return MISSING_CODES.some((known) => known === code);
}

function unknownCodeEntry(code: string): MissingReason {
  // An unknown literal is a closed-vocabulary violation, never a ninth reason: keep the exact
  // escaped value visible as the diagnostic instead of dropping it or guessing an explanation.
  const diagnostic = escapeFormatChars(code);
  return {
    code: diagnostic,
    text: $localize`:@@runs.evidence.invalid-body:Evidence response unavailable: ${diagnostic}:CODE:`,
  };
}

function targetEntry(target: EvidenceTarget): MissingReason {
  if (target.kind === 'producer') {
    const shard = escapeFormatChars(target.shard);
    return {
      code: shard,
      text: $localize`:@@runs.evidence.producer-link:View producer ${shard}:SHARD:`,
      href: evidenceHref(target),
    };
  }
  const executionKey = escapeFormatChars(target.executionKey);
  return {
    code: executionKey,
    text: $localize`:@@runs.evidence.attempt-link:View attempt ${target.attemptNo}:ATTEMPT: for ${executionKey}:EXECUTION_KEY:`,
    href: evidenceHref(target),
  };
}

/**
 * Presentation entries for `missing[]`: the exact literal, its localized explanation and one
 * confirmed target per entry. Server order and duplicates are preserved; a linkable literal with
 * no confirmed identity keeps its explanation plus the target-unavailable note and no href.
 */
export function missingReasonEntries(
  missing: readonly string[],
  confirmed: readonly ConfirmedMissingTarget[] = [],
): readonly MissingReason[] {
  const entries: MissingReason[] = [];
  for (const code of missing) {
    if (!isMissingCode(code)) {
      entries.push(unknownCodeEntry(code));
      continue;
    }
    const kind = TARGET_KIND[code];
    const targets =
      kind === 'none'
        ? []
        : confirmed
            .filter((relation) => relation.code === code && relation.target.kind === kind)
            .map((relation) => relation.target);
    entries.push({
      code: escapeFormatChars(code),
      text:
        targets.length > 0 || kind === 'none'
          ? explanation(code)
          : $localize`:@@runs.evidence.target-unavailable:${explanation(code)}:EXPLANATION: Target details unavailable`,
    });
    for (const target of targets) entries.push(targetEntry(target));
  }
  return entries;
}
