import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HlmAlert, HlmAlertDescription, HlmAlertTitle } from '@evidgate/ui/alert';
import { NarrowMutationPolicy } from './narrow-mutation-policy';

@Component({
  selector: 'app-desktop-required-notice',
  imports: [HlmAlert, HlmAlertDescription, HlmAlertTitle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    @if (policy.narrow()) {
      <section hlmAlert role="note" class="min-w-0 wrap-anywhere">
        <h2 hlmAlertTitle>{{ title }}</h2>
        <p hlmAlertDescription class="m-0">{{ description }}</p>
      </section>
    }
  `,
})
export class DesktopRequiredNoticeComponent {
  protected readonly policy = inject(NarrowMutationPolicy);
  protected readonly title = $localize`:@@shared.narrow.desktop-required.title:Desktop required`;
  protected readonly description = $localize`:@@shared.narrow.desktop-required.description:Use a wider screen to make changes. You can still inspect information, copy values and navigate.`;
}
