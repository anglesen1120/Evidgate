import { Directive, TemplateRef, ViewContainerRef, effect, inject } from '@angular/core';
import { NarrowMutationPolicy } from './narrow-mutation-policy';

/** Wrap mutation controls only; facts, filters, copying and navigation stay outside this template. */
@Directive({ selector: '[appDesktopMutation]' })
export class DesktopMutationDirective {
  private readonly policy = inject(NarrowMutationPolicy);
  private readonly template = inject<TemplateRef<unknown>>(TemplateRef);
  private readonly container = inject(ViewContainerRef);
  private mounted = false;

  constructor() {
    effect(() => {
      const show = this.policy.canMutate();
      if (show === this.mounted) return;
      this.mounted = show;
      if (show) {
        this.container.createEmbeddedView(this.template);
      } else {
        this.container.clear();
      }
    });
  }
}
