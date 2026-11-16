import { hlm } from '@evidgate/ui/utils';

import type { StatusTone } from './status-pill.component';

/** Let the line box grow with enlarged text; only the inner label owns horizontal elision. */
export const BADGE_CLASSES =
  'h-auto min-h-[var(--eg-space-7)] max-w-[min(100%,24ch)] min-w-0 flex-nowrap justify-start gap-[var(--eg-space-1)] whitespace-nowrap rounded-(--eg-radius-sm) overflow-visible px-[var(--eg-space-3)] py-[var(--eg-space-1)] font-sans font-semibold text-[length:var(--eg-text-xs)] leading-(--eg-leading-xs)';

export const IDENTITY_BADGE_CLASSES = hlm(
  BADGE_CLASSES,
  'rounded-(--eg-radius-sm) bg-secondary font-mono font-normal',
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

export const BADGE_ICON_NAMES = {
  success: 'lucideCircleCheck',
  info: 'lucideActivity',
  warning: 'lucideTriangleAlert',
  danger: 'lucideOctagonX',
  neutral: 'lucideCircle',
  pending: 'lucideClock',
} as const;
