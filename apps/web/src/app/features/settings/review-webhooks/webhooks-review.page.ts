import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import {
  SettingsPageCountsComponent,
  type SettingsPageCount,
} from '../settings-page-counts.component';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmTableImports } from '@evidgate/ui/table';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import {
  EmptyPatternComponent,
  InlineFeedbackComponent,
  LoadingPatternComponent,
} from '../../../shared/ui/states';
import { HlmSelectImports } from '@evidgate/ui/select';
import { WebhookIntentPreview } from './webhook-intent-preview';
import { DesktopMutationDirective } from '../../../shared/ui/narrow/desktop-mutation.directive';
import { webhooksFixture, type WebhooksViewModel } from './webhooks-review.fixture';
import { labels } from './webhooks-review.labels';
import { notices } from './webhooks-review.states';

// Review only until the live integration history is available.
// The data service will replace this snapshot when that behavior lands.
@Component({
  selector: 'app-webhooks-review',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DecimalPipe,
    HlmButton,
    ...HlmCardImports,
    ShellIconComponent,
    SettingsPageCountsComponent,
    HlmLabel,
    HlmDialogImports,
    ...HlmTableImports,
    AbsoluteTimeComponent,
    PageHeaderComponent,
    StatusPillComponent,
    EmptyPatternComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    ...HlmSelectImports,
    WebhookIntentPreview,
    DesktopMutationDirective,
  ],
  host: { class: 'grid min-w-0 gap-4' },
  templateUrl: './webhooks-review.page.html',
})
export class WebhooksReviewPage {
  private readonly route = inject(ActivatedRoute);
  readonly model = input<WebhooksViewModel>(webhooksFixture);
  protected readonly pageCounts = computed<readonly SettingsPageCount[]>(() =>
    this.model().metrics.map((metric) => ({
      label: metric.label,
      value: metric.value,
      hint: metric.detail,
      icon: (
        {
          lucideCircle: 'plug',
          lucideActivity: 'shield-check',
          lucideClock: 'scroll',
          lucideInfo: 'alert',
        } as const
      )[metric.icon],
    })),
  );
  protected readonly query = toSignal(this.route.queryParamMap);
  protected readonly l = labels;
  protected readonly destination = computed(() => {
    const endpoint = this.selected();
    return this.state() === 'long' && endpoint?.id === 'wh-19'
      ? `${endpoint.destination}/release-checks/storefront-web/webhook-delivery-with-long-path`
      : endpoint?.destination;
  });
  protected readonly state = linkedSignal(() => {
    const query = this.query();
    const raw = query?.get('state') ?? this.model().state;
    if (raw === 'denied' || raw === 'read-revoked' || raw === '403') return '403';
    if (raw === '401') return '401';
    const record = query?.get('endpoint');
    if (record != null && !this.model().endpoints.some((endpoint) => endpoint.id === record))
      return '404';
    if (raw === 'success') return 'saved';
    if (raw === 'default' || raw === 'ready') return 'ready';
    return notices.has(raw) ? raw : '404';
  });
  protected readonly notice = computed(() => notices.get(this.state()));
  protected readonly showRecords = computed(
    () => !['loading', 'empty', 'error', '401', '403', '404', '429'].includes(this.state()),
  );
  protected readonly choice = linkedSignal(
    () => this.query()?.get('endpoint') ?? this.model().endpoints[0]?.id,
  );
  protected readonly viewing = linkedSignal(() => !!this.query()?.get('endpoint'));
  protected readonly selected = computed(() =>
    this.model().endpoints.find((e) => e.id === this.choice()),
  );
  protected readonly filter = linkedSignal(() => this.query()?.get('event') ?? '');
  protected readonly deliveries = computed(() =>
    this.model().deliveries.filter(
      (d) => d.endpointId === this.choice() && (!this.filter() || this.filter() === d.event),
    ),
  );
  protected readonly failedDelivery = computed(() =>
    !['empty-history', 'no-match', 'pending-delivery'].includes(this.state())
      ? this.deliveries().find((d) => d.tone === 'danger')
      : undefined,
  );
  protected readonly preview = linkedSignal({
    source: () => (this.viewing() ? this.choice() : undefined),
    computation: () => false,
  });
  protected readonly canPreview = computed(
    () => this.showRecords() && !['read-only', 'submitting', '412'].includes(this.state()),
  );
  protected readonly filterLabel = (value: unknown): string =>
    typeof value === 'string' && value ? value : this.l.all;
  protected setFilter(value: unknown): void {
    if (typeof value === 'string') this.filter.set(value);
  }
  protected clearFilter(): void {
    this.filter.set('');
    if (this.state() === 'no-match') this.state.set('ready');
  }
  protected selectEndpoint(id: string): void {
    if (id !== this.choice()) this.filter.set('');
    this.choice.set(id);
    this.viewing.set(true);
  }
}
