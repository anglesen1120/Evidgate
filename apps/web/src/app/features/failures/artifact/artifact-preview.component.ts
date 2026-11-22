import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { ArtifactInspectionStore } from './artifact-inspection.store';
import { ARTIFACT_COPY } from './artifact-viewer.copy';
import type { ArtifactState, ArtifactViewModel } from './artifact-viewer.fixture';

@Component({
  selector: 'app-artifact-preview',
  imports: [NgTemplateOutlet, HlmButton, HlmCardImports, HlmDialogImports, StatusPillComponent],
  providers: [ArtifactInspectionStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  templateUrl: './artifact-preview.component.html',
})
export class ArtifactPreviewComponent {
  protected readonly viewer = inject(ArtifactInspectionStore);
  readonly record = input.required<ArtifactViewModel>();
  readonly state = input.required<ArtifactState>();
  protected readonly c = ARTIFACT_COPY;
  protected readonly fields = [this.c.name, this.c.email, this.c.address];
  protected readonly steps = [this.c.traceStep1, this.c.traceStep2, this.c.traceStep3];
  protected readonly open = computed(() => ['default', 'trace', 'reloaded'].includes(this.state()));
  protected readonly message = computed(() => {
    const state = this.state();
    return state in this.c ? this.c[state as keyof typeof ARTIFACT_COPY] : this.c.closed;
  });
  protected readonly tone = computed(() =>
    ['rejected', 'refresh'].includes(this.state()) ? 'danger' : 'neutral',
  );
}
