import { hlm } from '@evidgate/ui/utils';

import type { StatusTone } from './status-pill.component';

/** Soft corners and visible overflow keep wrapped labels clear of the badge's curved edge. */
export const BADGE_CLASSES =
  'h-auto min-h-[var(--eg-space-6)] max-w-full min-w-0 flex-wrap justify-start gap-[var(--eg-space-1)] whitespace-normal rounded-(--eg-radius-sm) overflow-visible px-[var(--eg-space-3)] py-[var(--eg-space-1)] font-sans font-semibold leading-(--eg-leading-sm) [overflow-wrap:anywhere]';

export const IDENTITY_BADGE_CLASSES = hlm(
  BADGE_CLASSES,
  'items-baseline rounded-sm bg-secondary font-mono font-normal',
);

export const BADGE_TONE_CLASSES = {
  success: 'bg-[var(--eg-success-bg)] text-[var(--eg-success-fg)]',
  info: 'bg-[var(--eg-info-bg)] text-[var(--eg-info-fg)]',
  warning: 'bg-[var(--eg-warning-bg)] text-[var(--eg-warning-fg)]',
  danger: 'bg-[var(--eg-danger-bg)] text-[var(--eg-danger-fg)]',
  neutral: 'bg-[var(--eg-neutral-bg)] text-[var(--eg-neutral-fg)]',
  pending:
    'bg-[var(--eg-pending-bg)] text-[var(--eg-pending-fg)] border-[var(--eg-pending-border)]',
} as const satisfies Readonly<Record<StatusTone | 'pending', string>>;
