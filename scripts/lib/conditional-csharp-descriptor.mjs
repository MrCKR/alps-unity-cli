import fs from "node:fs";
import path from "node:path";

/**
 * 从未进入当前 Unity 条件编译分支的 IUnityCliTool 源码提取静态 descriptor。
 * 只支持项目当前用于条件 Tool 的简单 DTO surface；遇到未知语法必须 fail closed。
 */
export function extractConditionalToolDescriptor(root, inventoryEntry) {
  const sourcePath = path.join(root, inventoryEntry.source);
  const source = fs.readFileSync(sourcePath, "utf8");
  const generic = source.match(/IUnityCliTool\s*<\s*[^,>]+\s*,\s*([A-Za-z0-9_.]+)\s*>/);
  if (!generic) throw new Error(`${inventoryEntry.source}: cannot resolve IUnityCliTool result type`);
  const name = stringField(source, "Name");
  if (name !== inventoryEntry.name) throw new Error(`${inventoryEntry.source}: descriptor Name mismatch`);
  const requestBody = classBody(source, "Request");
  const resultType = generic[1].split(".").at(-1);
  const resultBody = findClassBody(root, resultType);
  const examples = parseExamples(source, name);
  const target = optionalStringField(source, "Target") ?? "editor";
  const playModeSupport = optionalStringField(source, "PlayModeSupport") ?? "supported";
  return {
    availabilityScope: target === "runtime" ? "playModeSession" : "editorSession",
    description: stringField(source, "Description"),
    destructiveHint: booleanField(source, "DestructiveHint"),
    examples,
    group: stringField(source, "Group"),
    inputSchema: schemaFromClassBody(requestBody, `${inventoryEntry.source}:Request`),
    name,
    outputSchema: schemaFromClassBody(resultBody, `${inventoryEntry.source}:${resultType}`),
    permissions: stringArrayField(source, "Permissions"),
    playModeSupport,
    requiresPackages: stringArrayField(source, "RequiresPackages"),
    sideEffects: stringArrayField(source, "SideEffects"),
    source: inventoryEntry.descriptorOwner.startsWith("extension:") ? "extension" : "built-in",
    summary: stringField(source, "Summary"),
    supportsDryRun: booleanField(source, "SupportsDryRun"),
    target,
  };
}

/** 解析 public auto-property DTO；未知引用类型会拒绝生成，避免产生错误 schema。 */
function schemaFromClassBody(body, location) {
  const properties = {};
  const required = [];
  const pattern = /(\[UnityCliRequired\]\s*)?public\s+([A-Za-z0-9_.<>\[\]]+)\s+([A-Za-z0-9_]+)\s*\{\s*get;\s*set;\s*\}/g;
  for (const match of body.matchAll(pattern)) {
    const name = camelCase(match[3]);
    properties[name] = schemaForType(match[2], location);
    if (match[1]) required.push(name);
  }
  if (Object.keys(properties).length === 0 && !/class\s+Request\s*\{\s*\}/.test(`class Request {${body}}`)) {
    throw new Error(`${location}: no supported public properties found`);
  }
  return {
    additionalProperties: false,
    properties,
    ...(required.length > 0 ? { required: required.sort() } : {}),
    type: "object",
  };
}

function schemaForType(type, location) {
  if (type === "string" || type === "System.String") return { type: "string" };
  if (type === "bool" || type === "System.Boolean") return { type: "boolean" };
  if (["int", "long", "short", "byte", "System.Int32", "System.Int64"].includes(type)) return { type: "integer" };
  if (["float", "double", "decimal", "System.Single", "System.Double"].includes(type)) return { type: "number" };
  const array = type.match(/^(.+)\[\]$/);
  if (array) return { type: "array", items: schemaForType(array[1], location) };
  const list = type.match(/^(?:System\.Collections\.Generic\.)?List<(.+)>$/);
  if (list) return { type: "array", items: schemaForType(list[1], location) };
  throw new Error(`${location}: unsupported conditional DTO type ${type}`);
}

/** 当前 Tool examples 必须能完整还原 typed input 与 dryRun execution。 */
function parseExamples(source, toolName) {
  const examples = [];
  const pattern = /new\s+ToolExample\s*\{\s*Description\s*=\s*("(?:\\.|[^"])*")\s*,\s*Input\s*=\s*new\s+Request\s*(?:\(\))?\s*\{([^}]*)\}(?:\s*,\s*Execution\s*=\s*new\s+ToolExampleExecution\s*\{([^}]*)\})?\s*\}/g;
  for (const match of source.matchAll(pattern)) {
    const input = parseAssignments(match[2]);
    const execution = match[3] ? parseAssignments(match[3]) : undefined;
    examples.push({
      description: JSON.parse(match[1]),
      input,
      ...(execution && Object.keys(execution).length > 0 ? { execution } : {}),
    });
  }
  if (examples.length === 0) throw new Error(`${toolName}: conditional descriptor examples are not parseable`);
  return examples;
}

function parseAssignments(text) {
  const result = {};
  const pattern = /([A-Za-z0-9_]+)\s*=\s*("(?:\\.|[^"])*"|true|false|-?\d+(?:\.\d+)?)/g;
  for (const match of text.matchAll(pattern)) {
    const raw = match[2];
    result[camelCase(match[1])] = raw.startsWith('"') ? JSON.parse(raw) : raw === "true" ? true : raw === "false" ? false : Number(raw);
  }
  const residue = text.replace(pattern, "").replace(/[\s,]/g, "");
  if (residue.length > 0) throw new Error(`Unsupported conditional example assignment: ${residue}`);
  return result;
}

function findClassBody(root, className) {
  for (const file of csharpFiles(root)) {
    const source = fs.readFileSync(file, "utf8");
    if (new RegExp(`\\bclass\\s+${escapeRegex(className)}\\b`).test(source)) return classBody(source, className);
  }
  throw new Error(`Cannot find conditional result DTO class ${className}`);
}

function csharpFiles(root) {
  const result = [];
  const visit = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name);
      if (entry.isDirectory()) visit(full);
      else if (entry.name.endsWith(".cs")) result.push(full);
    }
  };
  visit(path.join(root, "unitypkg", "Editor"));
  visit(path.join(root, "extensions"));
  return result;
}

/** 以 brace depth 提取 class body，避免方法体中的大括号截断 DTO。 */
function classBody(source, className) {
  const match = new RegExp(`\\bclass\\s+${escapeRegex(className)}\\b[^\\{]*\\{`).exec(source);
  if (!match) throw new Error(`Cannot find class ${className}`);
  const open = source.indexOf("{", match.index);
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (let index = open; index < source.length; index += 1) {
    const char = source[index];
    if (inString) {
      if (escaped) escaped = false;
      else if (char === "\\") escaped = true;
      else if (char === '"') inString = false;
      continue;
    }
    if (char === '"') { inString = true; continue; }
    if (char === "{") depth += 1;
    else if (char === "}") {
      depth -= 1;
      if (depth === 0) return source.slice(open + 1, index);
    }
  }
  throw new Error(`Unterminated class ${className}`);
}

function stringField(source, field) {
  const value = optionalStringField(source, field);
  if (value === undefined) throw new Error(`Conditional descriptor is missing ${field}`);
  return value;
}

function optionalStringField(source, field) {
  const match = new RegExp(`\\b${escapeRegex(field)}\\s*=\\s*(\"(?:\\\\.|[^\"])*\")`).exec(source);
  return match ? JSON.parse(match[1]) : undefined;
}

function booleanField(source, field) {
  return new RegExp(`\\b${escapeRegex(field)}\\s*=\\s*true\\b`).test(source);
}

function stringArrayField(source, field) {
  const match = new RegExp(`\\b${escapeRegex(field)}\\s*=\\s*new\\[\\]\\s*\\{([^}]*)\\}`).exec(source);
  if (!match) return [];
  return [...match[1].matchAll(/"(?:\\.|[^"])*"/g)].map((item) => JSON.parse(item[0]));
}

function camelCase(value) {
  return value.length === 0 ? value : value[0].toLowerCase() + value.slice(1);
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
