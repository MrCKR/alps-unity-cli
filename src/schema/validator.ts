export interface SchemaIssue {
  path: string;
  expected: string;
  actual: string;
}

/** 校验生成器支持的 JSON Schema 子集；无法解析的 schema 关键字和引用同样 fail closed。 */
export function validateSchema(schema: unknown, value: unknown, path = "$", rootSchema: unknown = schema): SchemaIssue[] {
  if (!isRecord(schema)) return [issue(path, "valid schema", "missing schema")];
  const rule = schema;
  if (rule["x-unityCliAnyJson"] === true) return [];
  const supportedKeywords = new Set(["$schema", "$id", "title", "description", "$ref", "$defs", "allOf", "oneOf", "anyOf", "const", "enum", "type", "properties", "required", "additionalProperties", "items", "prefixItems", "minItems", "maxItems", "minLength", "maxLength", "minimum", "maximum", "pattern", "format", "uniqueItems"]);
  const unsupported = Object.keys(rule).find((key) => !supportedKeywords.has(key));
  if (unsupported) return [issue(path, "supported schema keyword", `unsupported keyword ${unsupported}`)];
  if (Object.keys(rule).length === 0) return [issue(path, "constrained schema", "unconstrained schema")];

  if (typeof rule.$ref === "string") {
    const resolved = resolveReference(rootSchema, rule.$ref);
    if (!resolved) return [issue(path, `resolvable reference ${rule.$ref}`, "unresolved reference")];
    const referenceIssues = validateSchema(resolved, value, path, rootSchema);
    if (referenceIssues.length > 0) return referenceIssues;
  }
  if (Array.isArray(rule.allOf)) {
    const issues = rule.allOf.flatMap((candidate) => validateSchema(candidate, value, path, rootSchema));
    if (issues.length > 0) return issues;
  }
  const alternatives = Array.isArray(rule.oneOf) ? rule.oneOf : Array.isArray(rule.anyOf) ? rule.anyOf : undefined;
  if (alternatives) {
    const matches = alternatives.filter((candidate) => validateSchema(candidate, value, path, rootSchema).length === 0).length;
    const valid = Array.isArray(rule.oneOf) ? matches === 1 : matches > 0;
    if (!valid) return [issue(path, Array.isArray(rule.oneOf) ? "exactly one allowed schema" : "an allowed schema", valueType(value))];
  }
  if ("const" in rule && !Object.is(rule.const, value)) return [issue(path, `const ${display(rule.const)}`, display(value))];
  if (Array.isArray(rule.enum) && !rule.enum.some((candidate) => Object.is(candidate, value))) return [issue(path, `enum ${rule.enum.map(display).join(", ")}`, display(value))];

  const allowedTypes = Array.isArray(rule.type) ? rule.type : typeof rule.type === "string" ? [rule.type] : [];
  if (allowedTypes.length > 0 && !allowedTypes.some((type) => matchesType(type, value))) return [issue(path, allowedTypes.join(" | "), valueType(value))];

  if (isRecord(value) && !Array.isArray(value) && (allowedTypes.includes("object") || rule.properties !== undefined || rule.additionalProperties !== undefined)) {
    const properties = isRecord(rule.properties) ? rule.properties : {};
    const issues: SchemaIssue[] = [];
    for (const key of Array.isArray(rule.required) ? rule.required.filter((item): item is string => typeof item === "string") : []) {
      if (!Object.prototype.hasOwnProperty.call(value, key)) issues.push(issue(`${path}.${key}`, "required property", "missing"));
    }
    for (const [key, child] of Object.entries(value)) {
      if (key in properties) issues.push(...validateSchema(properties[key], child, `${path}.${key}`, rootSchema));
      else if (rule.additionalProperties === false) issues.push(issue(`${path}.${key}`, "known property", "unknown property"));
      else if (isRecord(rule.additionalProperties)) issues.push(...validateSchema(rule.additionalProperties, child, `${path}.${key}`, rootSchema));
    }
    return issues;
  }

  if (Array.isArray(value) && (allowedTypes.includes("array") || rule.items !== undefined || rule.prefixItems !== undefined)) {
    const issues: SchemaIssue[] = [];
    if (typeof rule.minItems === "number" && value.length < rule.minItems) issues.push(issue(path, `at least ${rule.minItems} items`, `${value.length} items`));
    if (typeof rule.maxItems === "number" && value.length > rule.maxItems) issues.push(issue(path, `at most ${rule.maxItems} items`, `${value.length} items`));
    const prefixItems = Array.isArray(rule.prefixItems) ? rule.prefixItems : [];
    for (let index = 0; index < prefixItems.length && index < value.length; index += 1) issues.push(...validateSchema(prefixItems[index], value[index], `${path}[${index}]`, rootSchema));
    for (let index = prefixItems.length; index < value.length; index += 1) {
      if (rule.items === false) issues.push(issue(`${path}[${index}]`, `tuple with ${prefixItems.length} items`, "additional item"));
      else if (isRecord(rule.items)) issues.push(...validateSchema(rule.items, value[index], `${path}[${index}]`, rootSchema));
    }
    if (rule.uniqueItems === true && new Set(value.map((item) => JSON.stringify(item))).size !== value.length) issues.push(issue(path, "unique items", "duplicate item"));
    return issues;
  }

  if (typeof value === "string") {
    if (rule.format === "date-time" && !Number.isFinite(Date.parse(value))) return [issue(path, "date-time", value)];
    if (rule.format === "uuid" && !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) return [issue(path, "uuid", value)];
    if (typeof rule.minLength === "number" && value.length < rule.minLength) return [issue(path, `minimum length ${rule.minLength}`, `length ${value.length}`)];
    if (typeof rule.maxLength === "number" && value.length > rule.maxLength) return [issue(path, `maximum length ${rule.maxLength}`, `length ${value.length}`)];
    if (typeof rule.pattern === "string") {
      try {
        if (!new RegExp(rule.pattern).test(value)) return [issue(path, `pattern ${rule.pattern}`, value)];
      } catch {
        return [issue(path, "valid regex pattern", rule.pattern)];
      }
    }
  }
  if (typeof value === "number") {
    if (typeof rule.minimum === "number" && value < rule.minimum) return [issue(path, `>= ${rule.minimum}`, String(value))];
    if (typeof rule.maximum === "number" && value > rule.maximum) return [issue(path, `<= ${rule.maximum}`, String(value))];
  }
  return [];
}

function resolveReference(rootSchema: unknown, reference: string): unknown {
  if (!reference.startsWith("#/")) return undefined;
  let current: unknown = rootSchema;
  for (const encoded of reference.slice(2).split("/")) {
    if (!isRecord(current)) return undefined;
    const key = encoded.replace(/~1/g, "/").replace(/~0/g, "~");
    current = current[key];
  }
  return current;
}

function matchesType(type: unknown, value: unknown): boolean {
  if (type === "null") return value === null;
  if (type === "array") return Array.isArray(value);
  if (type === "object") return isRecord(value);
  if (type === "integer") return typeof value === "number" && Number.isInteger(value);
  if (type === "number") return typeof value === "number" && Number.isFinite(value);
  return typeof type === "string" && typeof value === type;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === "object" && !Array.isArray(value);
}

function issue(path: string, expected: string, actual: string): SchemaIssue { return { path, expected, actual }; }
function valueType(value: unknown): string { return value === null ? "null" : Array.isArray(value) ? "array" : typeof value; }
function display(value: unknown): string { return typeof value === "string" ? value : JSON.stringify(value); }
