// 09.1.1-T3.1 (plan §8 T47, 00 §5.6 W1): `features/<area>` is a vertical slice. A feature
// may import `core`, `shared` and its own area, never a sibling feature; `core` and `shared`
// are layers below the features and must not depend on one either. The rule resolves the
// specifier against the linted file (relative paths, the `@app/...` and `app/...` spellings
// used by the app tsconfig, dynamic `import()` and re-exports), so a relative `../onboarding`
// is caught exactly like an aliased one. No new dependency: the resolution is `node:path`.
'use strict';

const { posix } = require('node:path');

const WEB_SRC = 'apps/web/src/';
const FEATURES = 'apps/web/src/app/features/';
const CORE = 'apps/web/src/app/core/';
const SHARED = 'apps/web/src/app/shared/';

/** The repo-relative form of the linted file, e.g. apps/web/src/app/features/runs/x.ts. */
function webRelative(filename) {
  const normalized = filename.replaceAll('\\', '/');
  const index = normalized.lastIndexOf(WEB_SRC);
  return index === -1 ? null : normalized.slice(index);
}

/** The `features/<area>` a path belongs to, or null for anything outside a slice. */
function featureArea(path) {
  if (!path.startsWith(FEATURES)) {
    return null;
  }
  const rest = path.slice(FEATURES.length);
  const separator = rest.indexOf('/');
  if (separator > 0) {
    return rest.slice(0, separator);
  }
  // A specifier that stops at the area itself (`../onboarding`, its barrel) is still that
  // area; a rest with a dot is a file directly under `features/`, not an area.
  if (separator === -1 && rest.length > 0 && !rest.includes('.')) {
    return rest;
  }
  return null;
}

/** Resolves a specifier the way the app tsconfig will: relative, `@app/`, or `app/`. */
function resolveSpecifier(importer, specifier) {
  if (specifier.startsWith('.')) {
    return posix.join(posix.dirname(importer), specifier);
  }
  if (specifier.startsWith('@app/')) {
    return `${WEB_SRC}app/${specifier.slice('@app/'.length)}`;
  }
  if (specifier.startsWith('app/')) {
    return `${WEB_SRC}${specifier}`;
  }
  return null;
}

/** The literal source of an import/export/`import()` node, or null when it is computed. */
function literalSource(node) {
  if (!node) {
    return null;
  }
  if (node.type === 'Literal' && typeof node.value === 'string') {
    return node.value;
  }
  if (node.type === 'TemplateLiteral' && node.expressions.length === 0) {
    return node.quasis[0]?.value.cooked ?? null;
  }
  return null;
}

module.exports = {
  meta: {
    type: 'problem',
    docs: {
      description: 'W1: no import between apps/web features, or from core/shared into one.',
    },
    schema: [],
    messages: {
      featureToFeature:
        'W1: features/{{from}} must not import features/{{to}}. Move the shared code to core or shared, or reach the other feature through the router.',
      layerToFeature:
        'W1: {{layer}} must not depend on a feature (features/{{area}}). Move the feature-specific code into the feature, or behind a core interface.',
    },
  },

  create(context) {
    const importer = webRelative(context.filename || '');
    if (importer === null) {
      return {};
    }
    const importerArea = featureArea(importer);
    let importerLayer = null;
    if (importer.startsWith(CORE)) {
      importerLayer = 'core';
    } else if (importer.startsWith(SHARED)) {
      importerLayer = 'shared';
    }

    function check(node, specifier) {
      const target = specifier === null ? null : resolveSpecifier(importer, specifier);
      const targetArea = target === null ? null : featureArea(target);
      if (targetArea === null) {
        return;
      }
      if (importerArea !== null && targetArea !== importerArea) {
        context.report({
          node,
          messageId: 'featureToFeature',
          data: { from: importerArea, to: targetArea },
        });
        return;
      }
      if (importerLayer !== null) {
        context.report({
          node,
          messageId: 'layerToFeature',
          data: { layer: importerLayer, area: targetArea },
        });
      }
    }

    return {
      ImportDeclaration(node) {
        check(node.source, literalSource(node.source));
      },
      ExportAllDeclaration(node) {
        check(node.source, literalSource(node.source));
      },
      ExportNamedDeclaration(node) {
        if (node.source) {
          check(node.source, literalSource(node.source));
        }
      },
      ImportExpression(node) {
        check(node.source, literalSource(node.source));
      },
    };
  },
};
