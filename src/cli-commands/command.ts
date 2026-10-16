import { failure, success } from "../output.js";
import { checkToolExtensionAvailability, extensionIdForToolName, freezeExtensionAvailability } from "../extension-availability.js";
import { ExtensionInstallerError } from "../extensions/installer.js";
import type { CliResult } from "../types.js";
import { publicToolDescriptor, resolveCatalogIndependentOperabilityTool, resolveCatalogTool } from "../tools/catalog.js";
import { projectCatalogPage, type CatalogPageRequest } from "../tools/catalog-control.js";
import {
  beginToolInvocation,
  dispatchCatalogProjection,
  dispatchTool,
  resolveCatalogToolForInvocation,
  type ToolInvocationContext,
  type ToolsRuntime,
} from "../tools/dispatcher.js";
import type { ToolMetadata } from "../tools/metadata/index.js";

const ATTRIBUTE_PREFIX = "UnityPipeline_";
const ATTRIBUTE_GROUP = "UnityPipeline";

const HEAD_CONTROL_OPTIONS = new Map<string, "boolean" | "value">([
  ["help", "boolean"],
  ["pretty", "boolean"],
  ["instance", "value"],
  ["project-path", "value"],
  ["timeout", "value"],
  ["timeout-ms", "value"],
  ["artifact-dir", "value"],
  ["dry-run", "boolean"],
  ["page-size", "value"],
  ["cursor", "value"],
]);

const TRAILING_CONTROL_OPTIONS = new Map<string, "boolean" | "value">([
  ["help", "boolean"],
  ["pretty", "boolean"],
  ["instance", "value"],
  ["project-path", "value"],
  ["timeout-ms", "value"],
  ["artifact-dir", "value"],
  ["dry-run", "boolean"],
  ["runtime", "boolean"],
]);

type Parsed<T> = { ok: true; value: T } | { ok: false; result: CliResult };
type ParsedControl = { name: string; value: string | boolean; consumed: number };
type CommandInvocation = {
  commandName?: string;
  businessTokens: string[];
  flags: Record<string, string | boolean>;
};

/**
 * command入口只负责把官方argv投影为目标Tool的typed input。
 * catalog选择、schema终检、安全、执行与terminal envelope全部复用dispatchTool。
 */
export async function handleCommand(
  args: string[],
  initialFlags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
): Promise<CliResult> {
  const invocation = parseCommandInvocation(args, initialFlags, requestId);
  if (!invocation.ok) return invocation.result;

  const { commandName, businessTokens, flags } = invocation.value;
  if (!commandName && (flags.help === true || flags.help === "true")) {
    return success(requestId, {
      usage: "unitycli command [--project-path <path>] [--page-size <n>] [--cursor <nextCursor>] [name] [--field <value>|<positional>...]",
      inputForms: ["--field value", "--field=value", "--boolean-field", "<positional>"],
      nameResolution: {
        nativeExactFirst: true,
        attributeFallback: "UnityPipeline_<name>",
        explicitAttributeName: "UnityPipeline_<name>",
        similarNameMapping: false,
      },
      catalogDiscovery: {
        commandListing: "unitycli command --project-path <path> [--page-size <n>] [--cursor <nextCursor>]",
        toolSearch: "unitycli tools search <k1|k2|...> --instance <name|alias|projectPath>",
        toolDescribe: "unitycli tools describe <tool> --instance <name|alias|projectPath>",
      },
      note: "Omit name without --help to read bounded command summaries; continue with pageInfo.nextCursor and use command <name> --help for parameters.",
    });
  }

  if (!commandName) {
    const pageRequest = commandPageRequest(flags);
    return dispatchCatalogProjection("command.list", pageRequest as Record<string, unknown>, flags, runtime, requestId, (rootRuntime) =>
      projectCatalogPage("command.list", pageRequest, flags, rootRuntime, requestId, {
        collectionName: "commands",
        includeExtensionAvailability: true,
        projectItem: (tool) => ({
          name: tool.commandName ?? tool.name,
          summary: tool.summary,
          group: tool.group,
          ...(tool.available === false ? { available: false, unavailableReason: tool.unavailableReason } : {}),
        }),
      }, catalogDeadline(rootRuntime)));
  }

  if (flags.help === true || flags.help === "true") {
    if (resolveCatalogIndependentOperabilityTool(commandName)) return commandHelp(commandName, flags, runtime, requestId);
    return dispatchCatalogProjection("command.describe", { commandName }, flags, runtime, requestId, (rootRuntime) =>
      commandHelp(commandName, flags, rootRuntime, requestId, catalogDeadline(rootRuntime)));
  }

  // 实际执行的 discovery 与后续 admission 共享同一个顶层 deadline 和 exact listener provenance。
  const started = beginToolInvocation(flags, requestId);
  if (!started.ok) return started.result;
  const resolved = await resolveCommandCatalogTool(commandName, flags, runtime, requestId, started.context);
  if (!resolved.ok) return resolved;
  const tool = resolved.tool;
  if (!tool) return commandNotFound(requestId, commandName);

  const input = parseCommandInput(tool, businessTokens, requestId);
  if (!input.ok) return input.result;

  return dispatchTool(tool.name, undefined, flags, runtime, requestId, input.value, tool, resolved.context ?? started.context);
}

/** 分离command级控制项与业务token；选择实例的控制项在catalog加载前冻结。 */
function parseCommandInvocation(
  args: string[],
  initialFlags: Record<string, string | boolean>,
  requestId: string,
): Parsed<CommandInvocation> {
  const flags = { ...initialFlags };
  let commandName: string | undefined;
  let index = 0;

  for (; index < args.length; index += 1) {
    const token = args[index];
    if (token === "--") {
      commandName = args[index + 1];
      index += commandName === undefined ? 1 : 2;
      break;
    }
    if (!token.startsWith("--")) {
      commandName = token;
      index += 1;
      break;
    }

    const parsedControl = parseControl(args, index, HEAD_CONTROL_OPTIONS, requestId, "command");
    if (!parsedControl.ok) return parsedControl;
    const applied = applyControl(flags, parsedControl.value, requestId);
    if (!applied.ok) return applied;
    index += parsedControl.value.consumed - 1;
  }

  const trailing = extractTrailingControls(args.slice(index), flags, requestId);
  if (!trailing.ok) return trailing;
  return {
    ok: true,
    value: { commandName, businessTokens: trailing.value.businessTokens, flags },
  };
}

/** command名称之后仍允许当前CLI执行控制项；`--`之后全部保留为业务positional。 */
function extractTrailingControls(
  tokens: string[],
  flags: Record<string, string | boolean>,
  requestId: string,
): Parsed<{ businessTokens: string[] }> {
  const businessTokens: string[] = [];
  for (let index = 0; index < tokens.length; index += 1) {
    const token = tokens[index];
    if (token === "--") {
      businessTokens.push(...tokens.slice(index));
      break;
    }
    const option = splitOption(token);
    if (!option || !TRAILING_CONTROL_OPTIONS.has(option.name)) {
      businessTokens.push(token);
      continue;
    }

    const parsedControl = parseControl(tokens, index, TRAILING_CONTROL_OPTIONS, requestId, "command");
    if (!parsedControl.ok) return parsedControl;
    const applied = applyControl(flags, parsedControl.value, requestId);
    if (!applied.ok) return applied;
    index += parsedControl.value.consumed - 1;
  }
  return { ok: true, value: { businessTokens } };
}

/** 按显式arity读取一个command级控制项。 */
function parseControl(
  tokens: string[],
  index: number,
  options: ReadonlyMap<string, "boolean" | "value">,
  requestId: string,
  route: string,
): Parsed<ParsedControl> {
  const option = splitOption(tokens[index]);
  if (!option || !options.has(option.name)) {
    const rendered = option ? `--${option.name}` : tokens[index];
    return { ok: false, result: failure(requestId, "E_UNKNOWN_OPTION", `Unknown option for ${route}: ${rendered}.`, [
      "Run unitycli command --help to inspect command-level options.",
    ], { option: rendered, command: route }) };
  }

  const arity = options.get(option.name)!;
  if (arity === "boolean") {
    if (option.attached === undefined) return { ok: true, value: { name: option.name, value: true, consumed: 1 } };
    if (option.attached === "true" || option.attached === "false") {
      return { ok: true, value: { name: option.name, value: option.attached, consumed: 1 } };
    }
    return { ok: false, result: failure(requestId, "E_OPTION_VALUE_INVALID", `Boolean option --${option.name} accepts only true or false.`, [
      `Use --${option.name}, --${option.name}=true, or --${option.name}=false.`,
    ], { option: `--${option.name}`, expected: "boolean", actual: option.attached }) };
  }

  if (option.attached !== undefined) {
    if (option.attached.length > 0) return { ok: true, value: { name: option.name, value: option.attached, consumed: 1 } };
    return controlValueRequired(requestId, option.name);
  }

  const next = tokens[index + 1];
  if (next === undefined || next.startsWith("--")) return controlValueRequired(requestId, option.name, next);
  return { ok: true, value: { name: option.name, value: next, consumed: 2 } };
}

/** 将官方command wrapper字段绑定到现有selector/execution控制面。 */
function applyControl(
  flags: Record<string, string | boolean>,
  control: ParsedControl,
  requestId: string,
): Parsed<undefined> {
  if (control.name === "project-path") {
    const current = flags.instance;
    if (current !== undefined && current !== control.value) {
      return { ok: false, result: failure(requestId, "E_INSTANCE_CONFLICT", "--project-path and --instance select different Unity projects.", [
        "Pass exactly one project selector, or make both selectors identify the same canonical project.",
      ], { instance: current, projectPath: control.value }) };
    }
    flags.instance = control.value;
    return { ok: true, value: undefined };
  }

  if (control.name === "timeout") {
    const seconds = Number(control.value);
    if (!Number.isFinite(seconds) || seconds <= 0) {
      return { ok: false, result: failure(requestId, "E_OPTION_VALUE_INVALID", "--timeout must be a positive number of seconds.", [
        "Pass --timeout <seconds> before the command name.",
      ], { option: "--timeout", expected: "positive-number", actual: control.value }) };
    }
    const timeoutMs = String(Math.ceil(seconds * 1000));
    if (flags["timeout-ms"] !== undefined && flags["timeout-ms"] !== timeoutMs) {
      return { ok: false, result: failure(requestId, "E_OPTION_VALUE_INVALID", "--timeout and --timeout-ms specify different deadlines.", [
        "Pass only one timeout option.",
      ], { timeout: control.value, timeoutMs: flags["timeout-ms"] }) };
    }
    flags["timeout-ms"] = timeoutMs;
    return { ok: true, value: undefined };
  }

  flags[control.name] = control.value;
  return { ok: true, value: undefined };
}

/** 按目标Tool的live inputSchema解析named、bare boolean与声明顺序positional。 */
function parseCommandInput(tool: ToolMetadata, tokens: string[], requestId: string): Parsed<Record<string, unknown>> {
  const rootSchema = tool.inputSchema;
  const properties = schemaRecord(rootSchema.properties);
  const order = commandParameterOrder(rootSchema, properties);
  const input: Record<string, unknown> = {};
  let optionsEnabled = true;
  let positionalIndex = 0;

  for (let index = 0; index < tokens.length; index += 1) {
    const token = tokens[index];
    if (optionsEnabled && token === "--") {
      optionsEnabled = false;
      continue;
    }

    const option = optionsEnabled ? splitOption(token) : undefined;
    if (option) {
      if (!Object.hasOwn(properties, option.name)) {
        return { ok: false, result: failure(requestId, "E_UNKNOWN_OPTION", `Unknown option for command ${tool.name}: --${option.name}.`, [
          `Run unitycli command ${tool.name} --help to inspect its input schema.`,
        ], { option: `--${option.name}`, toolName: tool.name }) };
      }
      if (Object.hasOwn(input, option.name)) return duplicateInput(requestId, tool.name, option.name);

      const propertySchema = properties[option.name];
      let rawValue = option.attached;
      let value: unknown;
      if (rawValue === undefined && acceptsBoolean(propertySchema, rootSchema)) {
        const next = tokens[index + 1];
        if (next === "true" || next === "false") {
          rawValue = next;
          index += 1;
        } else {
          value = true;
        }
      } else if (rawValue === undefined) {
        const next = tokens[index + 1];
        if (next === undefined || (optionsEnabled && next.startsWith("--"))) {
          return optionValueRequired(requestId, tool.name, option.name, next);
        }
        const collected = collectCommandValue(tokens, index + 1, propertySchema, rootSchema);
        rawValue = collected.value;
        index = collected.nextIndex;
      }

      if (value === undefined) {
        const coerced = coerceInputValue(rawValue ?? "", propertySchema, rootSchema, requestId, tool.name, option.name);
        if (!coerced.ok) return coerced;
        value = coerced.value;
      }
      input[option.name] = value;
      continue;
    }

    while (positionalIndex < order.length && Object.hasOwn(input, order[positionalIndex])) positionalIndex += 1;
    const propertyName = order[positionalIndex];
    if (!propertyName) {
      return { ok: false, result: failure(requestId, "E_SCHEMA_INVALID", `Command ${tool.name} received more positional arguments than its schema declares.`, [
        `Run unitycli command ${tool.name} --help and remove the extra positional value.`,
      ], { path: "$", expected: order, actual: token }) };
    }
    const coerced = coerceInputValue(token, properties[propertyName], rootSchema, requestId, tool.name, propertyName);
    if (!coerced.ok) return coerced;
    input[propertyName] = coerced.value;
    positionalIndex += 1;
  }

  return { ok: true, value: input };
}

/** official importer按MethodInfo参数顺序插入properties；JSON解析后保留该live声明顺序。 */
function commandParameterOrder(_rootSchema: Record<string, unknown>, properties: Record<string, unknown>): string[] {
  return Object.keys(properties);
}

/** 复杂JSON值在PowerShell拆成多个token时收集到首个可解析边界。 */
function collectCommandValue(
  tokens: string[],
  valueIndex: number,
  propertySchema: unknown,
  rootSchema: Record<string, unknown>,
): { value: string; nextIndex: number } {
  const first = tokens[valueIndex];
  if (!expectsStructuredJson(propertySchema, rootSchema) || !/^[\[{]/.test(first.trimStart())) {
    return { value: first, nextIndex: valueIndex };
  }

  const parts: string[] = [];
  for (let index = valueIndex; index < tokens.length; index += 1) {
    const token = tokens[index];
    if (index > valueIndex && token.startsWith("--")) break;
    parts.push(token);
    const candidate = parts.join(" ");
    try {
      JSON.parse(candidate);
      return { value: candidate, nextIndex: index };
    } catch {
      // 继续读取同一个结构化JSON值。
    }
  }
  return { value: parts.join(" "), nextIndex: valueIndex + Math.max(0, parts.length - 1) };
}

/** 按schema primitive/structured/AnyJSON语义转换单个argv值。 */
function coerceInputValue(
  raw: string,
  propertySchema: unknown,
  rootSchema: Record<string, unknown>,
  requestId: string,
  toolName: string,
  propertyName: string,
): Parsed<unknown> {
  try {
    return { ok: true, value: coerceSchemaValue(raw, propertySchema, rootSchema) };
  } catch (error) {
    return { ok: false, result: failure(requestId, "E_SCHEMA_INVALID", `Command input ${propertyName} is invalid: ${error instanceof Error ? error.message : String(error)}.`, [
      `Run unitycli command ${toolName} --help to inspect the field schema.`,
    ], { path: `$.${propertyName}`, expected: propertySchema, actual: raw }) };
  }
}

/** 递归处理$ref与oneOf，最终schema验证仍由统一dispatcher执行。 */
function coerceSchemaValue(raw: string, schema: unknown, rootSchema: Record<string, unknown>): unknown {
  const resolved = resolveSchema(schema, rootSchema);
  if (resolved["x-unityCliAnyJson"] === true) return parseJson(raw, "valid JSON");

  const oneOf = Array.isArray(resolved.oneOf) ? resolved.oneOf : undefined;
  if (oneOf) {
    const candidates = raw === "null"
      ? [...oneOf].sort((left, right) => schemaType(left, rootSchema) === "null" ? -1 : schemaType(right, rootSchema) === "null" ? 1 : 0)
      : oneOf;
    for (const candidate of candidates) {
      try {
        return coerceSchemaValue(raw, candidate, rootSchema);
      } catch {
        // 尝试下一个合法分支。
      }
    }
    throw new Error("value does not match any allowed schema branch");
  }

  const type = schemaType(resolved, rootSchema);
  if (type === "string") return raw;
  if (type === "boolean") {
    if (raw === "true") return true;
    if (raw === "false") return false;
    throw new Error("expected true or false");
  }
  if (type === "integer") {
    const value = Number(raw);
    if (!Number.isSafeInteger(value)) throw new Error("expected a safe integer");
    return value;
  }
  if (type === "number") {
    const value = Number(raw);
    if (!Number.isFinite(value)) throw new Error("expected a finite number");
    return value;
  }
  if (type === "object") {
    const value = parseJson(raw, "a JSON object");
    if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("expected a JSON object");
    return value;
  }
  if (type === "array") {
    const value = parseJson(raw, "a JSON array");
    if (!Array.isArray(value)) throw new Error("expected a JSON array");
    return value;
  }
  if (type === "null") {
    if (raw !== "null") throw new Error("expected null");
    return null;
  }

  if (Array.isArray(resolved.enum) && resolved.enum.every((value) => typeof value === "string")) return raw;
  try {
    return JSON.parse(raw) as unknown;
  } catch {
    return raw;
  }
}

/** 判断`--flag`是否可在没有值token时投影为true。 */
function acceptsBoolean(schema: unknown, rootSchema: Record<string, unknown>): boolean {
  const resolved = resolveSchema(schema, rootSchema);
  if (schemaType(resolved, rootSchema) === "boolean") return true;
  return Array.isArray(resolved.oneOf) && resolved.oneOf.some((candidate) => acceptsBoolean(candidate, rootSchema));
}

/** 判断是否需要收集被PowerShell拆分的object/array/AnyJSON。 */
function expectsStructuredJson(schema: unknown, rootSchema: Record<string, unknown>): boolean {
  const resolved = resolveSchema(schema, rootSchema);
  if (resolved["x-unityCliAnyJson"] === true) return true;
  const type = schemaType(resolved, rootSchema);
  if (type === "object" || type === "array") return true;
  return Array.isArray(resolved.oneOf) && resolved.oneOf.some((candidate) => expectsStructuredJson(candidate, rootSchema));
}

/** 解析本地JSON Pointer `$ref`，禁止把无法解析的引用猜成其他类型。 */
function resolveSchema(schema: unknown, rootSchema: Record<string, unknown>): Record<string, unknown> {
  let current = schemaRecord(schema);
  const visited = new Set<string>();
  while (typeof current.$ref === "string") {
    const reference = current.$ref;
    if (!reference.startsWith("#/")) throw new Error(`unsupported schema reference ${reference}`);
    if (visited.has(reference)) throw new Error(`cyclic schema reference ${reference}`);
    visited.add(reference);
    let value: unknown = rootSchema;
    for (const rawSegment of reference.slice(2).split("/")) {
      const segment = rawSegment.replace(/~1/g, "/").replace(/~0/g, "~");
      value = schemaRecord(value)[segment];
    }
    current = schemaRecord(value);
  }
  return current;
}

/** 返回schema的单一type；union由oneOf负责。 */
function schemaType(schema: unknown, rootSchema: Record<string, unknown>): string | undefined {
  const type = resolveSchema(schema, rootSchema).type;
  return typeof type === "string" ? type : undefined;
}

/** command 在每个 target 的一次 exact describe 中保持 native 优先，再尝试官方 attribute 名称。 */
async function resolveCommandCatalogTool(
  name: string,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  invocationContext?: ToolInvocationContext,
  absoluteDeadlineMs?: number,
): Promise<{ ok: true; tool?: ToolMetadata; context?: ToolInvocationContext } | (CliResult & { ok: false })> {
  const operability = resolveCatalogIndependentOperabilityTool(name);
  if (operability) return { ok: true, tool: operability, context: invocationContext };
  const fallbackName = name.startsWith(ATTRIBUTE_PREFIX) ? undefined : `${ATTRIBUTE_PREFIX}${name}`;
  if (invocationContext) {
    const resolved = await resolveCatalogToolForInvocation(name, flags, runtime, requestId, invocationContext, fallbackName);
    if (!resolved.ok || !resolved.tool || resolved.tool.name === name) return resolved;
    if (resolved.tool.name === fallbackName && isOfficialAttributeTool(resolved.tool)) return resolved;
    return { ok: true, context: resolved.context };
  }
  const resolved = await resolveCatalogTool(name, flags, runtime, requestId, absoluteDeadlineMs, fallbackName);
  if (!resolved.ok || !resolved.tool || resolved.tool.name === name) return resolved;
  if (resolved.tool.name === fallbackName && isOfficialAttributeTool(resolved.tool)) return resolved;
  return { ok: true };
}

async function commandHelp(
  commandName: string,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  absoluteDeadlineMs?: number,
): Promise<CliResult> {
  const resolved = await resolveCommandCatalogTool(commandName, flags, runtime, requestId, undefined, absoluteDeadlineMs);
  if (!resolved.ok) return resolved;
  if (!resolved.tool) return commandNotFound(requestId, commandName);
  const descriptor = commandDescriptor(resolved.tool, commandName);
  const published = publicToolDescriptor(resolved.tool);
  if (extensionIdForToolName(resolved.tool.name)) {
    try {
      const snapshot = await freezeExtensionAvailability(flags, runtime);
      const availability = await checkToolExtensionAvailability(resolved.tool, snapshot, absoluteDeadlineMs);
      if (!availability.available) {
        published.available = false;
        published.unavailableReason = `${availability.errorCode}: ${availability.message}`;
      }
    } catch (error) {
      if (error instanceof ExtensionInstallerError) return failure(requestId, error.code, error.message, error.nextActions);
      throw error;
    }
  }
  return success(requestId, {
    usage: `unitycli command ${commandName} [business-args]`,
    command: { name: descriptor.name, summary: descriptor.summary, parameters: descriptor.parameters },
    tool: published,
  });
}

function commandNotFound(requestId: string, commandName: string): CliResult {
  return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", `Command not found: ${commandName}.`, [
    "Run unitycli command to list commands registered by the selected Unity instance.",
    "Run unitycli tools search <k1|k2|...> to find related Tools.",
  ], { commandName });
}

function commandPageRequest(flags: Record<string, string | boolean>): CatalogPageRequest {
  return {
    ...(flags["page-size"] !== undefined ? { pageSize: Number(flags["page-size"]) } : {}),
    ...(typeof flags.cursor === "string" ? { cursor: flags.cursor } : {}),
  };
}

function catalogDeadline(runtime: ToolsRuntime): number | undefined {
  return runtime.rootAdmissions?.editor?.executionAbsoluteDeadlineMs
    ?? runtime.rootAdmissions?.runtime?.executionAbsoluteDeadlineMs
    ?? runtime.rootAdmission?.executionAbsoluteDeadlineMs;
}

/** 构造稳定command descriptor，并保留其唯一公开Tool身份。 */
function commandDescriptor(tool: ToolMetadata, commandName: string): Record<string, unknown> {
  const properties = schemaRecord(tool.inputSchema.properties);
  return {
    name: commandName,
    summary: tool.summary,
    group: tool.group,
    parameters: commandParameterOrder(tool.inputSchema, properties),
    available: tool.available ?? true,
    ...((tool.available ?? true) ? {} : { unavailableReason: tool.unavailableReason }),
  };
}

/** 识别已经隔离到保留namespace的official attribute Tool。 */
function isOfficialAttributeTool(tool: ToolMetadata): boolean {
  return tool.contractProfile === "official-attribute"
    && tool.group === ATTRIBUTE_GROUP
    && tool.name.startsWith(ATTRIBUTE_PREFIX)
    && tool.name.length > ATTRIBUTE_PREFIX.length;
}

/** 拆分`--name`与`--name=value`，业务字段名保持exact。 */
function splitOption(token: string): { name: string; attached?: string } | undefined {
  if (!token.startsWith("--") || token === "--") return undefined;
  const value = token.slice(2);
  const equalsIndex = value.indexOf("=");
  return equalsIndex < 0
    ? { name: value }
    : { name: value.slice(0, equalsIndex), attached: value.slice(equalsIndex + 1) };
}

/** 转换任意unknown为schema record。 */
function schemaRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

/** 解析结构化JSON并保留可执行错误原因。 */
function parseJson(value: string, expected: string): unknown {
  try {
    return JSON.parse(value) as unknown;
  } catch {
    throw new Error(`expected ${expected}`);
  }
}

/** command级控制项缺值。 */
function controlValueRequired(requestId: string, name: string, actual?: string): Parsed<never> {
  return { ok: false, result: failure(requestId, "E_OPTION_VALUE_REQUIRED", `Option --${name} requires a value.`, [
    `Pass --${name}=<value> or --${name} <value>.`,
  ], { option: `--${name}`, expected: "value", ...(actual ? { actual } : {}) }) };
}

/** 业务字段缺值。 */
function optionValueRequired(requestId: string, toolName: string, name: string, actual?: string): Parsed<never> {
  return { ok: false, result: failure(requestId, "E_OPTION_VALUE_REQUIRED", `Command field --${name} requires a value.`, [
    `Run unitycli command ${toolName} --help to inspect the field schema.`,
  ], { option: `--${name}`, expected: "value", ...(actual ? { actual } : {}) }) };
}

/** 重复业务字段在effect前失败，禁止last-write-wins。 */
function duplicateInput(requestId: string, toolName: string, name: string): Parsed<never> {
  return { ok: false, result: failure(requestId, "E_SCHEMA_INVALID", `Command field ${name} was provided more than once.`, [
    `Pass ${name} exactly once for command ${toolName}.`,
  ], { path: `$.${name}`, expected: "single-value", actual: "duplicate" }) };
}
