import { ChangeDetectionStrategy, Component, input } from '@angular/core';

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
        @for (reason of reasons(); track $index) {
          <li class="reason">
            <code class="reason-code">{{ reason.code }}</code>
            @if (reason.href) {
              <a class="reason-link" [href]="reason.href">{{ reason.text }}</a>
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
}
