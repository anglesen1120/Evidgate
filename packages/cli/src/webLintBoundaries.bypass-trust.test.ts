import { describe, expect, it } from 'vitest';

import {
  BYPASS_TRUST,
  CORE_HTTP_FILE,
  FEATURE_FILE,
  lint,
  ruleIds,
  ruleIdsAt,
} from './webLintBoundaries.testing.js';

describe('apps/web lint boundaries (09.1.1 AC3, plan §8 T47 W1)', () => {
  describe('no-bypass-security-trust', () => {
    // The done-when: a feature file hands sanitized output back to a sink.
    const bypassCall = [
      'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
      '  return sanitizer.bypassSecurityTrustHtml(value);',
      '}',
      '',
    ].join('\n');

    it('rejects a bypassSecurityTrustHtml call in a feature file', () => {
      expect(ruleIds(bypassCall, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    it('rejects each of the five DomSanitizer bypass methods', () => {
      for (const method of ['Html', 'Style', 'Script', 'Url', 'ResourceUrl']) {
        const code = [
          'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
          `  return sanitizer.bypassSecurityTrust${method}(value);`,
          '}',
          '',
        ].join('\n');
        expect(ruleIds(code, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
      }
    });

    it('rejects optional chaining and a string-computed member', () => {
      const optionalMember = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        '  return sanitizer?.bypassSecurityTrustUrl(value);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(optionalMember, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      const optionalCall = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        '  return sanitizer.bypassSecurityTrustScript?.(value);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(optionalCall, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      const computed = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        "  return sanitizer['bypassSecurityTrustResourceUrl'](value);",
        '}',
        '',
      ].join('\n');
      expect(ruleIds(computed, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    it('rejects an aliased member reference that is never called', () => {
      const code = [
        'export function bind(sanitizer: DomSanitizer): (value: string) => unknown {',
        '  const bindHtml = sanitizer.bypassSecurityTrustHtml;',
        '  return bindHtml;',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    it('rejects a destructured bypass and its bare identifier call', () => {
      const code = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        '  const { bypassSecurityTrustStyle } = sanitizer;',
        '  return bypassSecurityTrustStyle(value);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([BYPASS_TRUST, BYPASS_TRUST]);
    });

    // Each of these builds the member name without a plain identifier or string property, and
    // each is reported exactly once: the constant that spells the name is the evidence, so a
    // key the rule cannot evaluate cannot hide the call.
    it('rejects a name spelled by a template literal, a variable or Reflect.get', () => {
      const templateKey = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        '  return sanitizer[`bypassSecurityTrustHtml`](value);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(templateKey, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      // A template WITH an expression is not folded; its inner literal is what the rule sees.
      const interpolatedKey = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        "  return sanitizer[`${'bypassSecurityTrustHtml'}`](value);",
        '}',
        '',
      ].join('\n');
      expect(ruleIds(interpolatedKey, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      const variableKey = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        "  const name = 'bypassSecurityTrustResourceUrl';",
        '  return sanitizer[name](value);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(variableKey, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      const reflected = [
        'export function read(sanitizer: DomSanitizer, value: string): unknown {',
        "  return Reflect.get(sanitizer, 'bypassSecurityTrustScript');",
        '}',
        '',
      ].join('\n');
      expect(ruleIds(reflected, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    it('rejects a name split across string concatenation', () => {
      const inlineKey = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        "  return sanitizer['bypass' + 'SecurityTrustHtml'](value);",
        '}',
        '',
      ].join('\n');
      expect(ruleIds(inlineKey, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      const splitKey = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        "  const name = 'bypass' + 'SecurityTrustUrl';",
        '  return sanitizer[name](value);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(splitKey, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    it('rejects a bypass method imported under an alias', () => {
      const aliased = [
        "import { bypassSecurityTrustHtml as bindHtml } from './sanitize';",
        '',
        'export const bind = bindHtml;',
        '',
      ].join('\n');
      expect(ruleIds(aliased, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      const plain = [
        "import { bypassSecurityTrustUrl } from './sanitize';",
        '',
        'export const bind = bypassSecurityTrustUrl;',
        '',
      ].join('\n');
      expect(ruleIds(plain, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    // An inline template is source text, so a call inside it never reaches an AST visitor: the
    // property value is scanned by content instead.
    it('rejects a bypass call written inside an inline template', () => {
      const code = [
        '@Component({',
        "  selector: 'evidgate-viewer',",
        '  template: `<div [innerHTML]="sanitizer.bypassSecurityTrustHtml(html)"></div>`,',
        '})',
        'export class ViewerComponent {}',
        '',
      ].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      // A mention, a look-alike call and an unrelated property stay legal.
      const clean = [
        '@Component({',
        '  template: `<div [innerHTML]="html"></div>`,',
        "  styleUrl: './x.css',",
        '})',
        'export class CleanComponent {',
        "  note = 'bypassSecurityTrustHtml is not used here';",
        "  other = 'sanitizer.bypassSecurityTrustHtmlX(html)';",
        '}',
        '',
      ].join('\n');
      expect(lint(clean, FEATURE_FILE)).toEqual([]);
    });

    it('treats a constant template string as the constant it is', () => {
      const code = [
        'const viewer = { template: `bypassSecurityTrustHtml` };',
        'export const sink = viewer;',
        '',
      ].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    // The rule is a lint-time report, not a claim about the call: the app-origin policy simply
    // has no policy for the value, so the browser refuses it at the sink.
    it('reports the DOM-sink refusal rather than a throw from the call', () => {
      const messages = lint(bypassCall, FEATURE_FILE);
      expect(messages.length).toBe(1);
      expect(messages[0]?.message).toContain('no `angular#unsafe-bypass` Trusted Types policy');
      expect(messages[0]?.message).toContain('DOM sink');
      expect(messages[0]?.message).not.toContain('throws');
    });

    it('allows the bypass inside the reviewed artifact-viewer bridge', () => {
      const bridgeFile = 'apps/web/src/app/features/runs/artifact-viewer-bridge/x.ts';
      expect(lint(bypassCall, bridgeFile)).toEqual([]);
      expect(lint(bypassCall, 'apps/web/src/app/shared/artifact-viewer-bridge/x.ts')).toEqual([]);
    });

    it('does not allow a name that merely contains the bridge words', () => {
      expect(
        ruleIds(bypassCall, 'apps/web/src/app/features/runs/artifact-viewer-bridge-notes.ts'),
      ).toEqual([BYPASS_TRUST]);
      expect(
        ruleIds(bypassCall, 'apps/web/src/app/features/runs/not-artifact-viewer-bridge/x.ts'),
      ).toEqual([BYPASS_TRUST]);
      expect(ruleIds(bypassCall, CORE_HTTP_FILE)).toEqual([BYPASS_TRUST]);
    });

    // The guard reads the segments BELOW the last `apps/web/src/app` marker, so a directory
    // that merely shares the name — the checkout's own parent, or a `..` traveller — cannot
    // switch the rule off for the whole tree.
    it('anchors the allow-list below apps/web, not at any bridge segment in the path', () => {
      expect(
        ruleIdsAt(
          bypassCall,
          '/checkout/artifact-viewer-bridge/apps/web/src/app/features/runs/x.ts',
        ),
      ).toEqual([BYPASS_TRUST]);
      expect(
        ruleIdsAt(bypassCall, 'artifact-viewer-bridge/apps/web/src/app/features/runs/x.ts'),
      ).toEqual([BYPASS_TRUST]);
      expect(
        ruleIdsAt(bypassCall, '/checkout/apps/web/src/app/artifact-viewer-bridge/../features/x.ts'),
      ).toEqual([BYPASS_TRUST]);
      expect(
        ruleIdsAt(bypassCall, 'apps/web/src/app/artifact-viewer-bridge/../features/x.ts'),
      ).toEqual([BYPASS_TRUST]);
      expect(
        ruleIdsAt(bypassCall, '/checkout/apps/web/src/app/features/my-artifact-viewer-bridge/x.ts'),
      ).toEqual([BYPASS_TRUST]);
      expect(ruleIdsAt(bypassCall, '/checkout/artifact-viewer-bridge/x.ts')).toEqual([
        BYPASS_TRUST,
      ]);
    });

    it('allows a Windows or absolute path inside the reviewed bridge directory', () => {
      expect(
        ruleIdsAt(
          bypassCall,
          'C:\\repo\\apps\\web\\src\\app\\features\\runs\\artifact-viewer-bridge\\x.ts',
        ),
      ).toEqual([]);
      expect(
        ruleIdsAt(bypassCall, '/checkout/apps/web/src/app/shared/artifact-viewer-bridge/x.ts'),
      ).toEqual([]);
      // `.`/`..` that resolve back inside the bridge keep the file inside it.
      expect(
        ruleIdsAt(bypassCall, 'apps/web/src/app/features/./../artifact-viewer-bridge/x.ts'),
      ).toEqual([]);
    });

    it('accepts sanitize() and an unrelated method name', () => {
      const sanitize = [
        'export function read(sanitizer: DomSanitizer, value: string): string | null {',
        '  return sanitizer.sanitize(SecurityContext.HTML, value);',
        '}',
        '',
      ].join('\n');
      expect(lint(sanitize, FEATURE_FILE)).toEqual([]);

      const unrelated = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        '  return sanitizer.bypassSecurityTrustFoo(value);',
        '}',
        '',
      ].join('\n');
      expect(lint(unrelated, FEATURE_FILE)).toEqual([]);
    });

    it('holds a spec file to the same rule', () => {
      expect(ruleIds(bypassCall, 'apps/web/src/app/x.spec.ts')).toEqual([BYPASS_TRUST]);
    });

    // The catch-all constants must not turn ordinary text into a finding.
    it('accepts an unrelated string, a comment and a look-alike property', () => {
      const code = [
        '// sanitizer.bypassSecurityTrustHtml(value) is the call this file exists to avoid.',
        "export const label = 'not a bypass';",
        "export const lookalike = 'bypassSecurityTrustHtmlX';",
        'export const keys = { bypassSecurityTrustHtmlX: 1 };',
        'export function read(sanitizer: DomSanitizer): number {',
        '  return sanitizer.bypassSecurityTrustHtmlX;',
        '}',
        '',
      ].join('\n');
      expect(lint(code, FEATURE_FILE)).toEqual([]);

      // A key whose constant spells a different name is not a bypass either.
      const otherKey = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        "  return sanitizer['bypassSecurityTrustHtml' + 'X'](value);",
        '}',
        '',
      ].join('\n');
      expect(lint(otherKey, FEATURE_FILE)).toEqual([]);
    });
  });
});
