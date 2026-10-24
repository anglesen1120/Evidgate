// 09.1.1-T3.1 (plan §2 AC3, 00 §9.1 same-origin API, ADR-0008 shape A): the application
// reaches the API only through `apps/web/src/app/core/http` (the HttpClient adapter, the
// functional interceptors, ApiGateway) and `core/data` (one data service per feature
// domain). A page, component, store or spec elsewhere calls a data service; it never
// imports `HttpClient`, and it never calls an `@evidgate/sdk` operation or the SDK's default
// `client` itself — that would bypass `HttpClient`, the interceptors, Angular's XSRF
// interceptor and `ApiGateway`'s success/failure decision.
//
// Two arms, both scoped to the same two owning directories:
//   - `@angular/common/http` is an allow-list of imported names (the view types the shared
//     error/UI patterns name, plus the providers a composition root may need);
//   - `@evidgate/sdk` is type-only, plus the allow-listed value helpers. A value import of
//     an operation (`listRuns`, …), `client`, `createClient`, `createConfig` or a namespace
//     is a violation; `import type { … }` and inline `{ type X }` specifiers are not.
// Neither arm fits `no-restricted-imports`: its `importNames` and `allowImportNames` are
// mutually exclusive, and it has no notion of a type-only import.
//
// Every syntactic form that binds a module runs the same check (F3): `import … from`,
// `export … from`, `export * from` and the dynamic `import('…')` with a string literal.
// Checking only `ImportDeclaration` left the boundary open through a re-export or a
// dynamic import.
//
// The `fetch`/XHR/WebSocket/EventSource globals and `window.fetch` — the other half of the
// same boundary — are ESLint's own `no-restricted-globals`, registered next to this rule in
// `apps/web/lint/boundaries.cjs`.
//
// Registered by `apps/web/lint/boundaries.cjs`, which also keeps the two core directories
// out of the block; the filename guard below repeats that so the rule is self-contained
// when a bare `Linter` is handed the blocks directly (the AC3 violating-file proof).
'use strict';

const HTTP_PACKAGE = '@angular/common/http';
const SDK_PACKAGE = '@evidgate/sdk';

/** Directories that own HTTP: the adapter + interceptors, and the data services. */
const EXEMPT_DIRECTORIES = [
  'apps/web/src/app/core/data/',
  'apps/web/src/app/core/http/',
  // 09.1.1-T12.1: the dev-only fixture backend *is* an `HttpBackend` — it replaces the XHR/fetch
  // backend under the real `HttpClient`, which is how a fixture answer still passes every
  // interceptor — so it imports `@angular/common/http` for the same reason `core/http` does. It
  // ships in no production build: `dev-backend.ts` is the empty seam production compiles.
  'apps/web/src/app/core/dev-fixtures/',
];

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

/**
 * The only `@evidgate/sdk` values a file outside core may use: the §9.2 envelope guard is
 * pure data inspection, so an error-handling helper anywhere may classify a body with it.
 * Everything else in the SDK is either a type (import it with `import type`) or talks to
 * the API (call a data service).
 */
const ALLOWED_SDK_IMPORTS = ['isErrorEnvelope'];

const ALLOWED_HTTP_NAMES = new Set(ALLOWED_HTTP_IMPORTS);
const ALLOWED_HTTP_TEXT = ALLOWED_HTTP_IMPORTS.join(', ');
const ALLOWED_SDK_NAMES = new Set(ALLOWED_SDK_IMPORTS);
const ALLOWED_SDK_TEXT = ALLOWED_SDK_IMPORTS.join(', ');

/** The module a `source` node names, or null when it is not a string literal. */
function moduleName(source) {
  if (!source || source.type !== 'Literal' || typeof source.value !== 'string') {
    return null;
  }
  return source.value;
}

/** The identifier (or string) an import/export specifier names. */
function specifierName(name) {
  return name.type === 'Identifier' ? name.name : String(name.value);
}

/** The name a specifier binds, phrased for the message of a rejected specifier. */
function boundName(specifier, packageName) {
  if (specifier.type === 'ImportDefaultSpecifier') {
    return 'the default export';
  }
  if (specifier.type === 'ImportNamespaceSpecifier') {
    return `the whole ${packageName} namespace`;
  }
  if (specifier.type === 'ExportSpecifier') {
    return specifierName(specifier.local);
  }
  return specifierName(specifier.imported);
}

module.exports = {
  meta: {
    type: 'problem',
    docs: {
      description:
        'Import HttpClient or @evidgate/sdk values only in apps/web/src/app/core/{http,data}.',
    },
    schema: [],
    messages: {
      httpImport:
        'Do not import {{name}} from @angular/common/http outside apps/web/src/app/core/{http,data}; call a data service (or ApiGateway) instead. Importable outside core: {{allowed}}.',
      httpSideEffect:
        'Do not import @angular/common/http for its side effects: only apps/web/src/app/core/http may.',
      httpModule:
        'Do not load all of @angular/common/http outside apps/web/src/app/core/{http,data}; call a data service (or ApiGateway) instead.',
      sdkOperation:
        'Do not import {{name}} from @evidgate/sdk outside apps/web/src/app/core/{http,data}: it calls the API directly. Import its type with `import type` if you need the shape, or call a data service (or ApiGateway). Allowed values: {{allowed}}.',
      sdkValue:
        'Do not use @evidgate/sdk as a value outside apps/web/src/app/core/{http,data}: use `import type`/`export type` for its types, or import only {{allowed}}, and call a data service (or ApiGateway) for API calls.',
    },
  },

  create(context) {
    const filename = (context.filename || '').replaceAll('\\', '/');
    if (EXEMPT_DIRECTORIES.some((directory) => filename.includes(directory))) {
      return {};
    }

    /** One `@angular/common/http` import or re-export specifier. */
    function checkHttpSpecifier(specifier) {
      const name = boundName(specifier, HTTP_PACKAGE);
      const named = specifier.type === 'ImportSpecifier' || specifier.type === 'ExportSpecifier';
      if (named && ALLOWED_HTTP_NAMES.has(name)) {
        return;
      }
      context.report({
        node: specifier,
        messageId: 'httpImport',
        data: { name, allowed: ALLOWED_HTTP_TEXT },
      });
    }

    /** One `@evidgate/sdk` import specifier: type-only, or an allow-listed value. */
    function checkSdkSpecifier(declaration, specifier) {
      const declarationIsType = declaration.importKind === 'type';
      const specifierIsType = specifier.importKind === 'type';
      if (declarationIsType || specifierIsType) {
        return;
      }
      const name = boundName(specifier, SDK_PACKAGE);
      // A default or namespace specifier binds the whole SDK surface (operations and the
      // default `client`), which no allow-listed helper covers.
      const isNamed = specifier.type === 'ImportSpecifier';
      if (isNamed && ALLOWED_SDK_NAMES.has(name)) {
        return;
      }
      if (!isNamed) {
        context.report({
          node: specifier,
          messageId: 'sdkValue',
          data: { allowed: ALLOWED_SDK_TEXT },
        });
        return;
      }
      context.report({
        node: specifier,
        messageId: 'sdkOperation',
        data: { name, allowed: ALLOWED_SDK_TEXT },
      });
    }

    return {
      ImportDeclaration(node) {
        const name = moduleName(node.source);
        if (name === HTTP_PACKAGE) {
          if (node.specifiers.length === 0) {
            context.report({ node, messageId: 'httpSideEffect' });
            return;
          }
          for (const specifier of node.specifiers) {
            checkHttpSpecifier(specifier);
          }
          return;
        }
        if (name === SDK_PACKAGE) {
          for (const specifier of node.specifiers) {
            checkSdkSpecifier(node, specifier);
          }
        }
      },

      ExportNamedDeclaration(node) {
        const name = moduleName(node.source);
        if (name === HTTP_PACKAGE) {
          for (const specifier of node.specifiers) {
            checkHttpSpecifier(specifier);
          }
          return;
        }
        if (name !== SDK_PACKAGE || node.exportKind === 'type') {
          return;
        }
        // `export { type RunPage } from '@evidgate/sdk'` is the export-side twin of an
        // inline `import { type RunPage }` and stays a type import. A value re-export
        // widens the boundary to every importer of this module, so it is rejected, even
        // for an allow-listed helper.
        for (const specifier of node.specifiers) {
          if (specifier.exportKind === 'type') {
            continue;
          }
          context.report({
            node: specifier,
            messageId: 'sdkValue',
            data: { allowed: ALLOWED_SDK_TEXT },
          });
        }
      },

      ExportAllDeclaration(node) {
        const name = moduleName(node.source);
        if (name === HTTP_PACKAGE) {
          context.report({ node, messageId: 'httpModule' });
          return;
        }
        if (name === SDK_PACKAGE && node.exportKind !== 'type') {
          context.report({ node, messageId: 'sdkValue', data: { allowed: ALLOWED_SDK_TEXT } });
        }
      },

      // Only a literal specifier is resolvable statically; `import(someVariable)` binds no
      // known module, so it stays the accepted limit here (a specifier built at run time
      // cannot name a boundary package without a literal anyway).
      ImportExpression(node) {
        const name = moduleName(node.source);
        if (name === HTTP_PACKAGE) {
          context.report({ node, messageId: 'httpModule' });
          return;
        }
        if (name === SDK_PACKAGE) {
          context.report({ node, messageId: 'sdkValue', data: { allowed: ALLOWED_SDK_TEXT } });
        }
      },
    };
  },
};
