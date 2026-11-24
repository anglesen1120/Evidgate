import { FieldHintComponent } from '../../../shared/ui/forms/field-hint.component';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { DesktopRequiredNoticeComponent } from '../../../shared/ui/narrow/desktop-required-notice.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { InlineFeedbackComponent } from '../../../shared/ui/states';
import { LINEAGE_COPY } from './knowledge-lineage.copy';
import { dependencySnapshot, lineage, type LineageState } from './knowledge-lineage.fixture';
import { RevocationPreviewStore } from './revocation-preview.store';

@Component({
  selector: 'app-revocation-effect',
  imports: [
    FieldHintComponent,
    NgTemplateOutlet,
    FormsModule,
    RouterLink,
    HlmButton,
    HlmCardImports,
    HlmDialogImports,
    HlmIcon,
    HlmInput,
    HlmLabel,
    InlineFeedbackComponent,
    DesktopRequiredNoticeComponent,
    AbsoluteTimeComponent,
  ],
  providers: [RevocationPreviewStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  templateUrl: './revocation-effect.component.html',
})
export class RevocationEffectComponent {
  protected readonly store = inject(RevocationPreviewStore);
  protected readonly policy = inject(NarrowMutationPolicy);
  readonly state = input.required<LineageState>();
  protected readonly c = LINEAGE_COPY;
  protected readonly record = lineage;
  protected readonly nonblank = /\S/;
  protected readonly snapshot = computed(() => dependencySnapshot(this.state()));
  protected readonly eligible = computed(() =>
    ['default', 'active', 'applied', '422', 'none', 'prepared'].includes(this.state()),
  );
  protected readonly opened = computed(
    () =>
      this.policy.canMutate() &&
      this.eligible() &&
      (this.store.requestedOpen() || (this.state() === '422' && !this.store.dismissed())),
  );
  protected readonly blank = computed(() => this.state() === '422' && !this.store.reason().trim());
  protected prepare(): void {
    if (this.opened() && this.policy.allowsMutation()) this.store.prepare();
  }
}
