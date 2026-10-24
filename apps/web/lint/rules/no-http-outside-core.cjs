// 09.1.1-T3.1 (plan §2 AC3, 00 §9.1 same-origin API): the application reaches the API
// only through `apps/web/src/app/core/http` (the HttpClient adapter, the functional
// interceptors, ApiGateway) and `core/data` (one data service per feature domain). A page,
// component, store or spec elsewhere calls a data service; it never imports `HttpClient`.
// The other half of the same boundary — the `fetch`/XHR/WebSocket/EventSource globals and
// `window.fetch` — is ESLint's own `no-restricted-globals`, registered next to this rule in
// `apps/web/lint/boundaries.cjs`.
//
// Only the import half is a local rule: the allowance below is an allow-list of imported
// names, and `no-restricted-imports` cannot express one (`importNames` and
// `allowImportNames` are mutually exclusive).
//
// Registered by `apps/web/lint/boundaries.cjs`, which also keeps the two core directories
// out of the block; the filename guard below repeats that so the rule is self-contained
// when a bare `Linter` is handed the blocks directly (the AC3 violating-file proof).
'use strict';

/** Directories that own HTTP: the adapter + interceptors, and the data services. */
const EXEMPT_DIRECTORIES = ['apps/web/src/app/core/data/', 'apps/web/src/app/core/http/'];

/**
 * The only `@angular/common/http` symbols a file outside core may import: the view types
 * the shared error/UI patterns name, plus the providers and the functional interceptor
 * type a composition root needs if it ever wires `HttpClient` itself. `provideApi()` in
 * `core/http/api-client.ts` owns that today, so nothing outside core imports any of these
 * yet — the list exists so that a future `app.config.ts` line does not weaken the rule.
 */
const ALLOWED_HTTP_IMPORTS = [
  'HttpContext',
  'HttpErrorResponse',
  'HttpEvent',
  'HttpHandlerFn',
  'HttpHeaders',
  'HttpInterceptorFn',
  'HttpParams',
  'HttpRequest',
  'provideHttpClient',
  'withInterceptors',
  'withXsrfConfiguration',
];

const ALLOWED_NAMES = new Set(ALLOWED_HTTP_IMPORTS);
const ALLOWED_TEXT = ALLOWED_HTTP_IMPORTS.join(', ');

module.exports = {
  meta: {
    type: 'problem',
    docs: {
      description: 'Import @angular/common/http only in apps/web/src/app/core/{http,data}.',
    },
    schema: [],
    messages: {
      httpImport:
        'Do not import {{name}} from @angular/common/http outside apps/web/src/app/core/{http,data}; call a data service (or ApiGateway) instead. Importable outside core: {{allowed}}.',
      httpSideEffect:
        'Do not import @angular/common/http for its side effects: only apps/web/src/app/core/http may.',
    },
  },

  create(context) {
    const filename = (context.filename || '').replaceAll('\\', '/');
    if (EXEMPT_DIRECTORIES.some((directory) => filename.includes(directory))) {
      return {};
    }

    function importedName(specifier) {
      if (specifier.type === 'ImportDefaultSpecifier') {
        return 'the default export';
      }
      if (specifier.type === 'ImportNamespaceSpecifier') {
        return 'the whole @angular/common/http namespace';
      }
      const imported = specifier.imported;
      return imported.type === 'Identifier' ? imported.name : String(imported.value);
    }

    return {
      ImportDeclaration(node) {
        if (node.source.value !== '@angular/common/http') {
          return;
        }
        if (node.specifiers.length === 0) {
          context.report({ node, messageId: 'httpSideEffect' });
          return;
        }
        for (const specifier of node.specifiers) {
          const name = importedName(specifier);
          if (specifier.type === 'ImportSpecifier' && ALLOWED_NAMES.has(name)) {
            continue;
          }
          context.report({
            node: specifier,
            messageId: 'httpImport',
            data: { name, allowed: ALLOWED_TEXT },
          });
        }
      },
    };
  },
};
