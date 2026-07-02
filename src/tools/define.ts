/**
 * TS 工具声明工厂(defineTool)与 registry。
 *
 * 设计依据:.plan/工具统一形态.md 第八节(TS 自包含形态)
 * - 一个 TS 工具 = 一个文件,导出 defineTool({...}) default
 * - 协议类型(Request/Result interface)与 desc + handler 自包含
 * - inputSchema/outputSchema 由构建期 gen-tool-schemas.mjs 从 Request/Result 类型生成,
 *   defineTool 不写 schema(与 C# 侧 IUnityCliTool 对称:Describe 无法染指 schema)
 * - 工具发现:扫描 src/tools/** 下所有 defineTool 导出(阶段5 废 tools.ts 路由器后启用)
 *
 * 阶段2b 先建机制,不迁移现有工具(新旧并存)。
 */

import type { ToolMetadata, PlayModeSupport, ToolHandler } from "./metadata/types.js";

// ─── 工具定义输入(defineTool 接收的形态)────────────────────────────────

/** defineTool 的输入。schema 不在此处(构建期从 Request/Result 类型生成注入)。 */
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
  /** 执行路由:bridge/bridge-runtime/job/offline/hybrid。 */
  handler?: ToolHandler;
  supportStatus?: import("./metadata/types.js").ToolSupportStatus;
  target?: string;
  requiresPackages?: string[];
  examples?: import("./metadata/types.js").ToolExample[];
  /** handler。结构体进结构体出。报错抛 Error(框架转结构化错误)。 */
  execute: (req: TReq, ctx: ToolContext) => Promise<TRes> | TRes;
}

/** 工具执行上下文(运行时由框架注入)。 */
export interface ToolContext {
  requestId: string;
  projectPath: string;
  /** artifact 目录(可写)。 */
  artifactDir: string;
  /** bridge 日志路径。 */
  logPath?: string;
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
  if (!definition.name) throw new Error("defineTool requires name");
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

/** 获取所有已注册 TS defineTool 工具的 metadata(含注入的 schema)。阶段5 发现链用。 */
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
