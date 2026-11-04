import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
  type ElementRef,
} from '@angular/core';
import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';

import cliManifest from '../../../../../../../packages/cli/package.json';

interface QuickstartInput {
  projectRef: string | null;
  token: Pick<IngestTokenView, 'name' | 'prefix'> | null;
  binding: Pick<CiBindingView, 'repository_full_name'> | null;
}

// Metadata only: deliberately pick individual fields rather than serializing credentials.
// Control and bidi characters are dropped from shell data (a newline would end the line inside
// the quotes) and flattened in comments (whoever pastes them should not run an escape sequence).
const UNSAFE_TEXT = /[\p{Cc}\p{Cf}\u2028\u2029]/gu;

function shellQuote(value: string): string {
  return `'${value.replace(UNSAFE_TEXT, '').replace(/'/g, `'"'"'`)}'`;
}

function comment(value: string): string {
  return value.replace(UNSAFE_TEXT, ' ');
}

export function buildQuickstartSnippet({ projectRef, token, binding }: QuickstartInput): string {
  const project = shellQuote(projectRef ?? '<project-slug>');
  const lines = [
    `# ${$localize`:@@onboarding.quickstart.variables:Set EVIDGATE_API_URL and EVIDGATE_PROJECT as non-secret CI variables.`}`,
    `export EVIDGATE_API_URL='<your-evidgate-api-url>'`,
    `export EVIDGATE_PROJECT=${project}`,
    '',
  ];

  if (token) {
    const name = comment(token.name);
    const prefix = comment(token.prefix);
    lines.push(
      `# ${$localize`:@@onboarding.quickstart.token-reference:Ingest token: ${name}:TOKEN_NAME: (prefix: ${prefix}:TOKEN_PREFIX:).`}`,
      `# ${$localize`:@@onboarding.quickstart.token-injection:Inject EVIDGATE_INGEST_TOKEN from your CI masked secret store. Never paste its value here.`}`,
    );
  } else if (binding) {
    const repository = comment(binding.repository_full_name);
    lines.push(
      `# ${$localize`:@@onboarding.quickstart.binding-reference:GitHub OIDC repository: ${repository}:REPOSITORY:.`}`,
      `# ${$localize`:@@onboarding.quickstart.binding-variable:Pass the EVIDGATE_BINDING_CHALLENGE repository variable by name; keep its value out of this file.`}`,
      `# ${$localize`:@@onboarding.quickstart.oidc-permission:Grant id-token: write in the CI job so the CLI can request GitHub OIDC credentials.`}`,
    );
  } else {
    lines.push(
      `# ${$localize`:@@onboarding.quickstart.create-token:Create an ingest token first, then inject EVIDGATE_INGEST_TOKEN from your CI masked secret store.`}`,
    );
  }

  lines.push(
    '',
    `npx --yes --package @evidgate/cli@${cliManifest.version} -- evidgate auth login`,
    `npx --yes --package @evidgate/cli@${cliManifest.version} -- evidgate quickstart --write .github/workflows/evidgate.yml`,
  );
  return lines.join('\n');
}

let nextQuickstartId = 0;

@Component({
  selector: 'app-cli-quickstart',
  imports: [HlmButton, HlmCardImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'data-testid': 'cli-quickstart', class: 'block min-w-0' },
  template: `
    <section hlmCard class="gap-(--eg-space-4) p-(--eg-space-6)" [attr.aria-labelledby]="headingId">
      <h3 hlmCardTitle [id]="headingId">{{ heading }}</h3>
      <p hlmCardDescription class="m-0" [id]="hintId">{{ hint }}</p>
      <pre
        #snippetControl
        class="m-0 min-w-0 max-w-full whitespace-pre-wrap rounded-md border border-border bg-muted p-(--eg-space-4) font-mono text-xs [overflow-wrap:anywhere] focus-visible:outline-solid focus-visible:outline-(length:--eg-focus-width) focus-visible:outline-ring focus-visible:outline-offset-(--eg-focus-offset)"
        tabindex="0"
        [attr.aria-labelledby]="headingId"
        [attr.aria-describedby]="hintId"
      ><code data-testid="cli-quickstart-snippet">{{ snippet() }}</code></pre>
      <div class="flex flex-wrap justify-end gap-(--eg-space-3)">
        <button
          type="button"
          hlmBtn
          [variant]="outlineVariant"
          class="min-h-(--eg-control-touch) max-w-full whitespace-normal aria-disabled:cursor-default aria-disabled:border-border aria-disabled:bg-muted aria-disabled:text-muted-foreground"
          data-testid="cli-quickstart-copy"
          [attr.aria-disabled]="copying()"
          [attr.aria-busy]="copying()"
          (click)="copy()"
        >
          {{ copyLabel }}
        </button>
      </div>
      <p
        class="m-0 min-h-(--eg-leading-sm) text-sm text-muted-foreground"
        data-testid="cli-quickstart-copy-status"
        role="status"
        [attr.aria-live]="'polite'"
        aria-atomic="true"
      >
        {{ copyStatus() }}
      </p>
    </section>
  `,
})
export class CliQuickstartComponent {
  readonly projectRef = input<string | null>(null);
  readonly token = input<QuickstartInput['token']>(null);
  readonly binding = input<QuickstartInput['binding']>(null);

  protected readonly outlineVariant = 'outline';
  protected readonly heading = $localize`:@@onboarding.quickstart.title:CLI quickstart`;
  protected readonly hint = $localize`:@@onboarding.quickstart.hint:Replace the API URL placeholder with your API root, without /v1. Run these commands with your CI variables and credentials injected. This snippet contains metadata only, not credentials.`;
  protected readonly copyLabel = $localize`:@@onboarding.quickstart.copy:Copy CLI quickstart commands`;
  protected readonly headingId = `cli-quickstart-heading-${++nextQuickstartId}`;
  protected readonly hintId = `${this.headingId}-hint`;
  protected readonly snippet = computed(() =>
    buildQuickstartSnippet({
      projectRef: this.projectRef(),
      token: this.token(),
      binding: this.binding(),
    }),
  );
  protected readonly copying = signal(false);
  protected readonly copyStatus = signal('');
  private readonly snippetControl = viewChild<ElementRef<HTMLElement>>('snippetControl');
  private readonly destroyRef = inject(DestroyRef);
  private attempt = 0;

  constructor() {
    effect(() => {
      this.snippet();
      this.attempt++;
      this.copying.set(false);
      this.copyStatus.set('');
    });
  }

  protected async copy(): Promise<void> {
    if (this.copying()) return;
    const attempt = ++this.attempt;
    const snippet = this.snippet();
    this.copying.set(true);
    this.copyStatus.set('');
    try {
      await navigator.clipboard.writeText(snippet);
      if (this.destroyRef.destroyed || attempt !== this.attempt || snippet !== this.snippet())
        return;
      this.copyStatus.set(
        $localize`:@@onboarding.quickstart.copied:CLI quickstart commands copied.`,
      );
    } catch {
      if (this.destroyRef.destroyed || attempt !== this.attempt || snippet !== this.snippet())
        return;
      this.snippetControl()?.nativeElement.focus();
      this.copyStatus.set(
        $localize`:@@onboarding.quickstart.copy-failed:Copy was unavailable. Select the commands above and copy them manually.`,
      );
    } finally {
      if (!this.destroyRef.destroyed && attempt === this.attempt) this.copying.set(false);
    }
  }
}
