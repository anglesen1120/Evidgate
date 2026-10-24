import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** Presentation data, not a wire response: href exists only when the caller has a real target. */
export type MissingReason = {
  readonly code: string;
  readonly text: string;
  readonly href?: string;
};

@Component({
  selector: 'app-missing-reason-list',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './missing-reason-list.component.css',
  template: `
    @if (heading(); as title) {
      <h3>{{ title }}</h3>
    }
    @if (reasons().length) {
      <ul class="reason-list" [attr.aria-label]="heading() || listLabel">
        @for (reason of safeReasons(); track $index) {
          <li class="reason">
            <code class="reason-code">{{ reason.code }}</code>
            @if (reason.href) {
              <a class="reason-link" [href]="reason.href" [attr.rel]="reason.rel">{{
                reason.text
              }}</a>
            } @else {
              <span class="reason-text">{{ reason.text }}</span>
            }
          </li>
        }
      </ul>
    } @else {
      <p class="reason-empty" i18n="@@shared.missing-reason-list.empty">
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
