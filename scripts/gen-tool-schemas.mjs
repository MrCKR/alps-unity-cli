// 构建期从 defineTool 的真实泛型参数生成严格 JSON Schema。

import { createRequire } from "node:module";
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const ts = require("typescript");
const SCRIPT_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const ROOT = resolve(process.argv[2] ?? SCRIPT_ROOT);
const TOOLS_DIR = join(ROOT, "src", "tools");
const EDITOR_STATE_STABLE_SCHEMA = join(ROOT, "contracts", "stable-session", "editor-state.schema.json");

function collectTsFiles(dir, files = []) {
  if (!existsSync(dir)) return files;
  for (const entry of readdirSync(dir).sort()) {
    const fullPath = join(dir, entry);
    if (statSync(fullPath).isDirectory()) collectTsFiles(fullPath, files);
    else if (entry.endsWith(".ts") && entry !== "define.ts" && !entry.startsWith(".generated-")) files.push(fullPath);
  }
  return files;
}

function loadProgram() {
  const configPath = join(ROOT, "tsconfig.json");
  if (!existsSync(configPath)) throw new Error(`tsconfig.json not found: ${configPath}`);
  const configFile = ts.readConfigFile(configPath, (path) => readFileSync(path, "utf8"));
  if (configFile.error) throw new Error(ts.flattenDiagnosticMessageText(configFile.error.messageText, "\n"));
  const parsed = ts.parseJsonConfigFileContent(configFile.config, ts.sys, ROOT, undefined, configPath);
  if (parsed.errors.length > 0) {
    throw new Error(parsed.errors.map((error) => ts.flattenDiagnosticMessageText(error.messageText, "\n")).join("\n"));
  }
  return ts.createProgram({ rootNames: parsed.fileNames, options: parsed.options });
}

function compareText(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

function propertyName(node) {
  if (ts.isIdentifier(node) || ts.isStringLiteral(node) || ts.isNumericLiteral(node)) return node.text;
  return undefined;
}

function findDefinitions(sourceFile, functionName, kind, errors) {
  const definitions = [];
  function visit(node) {
    if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === functionName) {
      const object = node.arguments[0];
      const nameProperty = object && ts.isObjectLiteralExpression(object)
        ? object.properties.find((property) => ts.isPropertyAssignment(property) && propertyName(property.name) === "name")
        : undefined;
      const name = nameProperty && ts.isPropertyAssignment(nameProperty) && ts.isStringLiteral(nameProperty.initializer)
        ? nameProperty.initializer.text
        : undefined;
      const capability = name ?? "<unknown>";
      if (!name) errors.push(formatError(sourceFile.fileName, kind, capability, "<generic>", "$", "unknown", "capability name must be a string literal"));
      if (!node.typeArguments || node.typeArguments.length !== 2) {
        errors.push(formatError(sourceFile.fileName, kind, capability, "<generic>", "$", "unknown", "exactly two explicit generic arguments are required"));
      } else if (name) {
        definitions.push({ name, fileName: sourceFile.fileName, kind, requestNode: node.typeArguments[0], resultNode: node.typeArguments[1] });
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(sourceFile);
  return definitions;
}

function formatError(fileName, kind, capability, typeName, path, typeText, reason) {
  return `${relative(ROOT, fileName).replaceAll("\\", "/")}: ${kind} ${JSON.stringify(capability)}, type ${typeName}, path ${path}, TypeScript type ${JSON.stringify(typeText)}: ${reason}`;
}

class SchemaBuilder {
  constructor(checker, definition, typeName, errors) {
    this.checker = checker;
    this.definition = definition;
    this.typeName = typeName;
    this.errors = errors;
    this.defs = {};
    this.active = new Map();
    this.usedDefNames = new Map();
    this.requiredDefs = new Set();
  }

  build(type, path = "$", allowUndefined = false) {
    const schema = this.typeSchema(type, path, allowUndefined);
    const defNames = Object.keys(this.defs).sort();
    if (defNames.length === 0) return schema;
    return { ...schema, $defs: Object.fromEntries(defNames.map((name) => [name, this.defs[name]])) };
  }

  fail(type, path, reason) {
    const text = this.checker.typeToString(type, undefined, ts.TypeFormatFlags.NoTruncation | ts.TypeFormatFlags.UseAliasDefinedOutsideCurrentScope);
    this.errors.push(formatError(this.definition.fileName, this.definition.kind, this.definition.name, this.typeName, path, text, reason));
    return { "x-unityCliSchemaError": true };
  }

  isAnyJson(type) {
    const symbol = type.aliasSymbol ?? type.getSymbol?.();
    if (symbol?.getName() !== "AnyJson") return false;
    return (symbol.declarations ?? []).some((declaration) => declaration.getSourceFile().fileName.replaceAll("\\", "/").endsWith("/schema/any-json.ts"));
  }

  typeSchema(type, path, allowUndefined = false) {
    if (this.isAnyJson(type)) return { "x-unityCliAnyJson": true };
    if (type.flags & ts.TypeFlags.Any) return this.fail(type, path, "implicit any is not supported; use AnyJson for intentional dynamic JSON");
    if (type.flags & ts.TypeFlags.Unknown) return this.fail(type, path, "unknown is not supported; use AnyJson for intentional dynamic JSON");
    if (type.flags & ts.TypeFlags.Never) return this.fail(type, path, "never is not representable in a public schema");
    if (type.flags & ts.TypeFlags.Undefined) return allowUndefined ? null : this.fail(type, path, "undefined is only supported through an optional property");
    if (type.flags & ts.TypeFlags.Null) return { type: "null" };
    if (type.flags & ts.TypeFlags.StringLiteral) return { type: "string", const: type.value };
    if (type.flags & ts.TypeFlags.NumberLiteral) return { type: "number", const: type.value };
    if (type.flags & ts.TypeFlags.BooleanLiteral) return { type: "boolean", const: type.intrinsicName === "true" };
    if (type.flags & ts.TypeFlags.String) return { type: "string" };
    if (type.flags & ts.TypeFlags.Number) return { type: "number" };
    if (type.flags & ts.TypeFlags.Boolean) return { type: "boolean" };
    if (type.flags & ts.TypeFlags.BigInt || type.flags & ts.TypeFlags.ESSymbol || type.flags & ts.TypeFlags.UniqueESSymbol) return this.fail(type, path, "type is not representable in JSON");
    if (type.isUnion?.()) return this.unionSchema(type, path, allowUndefined);
    if (this.checker.isTupleType(type)) return this.tupleSchema(type, path);
    if (this.checker.isArrayType(type) || this.isReadonlyArray(type)) {
      const elementType = this.checker.getTypeArguments(type)[0];
      if (!elementType) return this.fail(type, path, "array element type could not be resolved");
      return { type: "array", items: this.typeSchema(elementType, `${path}[]`) };
    }
    if (type.flags & (ts.TypeFlags.Object | ts.TypeFlags.Intersection)) return this.objectSchema(type, path);
    if (type.flags & ts.TypeFlags.TypeParameter) return this.fail(type, path, "unresolved generic type parameter is not supported");
    return this.fail(type, path, "unsupported TypeScript type");
  }

  isReadonlyArray(type) {
    const symbol = type.aliasSymbol ?? type.getSymbol?.();
    return symbol?.getName() === "ReadonlyArray";
  }

  // 公开字符串约束与 TypeScript property 同位声明，避免 generator 内出现 capability 特例。
  applyPropertyConstraints(schema, declaration, type, path) {
    for (const tag of ts.getJSDocTags(declaration)) {
      if (tag.tagName.text !== "minLength") continue;
      const raw = typeof tag.comment === "string" ? tag.comment.trim() : "";
      const value = Number(raw);
      if (!Number.isSafeInteger(value) || value < 0 || schema.type !== "string") {
        this.fail(type, path, "@minLength requires a non-negative integer on a string property");
        return schema;
      }
      schema = { ...schema, minLength: value };
    }
    return schema;
  }

  unionSchema(type, path, allowUndefined) {
    const members = [];
    for (const member of type.types) {
      if (member.flags & ts.TypeFlags.Undefined) {
        if (!allowUndefined) this.fail(member, path, "undefined is only supported through an optional property");
        continue;
      }
      members.push(member);
    }
    if (members.length === 0) return this.fail(type, path, "union has no JSON-representable members");
    const values = members.map((member) => this.literalValue(member));
    if (values.every((value) => value !== undefined)) {
      const kinds = new Set(values.map((value) => value === null ? "null" : typeof value));
      if (kinds.size === 1) {
        const kind = [...kinds][0];
        return kind === "null" ? { type: "null" } : { type: kind, enum: values };
      }
    }
    if (members.length === 1) return this.typeSchema(members[0], path);
    return { oneOf: members.map((member) => this.typeSchema(member, path)) };
  }

  literalValue(type) {
    if (type.flags & ts.TypeFlags.StringLiteral || type.flags & ts.TypeFlags.NumberLiteral) return type.value;
    if (type.flags & ts.TypeFlags.BooleanLiteral) return type.intrinsicName === "true";
    if (type.flags & ts.TypeFlags.Null) return null;
    return undefined;
  }

  tupleSchema(type, path) {
    const target = type.target ?? type;
    const elementTypes = this.checker.getTypeArguments(type);
    const elementFlags = target.elementFlags ?? elementTypes.map(() => ts.ElementFlags.Required);
    const prefixItems = [];
    let minItems = 0;
    let restSchema;
    for (let index = 0; index < elementTypes.length; index += 1) {
      const flags = elementFlags[index] ?? ts.ElementFlags.Required;
      const itemSchema = this.typeSchema(elementTypes[index], `${path}[${index}]`, Boolean(flags & ts.ElementFlags.Optional));
      if (flags & ts.ElementFlags.Rest) restSchema = itemSchema;
      else {
        prefixItems.push(itemSchema);
        if (!(flags & ts.ElementFlags.Optional)) minItems = prefixItems.length;
      }
    }
    return { type: "array", prefixItems, minItems, ...(restSchema ? { items: restSchema } : { items: false, maxItems: prefixItems.length }) };
  }

  objectSchema(type, path) {
    const activeName = this.active.get(type);
    if (activeName) {
      this.requiredDefs.add(activeName);
      return { $ref: `#/$defs/${activeName}` };
    }
    const symbol = type.aliasSymbol ?? type.getSymbol?.();
    const declarations = symbol?.declarations ?? [];
    if (declarations.some((declaration) => declaration.getSourceFile().isDeclarationFile && this.hasCallableMembers(type))) return this.fail(type, path, "runtime/library object types are not supported");

    const defName = this.definitionName(type, symbol);
    this.active.set(type, defName);
    const properties = {};
    const required = [];
    for (const property of this.checker.getPropertiesOfType(type).sort((a, b) => compareText(a.getName(), b.getName()))) {
      const declaration = property.valueDeclaration ?? property.declarations?.[0];
      if (!declaration) {
        this.fail(type, `${path}.${property.getName()}`, "property declaration could not be resolved");
        continue;
      }
      if (property.flags & ts.SymbolFlags.Method) {
        this.fail(type, `${path}.${property.getName()}`, "methods are not representable in JSON");
        continue;
      }
      const optional = Boolean(property.flags & ts.SymbolFlags.Optional);
      const propertyType = this.checker.getTypeOfSymbolAtLocation(property, declaration);
      const propertyPath = `${path}.${property.getName()}`;
      properties[property.getName()] = this.applyPropertyConstraints(this.typeSchema(propertyType, propertyPath, optional), declaration, propertyType, propertyPath);
      if (!optional) required.push(property.getName());
    }
    const stringIndex = this.checker.getIndexTypeOfType(type, ts.IndexKind.String);
    const schema = {
      type: "object",
      properties,
      ...(required.length > 0 ? { required } : {}),
      additionalProperties: stringIndex ? this.typeSchema(stringIndex, `${path}.*`) : false
    };
    this.active.delete(type);
    if (this.requiredDefs.has(defName)) this.defs[defName] = schema;
    return schema;
  }

  hasCallableMembers(type) {
    return this.checker.getSignaturesOfType(type, ts.SignatureKind.Call).length > 0
      || this.checker.getSignaturesOfType(type, ts.SignatureKind.Construct).length > 0
      || this.checker.getPropertiesOfType(type).some((property) => property.flags & ts.SymbolFlags.Method);
  }

  definitionName(type, symbol) {
    const base = (symbol?.getName() && symbol.getName() !== "__type" ? symbol.getName() : this.typeName).replace(/[^A-Za-z0-9_.-]/g, "_");
    const owner = this.usedDefNames.get(base);
    if (!owner || owner === type) {
      this.usedDefNames.set(base, type);
      return base;
    }
    let suffix = 2;
    while (this.usedDefNames.has(`${base}_${suffix}`) && this.usedDefNames.get(`${base}_${suffix}`) !== type) suffix += 1;
    const name = `${base}_${suffix}`;
    this.usedDefNames.set(name, type);
    return name;
  }
}

function normalizeSchema(value, key) {
  if (value === true && key === "value") return { "x-unityCliAnyJson": true };
  if (Array.isArray(value)) return value.map((item) => normalizeSchema(item));
  if (!value || typeof value !== "object") return value;
  return Object.fromEntries(Object.keys(value).sort(compareText).map((childKey) => [childKey, normalizeSchema(value[childKey], childKey)]));
}

function loadEditorStateOutputSchema() {
  if (!existsSync(EDITOR_STATE_STABLE_SCHEMA)) throw new Error(`stable editor.state schema not found: ${EDITOR_STATE_STABLE_SCHEMA}`);
  const schema = JSON.parse(readFileSync(EDITOR_STATE_STABLE_SCHEMA, "utf8"));
  if (!schema || typeof schema !== "object" || Array.isArray(schema)) throw new Error("stable editor.state schema must be a JSON object");
  return normalizeSchema(schema);
}

function buildInjections(program, directory, functionName, kind, names, errors, stableEditorStateOutputSchema) {
  const checker = program.getTypeChecker();
  const definitions = collectTsFiles(directory).flatMap((fileName) => {
    const sourceFile = program.getSourceFile(fileName);
    if (!sourceFile) {
      errors.push(formatError(fileName, kind, "<unknown>", "<generic>", "$", "unknown", "source file is not part of the TypeScript Program"));
      return [];
    }
    return findDefinitions(sourceFile, functionName, kind, errors);
  });
  const injections = [];
  for (const definition of definitions) {
    const previous = names.get(definition.name);
    if (previous) {
      errors.push(formatError(definition.fileName, kind, definition.name, "<generic>", "$", "duplicate capability", `name already declared in ${relative(ROOT, previous).replaceAll("\\", "/")}`));
      continue;
    }
    names.set(definition.name, definition.fileName);
    const requestType = checker.getTypeFromTypeNode(definition.requestNode);
    const resultType = checker.getTypeFromTypeNode(definition.resultNode);
    const requestName = checker.typeToString(requestType, undefined, ts.TypeFormatFlags.NoTruncation);
    const resultName = checker.typeToString(resultType, undefined, ts.TypeFormatFlags.NoTruncation);
    const inputSchema = new SchemaBuilder(checker, definition, requestName, errors).build(requestType);
    // editor.state 的公开契约由 stable schema 独占，避免 TS 推导弱化 integer、format 等语义。
    let outputSchema;
    if (kind === "Tool" && definition.name === "editor.state") {
      if (!stableEditorStateOutputSchema) throw new Error(`stable editor.state schema not found: ${EDITOR_STATE_STABLE_SCHEMA}`);
      outputSchema = stableEditorStateOutputSchema;
    } else {
      outputSchema = new SchemaBuilder(checker, definition, resultName, errors).build(resultType);
    }
    injections.push({ name: definition.name, inputSchema, outputSchema });
  }
  return injections.sort((a, b) => compareText(a.name, b.name));
}

function moduleContent(injections) {
  return `// <auto-generated by scripts/gen-tool-schemas.mjs />\n// 构建期从真实泛型参数生成严格 JSON Schema。禁止手改。\nimport { injectToolSchema } from "./define.js";\n\n${injections.map((item) => `injectToolSchema(${JSON.stringify(item.name)}, ${JSON.stringify(item.inputSchema)}, ${JSON.stringify(item.outputSchema)});`).join("\n")}\n`;
}

function writeModule(directory, injections) {
  mkdirSync(directory, { recursive: true });
  writeFileSync(join(directory, ".generated-schemas.ts"), moduleContent(injections), "utf8");
}

function generate() {
  if (existsSync(join(ROOT, "src", "contexts"))) throw new Error("src/contexts: Context schema input is forbidden; define Tools under src/tools instead");
  const program = loadProgram();
  for (const sourceFile of program.getSourceFiles()) {
    if (!sourceFile.isDeclarationFile && /\bdefineContext\s*(?:<|\()/.test(sourceFile.text)) {
      throw new Error(`${relative(ROOT, sourceFile.fileName)}: legacy defineContext schema input is forbidden`);
    }
  }
  const errors = [];
  const names = new Map();
  const stableEditorStateOutputSchema = existsSync(EDITOR_STATE_STABLE_SCHEMA) ? loadEditorStateOutputSchema() : undefined;
  const tools = buildInjections(program, TOOLS_DIR, "defineTool", "Tool", names, errors, stableEditorStateOutputSchema);
  if (errors.length > 0) throw new Error(`schema generation failed with ${errors.length} error(s):\n${errors.map((error) => `- ${error}`).join("\n")}`);
  writeModule(TOOLS_DIR, tools);
  console.log(`gen-tool-schemas: generated ${tools.length} tool schema(s), 0 errors`);
}

try {
  generate();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
}
