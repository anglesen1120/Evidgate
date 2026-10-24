// 09.1.1-T3.1 (plan §2 AC3, 00 §9.1/§9.2, FR-09-10): the wire format is defined once — in
// `contracts/openapi.yaml` and the `@evidgate/sdk` types generated from it — so `apps/web`
// never declares a payload of its own. A snake_case property key (the wire convention, e.g.
// `run_id`, `next_cursor`, `request_id`) or a declared name ending in Dto/DTO/Payload/
// Request/Response/Envelope means the author is mirroring a payload by hand: import the
// type from `@evidgate/sdk` instead.
//
// UI view models stay legal — `interface RunRowViewModel { runId: string }` is camelCase
// and locally named. Only declarations are inspected (`interface`, and a `type` alias whose
// type is an object type), never object literals used as values, so a spec may still build
// an error-envelope value inline.
'use strict';

/** Wire keys are snake_case, so any underscore marks a hand-written payload field. */
const WIRE_KEY = /_/;

/** Payload-shaped declaration names. */
const WIRE_NAME = /(?:Dto|DTO|Payload|Request|Response|Envelope)$/;

/** Type nodes that carry object-type members. */
const OBJECT_TYPES = new Set(['TSIntersectionType', 'TSTypeLiteral', 'TSUnionType']);

function isObjectType(node) {
  if (!node) {
    return false;
  }
  if (node.type === 'TSTypeLiteral') {
    return true;
  }
  if (node.type === 'TSParenthesizedType') {
    return isObjectType(node.typeAnnotation);
  }
  if (OBJECT_TYPES.has(node.type)) {
    return node.types.some(isObjectType);
  }
  return false;
}

function keyName(key) {
  if (key.type === 'Identifier') {
    return key.name;
  }
  if (key.type === 'Literal' && typeof key.value === 'string') {
    return key.value;
  }
  return null;
}

/** Every property signature of `type`, including the ones of nested literal types. */
function collectProperties(type, properties) {
  if (!type) {
    return properties;
  }
  if (type.type === 'TSTypeLiteral') {
    for (const member of type.members) {
      if (member.type !== 'TSPropertySignature') {
        continue;
      }
      const name = keyName(member.key);
      if (name !== null) {
        properties.push({ name, node: member.key });
      }
      collectProperties(member.typeAnnotation?.typeAnnotation, properties);
    }
  } else if (OBJECT_TYPES.has(type.type)) {
    for (const member of type.types) {
      collectProperties(member, properties);
    }
  } else if (type.type === 'TSParenthesizedType') {
    collectProperties(type.typeAnnotation, properties);
  }
  return properties;
}

module.exports = {
  meta: {
    type: 'problem',
    docs: {
      description: 'No hand-written wire DTO in apps/web: use @evidgate/sdk types.',
    },
    schema: [],
    messages: {
      wireKey:
        'Do not hand-write a wire payload: property "{{key}}" is a snake_case wire key. Import the payload type from @evidgate/sdk.',
      wireName:
        'Do not hand-write a wire payload: "{{name}}" is named like an API payload. Import the payload type from @evidgate/sdk.',
    },
  },

  create(context) {
    const filename = (context.filename || '').replaceAll('\\', '/');
    if (!filename.includes('apps/web/src/')) {
      return {};
    }

    function check(declaration, type) {
      const name = declaration.id.name;
      if (WIRE_NAME.test(name)) {
        context.report({ node: declaration.id, messageId: 'wireName', data: { name } });
      }
      for (const property of collectProperties(type, [])) {
        if (!WIRE_KEY.test(property.name)) {
          continue;
        }
        context.report({
          node: property.node,
          messageId: 'wireKey',
          data: { key: property.name },
        });
      }
    }

    return {
      TSInterfaceDeclaration(node) {
        if (node.declare) {
          return;
        }
        check(node, { type: 'TSTypeLiteral', members: node.body.body });
      },
      TSTypeAliasDeclaration(node) {
        if (node.declare || !isObjectType(node.typeAnnotation)) {
          return;
        }
        check(node, node.typeAnnotation);
      },
    };
  },
};
