import { executeBridgeCommand, type BridgeRuntime } from "./bridge/index.js";
import { inferPlayModeSupport, normalizeToolMetadata, type PlayModeSupport, type ResolvedToolMetadata, type ToolContractProfile, type ToolMetadata } from "./tools/metadata/index.js";
import { failure } from "./output.js";
import type { CliResult } from "./types.js";
import type { AnyJson } from "./schema/any-json.js";
import { validateSchema } from "./schema/validator.js";
import { isRegisteredToolName } from "./tool-name.js";

export interface ProjectToolRuntime extends Partial<BridgeRuntime> {
  projectTools?: ToolMetadata[];
  runtimeTools?: ToolMetadata[];
}

export type ProjectToolDiscovery = { ok: true; tools: ResolvedToolMetadata[]; mode: "runtime" | "bridge" } | (CliResult & { ok: false });

const LIVE_DESCRIPTOR_FIELDS = new Set([
  "name", "summary", "description", "group", "source", "inputSchema", "outputSchema",
  "destructiveHint", "sideEffects", "supportsDryRun", "permissions", "requiresPackages", "examples",
  "target", "availabilityScope", "playModeSupport", "contractProfile", "defaultTimeoutMs", "timeoutMs",
  "completionContract", "available", "unavailableReason",
]);

/** 当前实例是 Tool descriptor 的唯一公开真源；调用 discovery 必须实际读取该实例。 */
export async function discoverProjectTools(
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string,
  options: { requireBridge?: boolean; absoluteDeadlineMs?: number } = {}
): Promise<ProjectToolDiscovery> {
  if (runtime.projectTools) {
    return { ok: true, tools: runtime.projectTools.map(ensureProjectToolDefaults), mode: "runtime" };
  }

  const result = await executeBridgeCommand(requestId, "catalog.editor.list", flags, undefined, runtime, { mode: "read", mutation: false, absoluteDeadlineMs: options.absoluteDeadlineMs });
  if (result.ok === false) {
    return result as CliResult & { ok: false };
  }

  const rawTools = (result.data as { tools?: unknown } | undefined)?.tools;
  return parseBridgeToolCatalog(rawTools, requestId, "catalog.editor.list");
}

export function projectDescriptorToToolMetadata(value: unknown): ResolvedToolMetadata | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }
  const descriptor = value as Record<string, unknown>;
  if (Object.keys(descriptor).some((field) => !LIVE_DESCRIPTOR_FIELDS.has(field))) return undefined;
  const name = stringProp(descriptor, "name");
  const contractProfile = toolContractProfileProp(descriptor, "contractProfile");
  if (!contractProfile || !isRegisteredToolName(name, contractProfile)) {
    return undefined;
  }
  const summary = stringProp(descriptor, "summary");
  const description = stringProp(descriptor, "description");
  const group = stringProp(descriptor, "group");
  const inputSchema = objectProp(descriptor, "inputSchema");
  const outputSchema = objectProp(descriptor, "outputSchema");
  const destructiveHint = booleanProp(descriptor, "destructiveHint");
  const supportsDryRun = booleanProp(descriptor, "supportsDryRun");
  const playModeSupport = playModeSupportProp(descriptor, "playModeSupport");
  const target = toolTargetProp(descriptor, "target");
  const availabilityScope = availabilityScopeProp(descriptor, "availabilityScope");
  const completionContract = completionContractProp(descriptor, "completionContract");
  const examples = inputSchema ? toolExamplesProp(descriptor, "examples", name, inputSchema) : undefined;
  if (!summary || !description || !group || !inputSchema || !outputSchema || destructiveHint === undefined
      || supportsDryRun === undefined || !playModeSupport || !target || !availabilityScope || !completionContract || !contractProfile || !examples
      || officialAttributeContractInvalidFields(descriptor).length > 0) return undefined;
  return ensureProjectToolDefaults({
    name,
    summary,
    description,
    group,
    inputSchema,
    outputSchema,
    destructiveHint,
    sideEffects: stringArrayProp(descriptor, "sideEffects"),
    supportsDryRun,
    playModeSupport,
    timeoutMs: numberProp(descriptor, "defaultTimeoutMs") ?? numberProp(descriptor, "timeoutMs"),
    permissions: stringArrayProp(descriptor, "permissions"),
    requiresPackages: stringArrayProp(descriptor, "requiresPackages"),
    examples,
    target,
    availabilityScope,
    available: booleanProp(descriptor, "available") ?? undefined,
    unavailableReason: stringProp(descriptor, "unavailableReason"),
    source: toolSourceProp(descriptor, "source"),
    contractProfile,
    completionContract
  });
}

/** 返回 descriptor 公开字段级诊断，不回显 schema/example/内部值。 */
function projectDescriptorInvalidFields(value: unknown): string[] {
  if (!value || typeof value !== "object" || Array.isArray(value)) return ["descriptor"];
  const descriptor = value as Record<string, unknown>;
  const name = stringProp(descriptor, "name");
  const contractProfile = toolContractProfileProp(descriptor, "contractProfile");
  const inputSchema = objectProp(descriptor, "inputSchema");
  const unknownFields = Object.keys(descriptor).filter((field) => !LIVE_DESCRIPTOR_FIELDS.has(field)).map((field) => `unknown:${field}`);
  const checks: Array<[string, boolean]> = [
    ["name", !!contractProfile && isRegisteredToolName(name, contractProfile)], ["summary", !!stringProp(descriptor, "summary")], ["description", !!stringProp(descriptor, "description")],
    ["group", !!stringProp(descriptor, "group")], ["inputSchema", !!inputSchema], ["outputSchema", !!objectProp(descriptor, "outputSchema")],
    ["destructiveHint", booleanProp(descriptor, "destructiveHint") !== undefined], ["supportsDryRun", booleanProp(descriptor, "supportsDryRun") !== undefined],
    ["playModeSupport", !!playModeSupportProp(descriptor, "playModeSupport")], ["target", !!toolTargetProp(descriptor, "target")],
    ["availabilityScope", !!availabilityScopeProp(descriptor, "availabilityScope")], ["completionContract", !!completionContractProp(descriptor, "completionContract")],
    ["contractProfile", !!contractProfile],
    ["examples", !!(name && inputSchema && toolExamplesProp(descriptor, "examples", name, inputSchema))],
  ];
  return [...new Set([...unknownFields, ...checks.filter(([, valid]) => !valid).map(([field]) => field), ...officialAttributeContractInvalidFields(descriptor)])];
}

function ensureProjectToolDefaults(tool: ToolMetadata): ResolvedToolMetadata {
  const target = tool.target ?? "editor";
  // target=runtime 是公开契约不变量，内部路由必须按 runtime 目标归一化。
  const handler = target === "runtime" ? "bridge-runtime" : tool.handler ?? "bridge";
  const supportStatus = target === "runtime" ? "live-bridge-runtime" : tool.supportStatus;
  const source = tool.source === "built-in" || tool.source === "extension" || tool.source === "project" ? tool.source : "project";
  return normalizeToolMetadata({
    ...tool,
    handler,
    supportStatus,
    target,
    playModeSupport: tool.playModeSupport ?? inferPlayModeSupport(tool)
  }, source);
}

function stringProp(record: Record<string, unknown>, key: string): string | undefined {
  const value = record[key];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function booleanProp(record: Record<string, unknown>, key: string): boolean | undefined {
  return typeof record[key] === "boolean" ? record[key] : undefined;
}

function numberProp(record: Record<string, unknown>, key: string): number | undefined {
  const value = record[key];
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function playModeSupportProp(record: Record<string, unknown>, key: string): PlayModeSupport | undefined {
  const value = record[key];
  return value === "supported" || value === "runtime-only" || value === "session-affecting" || value === "editor-only" || value === "unknown" ? value : undefined;
}

function toolTargetProp(record: Record<string, unknown>, key: string): ToolMetadata["target"] | undefined {
  const value = record[key];
  return value === "editor" || value === "runtime" || value === "both" ? value : undefined;
}

function availabilityScopeProp(record: Record<string, unknown>, key: string): string | undefined {
  const value = record[key];
  return value === "always" || value === "projectFiles" || value === "editorSession" || value === "playModeSession" ? value : undefined;
}

function toolContractProfileProp(record: Record<string, unknown>, key: string): ToolContractProfile | undefined {
  const value = record[key];
  return value === "native" || value === "official-attribute" ? value : undefined;
}

/** official-attribute 是 sealed 保守档位，live descriptor 不得弱化任一风险或完成字段。 */
function officialAttributeContractInvalidFields(descriptor: Record<string, unknown>): string[] {
  if (descriptor.contractProfile !== "official-attribute") return [];
  const invalid = new Set<string>();
  const sideEffects = stringArrayProp(descriptor, "sideEffects");
  const permissions = stringArrayProp(descriptor, "permissions");
  const requiresPackages = stringArrayProp(descriptor, "requiresPackages");
  const completion = completionContractProp(descriptor, "completionContract");
  const playModeSupport = playModeSupportProp(descriptor, "playModeSupport");
  const availabilityScope = availabilityScopeProp(descriptor, "availabilityScope");
  if (!stringProp(descriptor, "name")?.startsWith("UnityPipeline_")) invalid.add("name");
  if (descriptor.group !== "UnityPipeline") invalid.add("group");
  if (descriptor.destructiveHint !== true) invalid.add("destructiveHint");
  if (descriptor.supportsDryRun !== false) invalid.add("supportsDryRun");
  if (!sideEffects || sideEffects.length !== 1 || sideEffects[0] !== "arbitrary-method-invocation") invalid.add("sideEffects");
  if (!permissions || permissions.length !== 1 || permissions[0] !== "execute-user-code") invalid.add("permissions");
  if ("requiresPackages" in descriptor && (!requiresPackages || requiresPackages.length !== 0)) invalid.add("requiresPackages");
  if (descriptor.source !== "project") invalid.add("source");
  if (descriptor.target !== "editor") invalid.add("target");
  if (!(playModeSupport === "unknown" || playModeSupport === "runtime-only")) invalid.add("playModeSupport");
  if ((playModeSupport === "runtime-only" && availabilityScope !== "playModeSession")
      || (playModeSupport === "unknown" && availabilityScope !== "editorSession")) invalid.add("availabilityScope");
  if (!completion
      || completion.executionClass !== "external"
      || completion.journalPolicy !== "required"
      || completion.mutation !== true
      || completion.completionPredicate !== "attributed-method-return-v1"
      || completion.proofSource !== "official-attributed-method-return"
      || completion.reloadPolicy !== "outcome-unknown-after-dispatch"
      || completion.timeoutError !== "E_EXECUTION_TIMEOUT"
      || completion.outcomeUnknownPolicy !== "method-may-still-be-running"
      || completion.completionDriverId !== "official-method-return-v1") invalid.add("completionContract");
  return [...invalid];
}

function completionContractProp(record: Record<string, unknown>, key: string): ToolMetadata["completionContract"] {
  const value = objectProp(record, key);
  if (!value) return undefined;
  const executionClass = stringProp(value, "executionClass");
  const journalPolicy = stringProp(value, "journalPolicy");
  const mutation = booleanProp(value, "mutation");
  const completionPredicate = stringProp(value, "completionPredicate");
  const proofSource = stringProp(value, "proofSource");
  const reloadPolicy = stringProp(value, "reloadPolicy");
  const timeoutError = stringProp(value, "timeoutError");
  const outcomeUnknownPolicy = stringProp(value, "outcomeUnknownPolicy");
  const completionDriverId = stringProp(value, "completionDriverId");
  if (!(executionClass === "atomic" || executionClass === "observed-state" || executionClass === "resumable" || executionClass === "external")
      || !(journalPolicy === "required" || journalPolicy === "none")
      || mutation === undefined || !completionPredicate || !proofSource || !reloadPolicy || !timeoutError || !outcomeUnknownPolicy || !completionDriverId) return undefined;
  return { executionClass, journalPolicy, mutation, completionPredicate, proofSource, reloadPolicy, timeoutError, outcomeUnknownPolicy, completionDriverId };
}

function objectProp(record: Record<string, unknown>, key: string): Record<string, unknown> | undefined {
  const value = record[key];
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function stringArrayProp(record: Record<string, unknown>, key: string): string[] | undefined {
  const value = record[key];
  return Array.isArray(value) && value.every((item): item is string => typeof item === "string") ? value : undefined;
}

/** 保留 live authoring 信息，并验证公开 argv 可直接调用同一个 Tool。 */
function toolExamplesProp(record: Record<string, unknown>, key: string, toolName: string, inputSchema: Record<string, unknown>): ToolMetadata["examples"] {
  const value = record[key];
  if (!Array.isArray(value) || value.length === 0) return undefined;
  const examples = value.flatMap((item) => {
    if (!item || typeof item !== "object" || Array.isArray(item)) return [];
    const example = item as Record<string, unknown>;
    const description = stringProp(example, "description");
    if (!description) return [];
    const authoredInput = objectProp(example, "input") as AnyJson | undefined;
    const explicitArgv = stringArrayProp(example, "argv");
    const execution = objectProp(example, "execution");
    const dryRun = execution?.dryRun === true;
    const timeoutMs = numberProp(execution ?? {}, "timeoutMs");
    const argv = explicitArgv ?? ["tools", "run", toolName, "--input", JSON.stringify(authoredInput ?? {}), ...(dryRun ? ["--dry-run"] : []), ...(timeoutMs !== undefined ? ["--timeout-ms", String(timeoutMs)] : [])];
    if (argv[0] !== "tools" || argv[1] !== "run" || argv[2] !== toolName) return [];
    const inputIndex = argv.indexOf("--input");
    try {
      const input = inputIndex >= 0 ? JSON.parse(argv[inputIndex + 1] ?? "") : authoredInput ?? {};
      if (!input || typeof input !== "object" || Array.isArray(input) || validateSchema(inputSchema, input).length > 0) return [];
    } catch {
      return [];
    }
    const output = stringProp(example, "output");
    return [{ description, argv, ...(authoredInput ? { input: authoredInput } : {}), ...((dryRun || timeoutMs !== undefined) ? { execution: { ...(dryRun ? { dryRun: true } : {}), ...(timeoutMs !== undefined ? { timeoutMs } : {}) } } : {}), ...(output ? { output } : {}) }];
  });
  return examples.length === value.length ? examples : undefined;
}

function toolSourceProp(record: Record<string, unknown>, key: string): ToolMetadata["source"] | undefined {
  const value = record[key];
  return value === "built-in" || value === "extension" || value === "project" ? value : undefined;
}

export function groupFromName(name: string): string {
  const [group] = name.split(".");
  return group || "custom";
}

/** catalog.player.list 只补充当前 PlayMode availability；不会形成第二份公开目录。 */
export async function discoverRuntimeTools(
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string,
  options: { requireBridge?: boolean; absoluteDeadlineMs?: number } = {}
): Promise<ProjectToolDiscovery> {
  if (runtime.runtimeTools ?? runtime.projectTools) {
    const runtimeTools = (runtime.runtimeTools ?? runtime.projectTools ?? [])
      .map(ensureProjectToolDefaults)
      .filter((tool) => tool.target === "runtime" || tool.target === "both");
    return { ok: true, tools: runtimeTools, mode: "runtime" };
  }

  const result = await executeBridgeCommand(requestId, "catalog.player.list", flags, undefined, runtime, { mode: "read", mutation: false, absoluteDeadlineMs: options.absoluteDeadlineMs });
  if (result.ok === false) {
    return result as CliResult & { ok: false };
  }

  const rawTools = (result.data as { tools?: unknown } | undefined)?.tools;
  return parseBridgeToolCatalog(rawTools, requestId, "catalog.player.list");
}

/** Bridge catalog 必须整体可解析且名称唯一，禁止静默返回部分能力。 */
function parseBridgeToolCatalog(rawTools: unknown, requestId: string, command: string): ProjectToolDiscovery {
  if (!Array.isArray(rawTools)) return unhealthyToolCatalog(requestId, `${command} response must contain a tools array.`);
  const tools: ResolvedToolMetadata[] = [];
  for (let index = 0; index < rawTools.length; index += 1) {
    const parsed = projectDescriptorToToolMetadata(rawTools[index]);
    if (!parsed) {
      const name = rawTools[index] && typeof rawTools[index] === "object" && !Array.isArray(rawTools[index])
        ? (rawTools[index] as Record<string, unknown>).name
        : undefined;
      return unhealthyToolCatalog(requestId, `${command} tools[${index}] is not a complete Tool descriptor.`, {
        index,
        ...(typeof name === "string" ? { name } : {}),
        invalidFields: projectDescriptorInvalidFields(rawTools[index]),
      });
    }
    tools.push(parsed);
  }
  const duplicates = [...new Set(tools.map((tool) => tool.name).filter((name, index, names) => names.indexOf(name) !== index))].sort();
  if (duplicates.length > 0) return unhealthyToolCatalog(requestId, `${command} contains duplicate Tool registrations: ${duplicates.join(", ")}.`, { duplicates });
  return { ok: true, tools, mode: "bridge" };
}

/** 返回稳定 registry 健康错误，要求修复实例 owner 后重新发现。 */
function unhealthyToolCatalog(requestId: string, message: string, details?: Record<string, unknown>): CliResult & { ok: false } {
  return failure(requestId, "E_TOOL_REGISTRY_UNHEALTHY", message, ["Fix the Tool descriptor registry in the selected Unity instance, then retry discovery."], details) as CliResult & { ok: false };
}
