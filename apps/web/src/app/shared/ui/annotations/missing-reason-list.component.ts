import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { hlm } from '@evidgate/ui/utils';

import { BADGE_CLASSES } from '../chips/badge-classes';

/** Presentation data, not a wire response: href exists only when the caller has a real target. */
export type MissingReason = {
  readonly code: string;
  readonly text: string;
  readonly href?: string;
};

@Component({
  selector: 'app-missing-reason-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmBadge, HlmButton],
  host: { class: 'block min-w-0 font-sans text-sm text-foreground wrap-anywhere' },
  template: `
    @if (heading(); as title) {
      <h3 class="mt-0 mb-(--eg-space-3) text-sm font-semibold">{{ title }}</h3>
    }
    @if (reasons().length) {
      <ul
        class="reason-list m-0 flex flex-col gap-(--eg-space-3) ps-(--eg-space-5)"
        [attr.aria-label]="heading() || listLabel"
      >
        @for (reason of safeReasons(); track $index) {
          <li class="reason min-w-0 whitespace-normal wrap-anywhere">
            <code
              hlmBadge
              [variant]="'outline'"
              class="reason-code me-(--eg-space-3)"
              [class]="reasonBadgeClasses"
              >{{ reason.code }}</code
            >
            @if (reason.href) {
              <a
                hlmBtn
                [variant]="'link'"
                class="reason-link h-auto min-h-(--eg-control-sm) min-w-(--eg-control-sm) max-w-full p-0 text-start text-foreground whitespace-normal underline wrap-anywhere pointer-coarse:min-h-(--eg-control-touch) pointer-coarse:min-w-(--eg-control-touch)"
                [href]="reason.href"
                [attr.rel]="reason.rel"
                >{{ reason.text }}</a
              >
            } @else {
              <span class="reason-text">{{ reason.text }}</span>
            }
          </li>
        }
      </ul>
    } @else {
      <p class="reason-empty m-0 text-muted-foreground" i18n="@@shared.missing-reason-list.empty">
        No missing evidence reasons reported
      </p>
    }
  `,
})
export class MissingReasonList {
  readonly reasons = input<readonly MissingReason[]>([]);
  /** Caller-supplied headings must already be localized. */
  readonly heading = input<string>();
  protected readonly listLabel = $localize`:@@shared.missing-reason-list.label:Missing evidence reasons`;
  protected readonly reasonBadgeClasses = hlm(BADGE_CLASSES, 'font-mono text-xs font-normal');

  protected readonly safeReasons = computed(() =>
    this.reasons().map((reason) => {
      const href = this.safeHref(reason.href);
      return {
        ...reason,
        href,
        rel: href && /^https?:/i.test(href) ? 'noopener noreferrer' : null,
      };
    }),
  );

  private safeHref(href: string | undefined): string | undefined {
    // Reject characters browsers normalize before navigation, including /\host authority paths.
    if (!href || /[\s\p{Cc}\\]/u.test(href)) {
      return undefined;
    }
    if ((href.startsWith('/') && !href.startsWith('//')) || href.startsWith('#')) {
      return href;
    }
    if (!/^https?:\/\//i.test(href)) {
      return undefined;
    }
    try {
      const url = new URL(href);
      return url.hostname ? href : undefined;
    } catch {
      return undefined;
    }
  }
}
