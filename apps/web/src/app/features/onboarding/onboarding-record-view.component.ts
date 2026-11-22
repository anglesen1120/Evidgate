import { NgTemplateOutlet } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type TemplateRef,
  computed,
  inject,
  input,
  linkedSignal,
  output,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { StatusPillComponent, type StatusTone } from '../../shared/ui/chips/status-pill.component';
import type { OnboardingProgress } from './data/onboarding-progress.service';
import type { StepNumber } from './data/step-derivation';

export interface OnboardingRecordView {
  readonly title: string;
  readonly subject: string;
  readonly id: string | undefined;
  readonly status: string | undefined;
  readonly tone: StatusTone;
  readonly details: TemplateRef<unknown>;
  readonly opener: HTMLButtonElement;
}

export const RECORD_COPY = {
  actions: $localize`:@@onboarding.record.actions:Actions`,
  binding: $localize`:@@onboarding.record.binding:CI binding details`,
  token: $localize`:@@onboarding.record.token:Ingest token details`,
  health: $localize`:@@onboarding.record.health:Credential health details`,
  resource: $localize`:@@onboarding.record.resource:Resource details`,
  identifier: $localize`:@@onboarding.record.identifier:Identifier`,
  summary: $localize`:@@onboarding.record.summary:Summary`,
  history: $localize`:@@onboarding.record.history:History`,
  source: $localize`:@@onboarding.record.source:Source`,
  credential: $localize`:@@onboarding.record.credential:Credential`,
  readOnly: $localize`:@@onboarding.record.read-only:Read-only setup details. This view cannot change the record.`,
  status: $localize`:@@onboarding.connect.status:Status`,
};

@Component({
  selector: 'app-onboarding-record',
  imports: [HlmButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'contents' },
  template: `
    <button
      #opener
      hlmBtn
      type="button"
      [variant]="'outline'"
      [attr.aria-haspopup]="'dialog'"
      data-onboarding-record
      [attr.aria-label]="name()"
      (click)="open(opener)"
      class="pointer-coarse:min-h-(--eg-control-touch) pointer-coarse:min-w-(--eg-control-touch)"
    >
      {{ view }}
    </button>
  `,
})
export class OnboardingRecordButtonComponent {
  readonly appOnboardingRecord = input.required<TemplateRef<unknown>>();
  readonly subject = input.required<string>();
  readonly recordTitle = input.required<string>();
  readonly recordId = input<string>();
  readonly recordStatus = input<string>();
  readonly recordTone = input<StatusTone>('neutral');
  readonly inspect = output<OnboardingRecordView>();
  protected readonly view = $localize`:@@onboarding.record.view:View`;
  protected readonly name = computed(
    () => $localize`:@@onboarding.record.view-named:View ${this.subject()}:SUBJECT:`,
  );

  protected open(opener: HTMLButtonElement): void {
    this.inspect.emit({
      title: this.recordTitle(),
      subject: this.subject(),
      id: this.recordId(),
      status: this.recordStatus(),
      tone: this.recordTone(),
      details: this.appOnboardingRecord(),
      opener,
    });
  }
}

@Component({
  selector: 'app-onboarding-record-dialog',
  imports: [HlmDialogImports, HlmButton, NgTemplateOutlet, StatusPillComponent],
  host: { class: 'contents' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (selection(); as record) {
      <hlm-dialog
        [state]="'open'"
        [aria-modal]="true"
        [autoFocus]="'[data-record-heading]'"
        [restoreFocus]="record.opener"
        (closed)="selection.set(null)"
      >
        <hlm-dialog-content
          *hlmDialogPortal
          class="w-[min(560px,calc(100vw-var(--eg-space-8)))] max-w-[560px] max-h-[calc(100dvh-var(--eg-space-8))] grid-rows-[auto_minmax(0,1fr)_auto] overflow-hidden pointer-coarse:[&_.spartan-dialog-close]:size-(--eg-control-touch) max-[599.98px]:w-screen max-[599.98px]:max-w-none max-[599.98px]:h-dvh max-[599.98px]:max-h-dvh max-[599.98px]:rounded-none max-[599.98px]:border-0 max-[599.98px]:pt-[max(env(safe-area-inset-top),var(--eg-space-5))] max-[599.98px]:pb-[max(env(safe-area-inset-bottom),var(--eg-space-5))] max-[599.98px]:ps-[max(env(safe-area-inset-left),var(--eg-space-5))] max-[599.98px]:pe-[max(env(safe-area-inset-right),var(--eg-space-5))]"
        >
          <header hlmDialogHeader>
            <h2 hlmDialogTitle data-record-heading tabindex="-1">{{ record.title }}</h2>
            <p hlmDialogDescription class="wrap-anywhere">
              <bdi>{{ record.subject }}</bdi>
            </p>
            @if (record.id; as id) {
              <span class="text-xs text-muted-foreground"
                >{{ copy.identifier }}: <bdi class="font-mono">{{ id }}</bdi></span
              >
            }
            @if (record.status; as status) {
              <app-status-pill [tone]="record.tone" [label]="status" />
            }
            <p class="m-0 text-xs text-muted-foreground">{{ copy.readOnly }}</p>
          </header>
          <div
            class="min-w-0 overflow-y-auto overscroll-contain wrap-anywhere [&_dl]:m-0 [&_dl]:grid [&_dl]:gap-(--eg-space-4) [&_dl>div]:grid [&_dl>div]:gap-(--eg-space-2) min-[400px]:[&_dl>div]:grid-cols-[minmax(8rem,36%)_minmax(0,1fr)] [&_dt]:text-muted-foreground [&_dd]:m-0"
          >
            <h3 class="mb-(--eg-space-4) text-sm font-semibold">{{ copy.summary }}</h3>
            <ng-container [ngTemplateOutlet]="record.details" />
          </div>
          <footer hlmDialogFooter>
            <button
              hlmBtn
              hlmDialogClose
              type="button"
              [variant]="'outline'"
              class="pointer-coarse:min-h-(--eg-control-touch)"
            >
              {{ close }}
            </button>
          </footer>
        </hlm-dialog-content>
      </hlm-dialog>
    }
  `,
})
export class OnboardingRecordDialogComponent {
  private readonly destroyRef = inject(DestroyRef);
  protected readonly copy = RECORD_COPY;
  readonly scope = input.required<string>();
  readonly progress = input.required<OnboardingProgress | null>();
  readonly step = input.required<StepNumber>();
  protected readonly selection = linkedSignal({
    source: () => [this.scope(), this.progress(), this.step()] as const,
    computation: (): OnboardingRecordView | null => null,
  });
  protected readonly close = $localize`:@@ui.close:Close`;

  constructor() {
    afterRenderEffect((onCleanup) => {
      const record = this.selection();
      if (!record) return;
      const scope = this.scope();
      const step = this.step();
      const panel = record.opener.closest('section');
      const selector = 'button[data-onboarding-record]';
      const index = Array.from(panel?.querySelectorAll(selector) ?? []).indexOf(record.opener);
      onCleanup(() =>
        queueMicrotask(() => {
          if (
            this.destroyRef.destroyed ||
            this.scope() !== scope ||
            this.step() !== step ||
            record.opener.isConnected ||
            !panel?.isConnected
          )
            return;
          if (panel.ownerDocument.activeElement !== panel.ownerDocument.body) return;
          const remaining = panel.querySelectorAll<HTMLButtonElement>(selector);
          const target =
            remaining[Math.min(Math.max(index, 0), remaining.length - 1)] ??
            panel.querySelector<HTMLElement>('h2, h3');
          if (target && !remaining.length) target.tabIndex = -1;
          target?.focus();
        }),
      );
    });
  }

  open(record: OnboardingRecordView): void {
    if (!this.selection()) this.selection.set(record);
  }
}

@Component({
  selector: 'app-onboarding-table-range',
  host: { class: 'contents' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p
      class="my-0 border-t border-border py-(--eg-space-3) text-xs text-muted-foreground tabular-nums"
    >
      {{ description() }}
    </p>
  `,
})
export class OnboardingTableRangeComponent {
  readonly appOnboardingTableRange = input.required<number>();
  readonly truncated = input(false);
  protected readonly description = computed(() => {
    const end = this.appOnboardingTableRange();
    if (this.truncated())
      return $localize`:@@onboarding.record.range-partial:Showing ${end}:COUNT: records · more records are available`;
    if (!end) return $localize`:@@onboarding.record.no-rows:0 records`;
    return $localize`:@@onboarding.record.range:Showing 1–${end}:LAST: of ${end}:TOTAL:`;
  });
}
