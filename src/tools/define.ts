/**
 * TS Tool 声明工厂与运行时注册表。
 * 一个能力文件默认导出一个 defineTool；公开 schema 由构建期从 Request/Result 类型生成并注入。
 */

import type { BridgeRuntime } from "../bridge/index.js";
import { isRegisteredToolName, MAX_TOOL_NAME_UTF8_BYTES } from "../tool-name.js";
import type { ToolMetadata, PlayModeSupport, ToolHandler } from "./metadata/types.js";

// ─── 工具定义输入(defineTool 接收的形态)────────────────────────────────

/** defineTool 的输入。schema 不在此处，由构建期从 Request/Result 类型生成注入。 */
export interface ToolDefinition<TReq = unknown, TRes = unknown> {
  name: string;
  summary: string;
  description: string;
  group: string;
  destructiveHint: boolean;
  /** 副作用类别,如 ['file-write','scene-modify']。 */
  sideEffects?: string[];
  playModeSupport?: PlayModeSupport;
  supportsDryRun?: boolean;
  timeoutMs?: number;
  permissions?: string[];
  /** 机制细节、配额与恢复流程；默认不进入公开 describe，只能经 --fields notes 读取。 */
  notes?: string[];
  /** 执行路由:bridge/bridge-runtime/offline/hybrid。 */
  handler?: ToolHandler;
  supportStatus?: import("./metadata/types.js").ToolSupportStatus;
  target?: string;
  requiresPackages?: string[];
  examples?: import("./metadata/types.js").ToolExample[];
  /** bridge-runtime 定义仍提供 fail-closed guard；dispatcher 按 handler 选择唯一 Bridge executor。 */
  execute: (req: TReq, ctx: ToolContext) => Promise<TRes> | TRes;
}

/** 工具执行上下文（运行时由框架注入）。 */
export interface ToolContext {
  requestId: string;
  projectPath: string;
  /** dispatcher 入口冻结的绝对截止时间；本地前置工作与 Bridge 等待必须共同消费。 */
  absoluteDeadlineMs?: number;
  /** 命令层执行控制，不属于 Tool Request 或公开业务 schema。 */
  execution: {
    dryRun?: boolean;
    timeoutMs?: number;
  };
  /** CLI data 目录,用于离线 cache。 */
  dataDir?: string;
  /** TS mutation 在全部前置校验后、首个业务 effect 前调用；dispatcher 注入 durable barrier。 */
  commitEffect?: () => Promise<void>;
  /** 当前 direct TS mutation 是否由 local root journal 托管；仅用于派生内部 child requestId。 */
  localJournalActive?: boolean;
  /** artifact 目录(可写)。 */
  artifactDir: string;
  /** bridge 日志路径。 */
  logPath?: string;
  /** tools run 合并后的原始 flags,供 action 型工具执行内部确认与 dry-run 判定。 */
  flags?: Record<string, string | boolean>;
  /** 测试或宿主注入的实例 Tool descriptor。 */
  projectTools?: ToolMetadata[];
  /** 同一公开根调用共享的私有准入lease；所有Bridge子请求必须显式复用。 */
  rootAdmission?: BridgeRuntime["rootAdmission"];
  /** 统一catalog公开根分别复用Editor/Runtime准入lease。 */
  rootAdmissions?: BridgeRuntime["rootAdmissions"];
  /** 可选 live Bridge 执行器，仅供 defineTool 的隔离测试 transport。 */
  bridgeExecute?: BridgeRuntime["bridgeExecute"];
  /** 测试或宿主注入的文档抓取器,用于 docs online fallback,避免工具内部直接绑定全局网络。 */
  fetch?: (url: string) => Promise<{ ok: boolean; status: number; text: () => Promise<string> }>;
}

// ─── registry(收集 defineTool 注册的工具)──────────────────────────────

/** 已注册的 TS defineTool 工具(不含 schema,schema 由构建期注入)。 */
interface RegisteredTool {
  definition: ToolDefinition;
  /** 构建期生成的 inputSchema(由 gen-tool-schemas.mjs 注入),运行时 describe 用。 */
  inputSchema?: Record<string, unknown>;
  outputSchema?: Record<string, unknown>;
}

const registry = new Map<string, RegisteredTool>();

/**
 * 声明一个 TS 工具。在 src/tools/<域>/<工具>.ts 中 `export default defineTool({...})`。
 * 返回原 definition(便于文件 default 导出)。
 */
export function defineTool<TReq, TRes>(definition: ToolDefinition<TReq, TRes>): ToolDefinition<TReq, TRes> {
  if (!isRegisteredToolName(definition.name)) throw new Error(`defineTool name must use the registered Tool syntax and fit ${MAX_TOOL_NAME_UTF8_BYTES} UTF-8 bytes`);
  if (registry.has(definition.name)) {
    throw new Error(`duplicate defineTool registration: ${definition.name}`);
  }
  registry.set(definition.name, { definition: definition as ToolDefinition });
  return definition;
}

/** 构建期注入 schema(gen-tool-schemas.mjs 调用)。 */
export function injectToolSchema(name: string, inputSchema: Record<string, unknown>, outputSchema?: Record<string, unknown>): void {
  const entry = registry.get(name);
  if (!entry) return;
  entry.inputSchema = inputSchema;
  entry.outputSchema = outputSchema;
}

/** 获取所有已注册 TS defineTool 工具的 metadata（含构建期注入的 schema）。 */
export function defineToolMetadata(): ToolMetadata[] {
  const result: ToolMetadata[] = [];
  for (const [, entry] of registry) {
    const d = entry.definition;
    result.push({
      name: d.name,
      summary: d.summary,
      description: d.description,
      group: d.group,
      inputSchema: entry.inputSchema ?? {},
      outputSchema: entry.outputSchema,
      destructiveHint: d.destructiveHint,
      sideEffects: d.sideEffects,
      playModeSupport: d.playModeSupport,
      supportsDryRun: d.supportsDryRun,
      timeoutMs: d.timeoutMs,
      permissions: d.permissions,
      notes: d.notes,
      examples: d.examples,
      handler: d.handler,
      supportStatus: d.supportStatus,
      target: d.target,
      requiresPackages: d.requiresPackages,
    });
  }
  return result;
}

/** 按 name 查找已注册的 TS defineTool 工具定义(执行用)。 */
export function findDefineTool(name: string): ToolDefinition | undefined {
  return registry.get(name)?.definition;
}
