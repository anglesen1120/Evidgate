import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
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
import { HlmInput } from '@evidgate/ui/input';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { agentPoolsFixture, type AgentPoolsViewModel } from './agent-pools-review.fixture';
import { labels } from './agent-pools-review.labels';
import { notices } from './agent-pools-review.states';

// Review only until the live pool directory is available.
// The data service will replace this snapshot when that behavior lands.
@Component({
  selector: 'app-agent-pools-review',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DecimalPipe,
    ...HlmCardImports,
    HlmIcon,
    HlmLabel,
    ...HlmTableImports,
    AbsoluteTimeComponent,
    PageHeaderComponent,
    StatusPillComponent,
    EmptyPatternComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    HlmInput,
    CopyableIdentifierComponent,
  ],
  host: { class: 'grid min-w-0 gap-4' },
  templateUrl: './agent-pools-review.page.html',
})
export class AgentPoolsReviewPage {
  private readonly route = inject(ActivatedRoute);
  readonly model = input<AgentPoolsViewModel>(agentPoolsFixture);
  protected readonly query = toSignal(this.route.queryParamMap);
  protected readonly l = labels;
  protected readonly noMatches = notices.get('no-match') ?? {
    title: labels.directory,
    description: labels.filter,
  };
  protected readonly longCapability =
    'capability/step_events_with_network_capture_and_retry_context · linux/chromium-headless-shell';
  protected readonly runningJob = 'job-71 · browser-ci-east · web-linux-01';
  protected readonly unmatchedJob = 'job-72 · ' + labels.unschedulable;
  protected readonly leaseStates = 'queued · leased · running · finished · lost · canceled';
  protected readonly mobilePlatform = 'iOS';
  protected readonly mobileEngine = 'xctest';
  protected readonly state = linkedSignal(() => {
    const query = this.query();
    const raw = query?.get('state') ?? this.model().state;
    if (raw === 'denied' || raw === 'read-revoked' || raw === '403') return '403';
    if (raw === '401') return '401';
    const record = query?.get('pool');
    if (record != null && !this.model().directory.some((pool) => pool.name === record))
      return '404';
    if (raw === 'success') return 'restored';
    if (raw === 'default' || raw === 'ready') return 'ready';
    return notices.has(raw) ? raw : '404';
  });
  protected readonly notice = computed(() => notices.get(this.state()));
  protected readonly showRecords = computed(
    () =>
      !['loading', 'empty', 'error', '401', '403', '404', '429', 'no-match'].includes(this.state()),
  );
  protected readonly filter = signal('');
  protected readonly matches = computed(() => {
    const term = this.filter().trim().toLocaleLowerCase();
    return this.model().directory.filter(
      (p) =>
        !term || [p.name, p.description, p.badge].some((v) => v.toLocaleLowerCase().includes(term)),
    );
  });
  protected readonly choice = linkedSignal(() =>
    this.state() === 'unschedulable'
      ? 'mobile-lab'
      : (this.query()?.get('pool') ?? this.model().directory[0]?.name),
  );
  protected readonly selected = computed(
    () => this.matches().find((p) => p.name === this.choice()) ?? this.matches()[0],
  );
  protected recover(): void {
    this.filter.set('');
    this.state.set('restored');
  }
}
