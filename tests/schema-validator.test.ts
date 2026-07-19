import assert from "node:assert/strict";
import test from "node:test";
import { validateSchema } from "../src/schema/validator.js";

const schema = {
  type: "object",
  properties: {
    node: { $ref: "#/$defs/node" },
    tuple: { type: "array", prefixItems: [{ type: "string" }, { type: "integer" }], minItems: 2, maxItems: 2, items: false },
    dictionary: { type: "object", additionalProperties: { type: "boolean" } }
  },
  required: ["node", "tuple", "dictionary"],
  additionalProperties: false,
  $defs: {
    node: {
      type: "object",
      properties: { value: { type: "string" }, child: { oneOf: [{ type: "null" }, { $ref: "#/$defs/node" }] } },
      required: ["value"],
      additionalProperties: false
    }
  }
};

test("shared schema validator accepts refs, recursive values, tuples, and typed dictionaries", () => {
  assert.deepEqual(validateSchema(schema, {
    node: { value: "root", child: { value: "leaf", child: null } },
    tuple: ["count", 2],
    dictionary: { visible: true }
  }), []);
});

test("shared schema validator rejects nested, tuple, dictionary, and unknown-property errors", () => {
  const issues = validateSchema(schema, {
    node: { value: 1, extra: true },
    tuple: ["count", "two", false],
    dictionary: { visible: "yes" },
    unknown: true
  });
  assert.ok(issues.some((entry) => entry.path === "$.node.value"));
  assert.ok(issues.some((entry) => entry.path === "$.node.extra"));
  assert.ok(issues.some((entry) => entry.path === "$.tuple[1]"));
  assert.ok(issues.some((entry) => entry.path === "$.tuple[2]"));
  assert.ok(issues.some((entry) => entry.path === "$.dictionary.visible"));
  assert.ok(issues.some((entry) => entry.path === "$.unknown"));
});

test("shared schema validator enforces numeric ranges and date-time format", () => {
  assert.deepEqual(validateSchema({ type: "integer", minimum: 0, maximum: 10 }, 5), []);
  assert.equal(validateSchema({ type: "integer", minimum: 0 }, -1)[0].expected, ">= 0");
  assert.equal(validateSchema({ type: "integer", maximum: 10 }, 11)[0].expected, "<= 10");
  assert.deepEqual(validateSchema({ type: "string", format: "date-time" }, "2026-07-16T00:00:00.000Z"), []);
  assert.equal(validateSchema({ type: "string", format: "date-time" }, "not-a-date")[0].expected, "date-time");
});

test("shared schema validator fails closed on unresolved refs and unconstrained schemas", () => {
  const unresolved = validateSchema({ $ref: "#/$defs/missing", $defs: {} }, {});
  assert.equal(unresolved.length, 1);
  assert.match(unresolved[0].actual, /unresolved/);
  const unconstrained = validateSchema({}, { arbitrary: true });
  assert.equal(unconstrained.length, 1);
  assert.equal(unconstrained[0].actual, "unconstrained schema");
  const unsupported = validateSchema({ type: "object", unevaluatedProperties: false }, {});
  assert.equal(unsupported.length, 1);
  assert.match(unsupported[0].actual, /unsupported keyword/);
});
