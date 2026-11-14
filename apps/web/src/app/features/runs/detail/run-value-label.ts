import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';

export const RUN_DETAIL_NOT_PROVIDED = $localize`:@@runs.detail.not-provided:Not provided`;

export type RunValueKind = 'purpose' | 'attempt' | 'identity' | 'producer' | 'decision' | 'mode';

/** Only application-owned enum values are translated; caller-written identifiers stay verbatim. */
export function runValueLabel(kind: RunValueKind, value: string): string {
  switch (`${kind}:${value}`) {
    case 'purpose:regular':
      return $localize`:@@runs.value.purpose-regular:Regular run`;
    case 'purpose:replay':
      return $localize`:@@runs.value.purpose-replay:Replay`;
    case 'attempt:passed':
      return $localize`:@@runs.value.status-passed:Passed`;
    case 'attempt:failed':
      return $localize`:@@runs.value.status-failed:Failed`;
    case 'attempt:skipped':
      return $localize`:@@runs.value.status-skipped:Skipped`;
    case 'attempt:timed_out':
      return $localize`:@@runs.value.status-timed-out:Timed out`;
    case 'attempt:error':
      return $localize`:@@runs.value.status-error:Error`;
    case 'attempt:blocked':
      return $localize`:@@runs.value.status-blocked:Blocked`;
    case 'identity:bound':
      return $localize`:@@runs.value.identity-bound:Bound`;
    case 'identity:unbound':
      return $localize`:@@runs.value.identity-unbound:Unbound`;
    case 'producer:open':
      return $localize`:@@runs.value.producer-open:Open`;
    case 'producer:finalized':
      return $localize`:@@runs.value.producer-finalized:Finalized`;
    case 'decision:evaluated':
      return $localize`:@@runs.value.decision-evaluated:Evaluated`;
    case 'decision:pending':
      return $localize`:@@runs.value.decision-pending:Not evaluated`;
    case 'decision:unavailable':
      return $localize`:@@runs.value.decision-unavailable:Unavailable`;
    case 'mode:live':
      return $localize`:@@runs.value.decision-live:Live evaluation`;
    default:
      return escapeFormatChars(value);
  }
}
