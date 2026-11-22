import {
  ChangeDetectionStrategy,
  Component,
  type ElementRef,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardContent, HlmCardHeader, HlmCardTitle } from '@evidgate/ui/card';
import { GalleryExampleComponent } from './gallery-example.component';
import { GALLERY_COPY } from './gallery-example-copy';
import { GALLERY_STATES, type GallerySpecimen } from './gallery-sections';

@Component({
  selector: 'app-gallery-specimen',
  imports: [
    HlmButton,
    HlmCard,
    HlmCardContent,
    HlmCardHeader,
    HlmCardTitle,
    GalleryExampleComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0', '(keydown.escape)': 'closeComparison($event)' },
  template: `
    <article hlmCard class="min-w-0 gap-0 overflow-visible">
      <header hlmCardHeader class="border-b border-(--eg-border) p-(--eg-space-5)">
        <h3 hlmCardTitle class="m-0" [id]="'specimen-' + specimen().id">{{ specimen().title }}</h3>
      </header>
      <div hlmCardContent class="grid gap-(--eg-space-4) p-(--eg-space-5)">
        <app-gallery-example [kind]="specimen().id" [state]="'default'" />
        <button
          #trigger
          hlmBtn
          [variant]="'ghost'"
          type="button"
          class="justify-self-start"
          [attr.aria-labelledby]="'compare-' + specimen().id + ' specimen-' + specimen().id"
          [attr.aria-expanded]="expanded()"
          [attr.aria-controls]="'states-' + specimen().id"
          (click)="expanded.set(!expanded())"
        >
          <span [id]="'compare-' + specimen().id">{{ moreStates }}</span>
        </button>
        <div [id]="'states-' + specimen().id" [hidden]="!expanded()">
          @if (expanded()) {
            @for (state of states; track state.id) {
              <div class="grid gap-(--eg-space-3) border-t border-(--eg-border) py-(--eg-space-4)">
                <strong class="text-xs">{{ state.label }}</strong>
                <app-gallery-example [kind]="specimen().id" [state]="state.id" />
              </div>
            }
          }
        </div>
      </div>
    </article>
  `,
})
export class GallerySpecimenComponent {
  readonly specimen = input.required<GallerySpecimen>();
  protected readonly expanded = signal(false);
  protected readonly states = GALLERY_STATES;
  protected readonly moreStates = GALLERY_COPY.moreStates;

  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');

  protected closeComparison(event: Event): void {
    if (event.defaultPrevented || !this.expanded()) return;
    const target = event.target;
    if (
      target instanceof Element &&
      target.closest(
        '[aria-haspopup]:not([aria-haspopup="false"])[aria-expanded="true"], [role="listbox"], [role="menu"], [role="dialog"], [role="alertdialog"], dialog[open]',
      )
    ) {
      return;
    }
    // A native popover may leave focus on its invoker. Let the browser dismiss it first.
    if (
      event.currentTarget instanceof HTMLElement &&
      globalThis.CSS?.supports?.('selector(:popover-open)') &&
      event.currentTarget.querySelector('[popover]:popover-open')
    ) {
      return;
    }
    event.stopPropagation();
    this.expanded.set(false);
    this.trigger()?.nativeElement.focus();
  }
}
