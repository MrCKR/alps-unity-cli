import type { BridgeRuntime } from "../bridge/index.js";
import type { ProjectToolRuntime } from "../project-tool-discovery.js";
import type { CliResult } from "../types.js";
import type { ContextAvailabilityScope, ContextDescriptor, ContextExample, ContextPlayModeSupport, ContextSource, ContextTarget } from "./types.js";

export interface ContextDefinition<TReq = unknown, TRes = unknown> {
  name: string;
  summary?: string;
  description: string;
  category: string;
  source?: ContextSource;
  target?: ContextTarget;
  availabilityScope?: ContextAvailabilityScope;
  playModeSupport?: ContextPlayModeSupport;
  requiresPackages?: string[];
  examples: ContextExample[];
  /** 只读读取入口。不得修改 Unity、项目文件或会话状态。 */
  read: (req: TReq, ctx: ContextReadContext) => Promise<TRes> | TRes;
}

export interface ContextReadContext extends Partial<BridgeRuntime>, ProjectToolRuntime {
  requestId: string;
  flags?: Record<string, string | boolean>;
  /** 私有 wall-clock 上限；hybrid Tool 的前置 observation 不得扩展 root deadline。 */
  observationAbsoluteDeadlineMs?: number;
}

export class StructuredContextError extends Error {
  constructor(
    public readonly code: string,
    message: string,
    public readonly nextActions: string[] = [],
    public readonly details?: Record<string, unknown>
  ) {
    super(message);
    this.name = "StructuredContextError";
  }
}

interface RegisteredContext {
  definition: ContextDefinition;
  inputSchema?: Record<string, unknown>;
  outputSchema?: Record<string, unknown>;
}

const registry = new Map<string, RegisteredContext>();

/** 声明一个 TS 自包含 Context。schema 由构建期从 Request/Result interface 注入。 */
export function defineContext<TReq, TRes>(definition: ContextDefinition<TReq, TRes>): ContextDefinition<TReq, TRes> {
  if (!definition.name) throw new Error("defineContext requires name");
  if (registry.has(definition.name)) {
    throw new Error(`duplicate defineContext registration: ${definition.name}`);
  }
  registry.set(definition.name, { definition: definition as ContextDefinition });
  return definition;
}

/** 构建期 schema 注入入口。 */
export function injectContextSchema(name: string, inputSchema: Record<string, unknown>, outputSchema?: Record<string, unknown>): void {
  const entry = registry.get(name);
  if (!entry) return;
  entry.inputSchema = inputSchema;
  entry.outputSchema = outputSchema;
}

/** 获取所有 TS defineContext descriptor,供本地 context list/describe 使用。 */
export function defineContextMetadata(): ContextDescriptor[] {
  const result: ContextDescriptor[] = [];
  for (const [, entry] of registry) {
    const d = entry.definition;
    if (!entry.inputSchema || !entry.outputSchema) {
      throw new Error(`Context schema was not generated for ${d.name}`);
    }
    result.push({
      name: d.name,
      summary: d.summary ?? d.description,
      description: d.description,
      category: d.category,
      source: d.source ?? "built-in",
      target: d.target ?? "editor",
      availabilityScope: d.availabilityScope ?? "editorSession",
      playModeSupport: d.playModeSupport ?? "supported",
      requiresPackages: d.requiresPackages,
      inputSchema: entry.inputSchema,
      outputSchema: entry.outputSchema,
      examples: d.examples
    });
  }
  return result;
}

export function findDefineContext(name: string): ContextDefinition | undefined {
  return registry.get(name)?.definition;
}

export function contextErrorFromResult(result: CliResult): StructuredContextError {
  if (result.ok) {
    return new StructuredContextError("CONTEXT_READ_FAILED", "Context read returned a success envelope.");
  }
  return new StructuredContextError(result.error.code, result.error.message, result.error.nextActions, result.error.details as Record<string, unknown> | undefined);
}
