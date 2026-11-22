import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { CopyValueButtonComponent } from '../../../shared/ui/values/copy-value-button.component';
import type { WebhooksViewModel } from './webhooks-review.fixture';
import { labels } from './webhooks-review.labels';

type Intent = 'create' | 'rotate' | 'redeliver';
@Component({
  selector: 'app-webhook-intent-preview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmButton,
    HlmCheckbox,
    ...HlmDialogImports,
    HlmInput,
    HlmLabel,
    AbsoluteTimeComponent,
    CopyValueButtonComponent,
  ],
  templateUrl: './webhook-intent-preview.html',
})
export class WebhookIntentPreview {
  private readonly narrow = inject(NarrowMutationPolicy);
  readonly endpointId = input.required<string>();
  readonly rotationAllowed = input.required<boolean>();
  readonly failedDelivery = input<WebhooksViewModel['deliveries'][number]>();
  readonly recorded = output<'created' | 'rotated' | 'saved'>();
  protected readonly l = labels;
  protected readonly intent = signal<Intent | null>(null);
  protected readonly ack = signal(false);
  protected readonly name = signal('');
  protected readonly url = signal('');
  protected readonly runEvent = signal(false);
  protected readonly evalEvent = signal(false);
  protected readonly eventNames = ['run.state_changed', 'evaluation.created'] as const;
  protected readonly redaction = '[REDACTED · VALUE NOT GENERATED]';
  protected readonly validCreate = computed(() => {
    if (!this.name().trim() || !(this.runEvent() || this.evalEvent())) return false;
    try {
      const u = new URL(this.url());
      return u.protocol === 'https:' && !u.username && !u.password && !u.search && !u.hash;
    } catch {
      return false;
    }
  });
  protected open(intent: Intent): void {
    if (!this.narrow.allowsMutation() || !this.allowed(intent)) return;
    this.ack.set(false);
    this.name.set('');
    this.url.set('');
    this.runEvent.set(false);
    this.evalEvent.set(false);
    this.intent.set(intent);
  }
  protected confirm(): void {
    const intent = this.intent();
    if (
      !intent ||
      !this.narrow.allowsMutation() ||
      !this.allowed(intent) ||
      (intent === 'create' ? !this.validCreate() : !this.ack())
    )
      return;
    this.intent.set(null);
    this.recorded.emit(intent === 'create' ? 'created' : intent === 'rotate' ? 'rotated' : 'saved');
  }
  private allowed(intent: Intent): boolean {
    return (
      intent === 'create' ||
      (intent === 'rotate'
        ? this.endpointId() === 'wh-19' && this.rotationAllowed()
        : this.failedDelivery()?.endpointId === this.endpointId())
    );
  }
}
