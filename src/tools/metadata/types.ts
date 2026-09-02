import type { AnyJson } from "../../schema/any-json.js";

export interface ToolExample {
  description: string;
  argv: string[];
  /** 内部 authoring 输入；公开投影返回 description、input 与按需 execution。 */
  input?: AnyJson;
  execution?: { dryRun?: boolean; timeoutMs?: number };
  output?: string;
}

export type ToolHandler = "bridge" | "bridge-runtime" | "offline" | "hybrid";
export type ToolRiskLevel = "safe" | "low" | "medium" | "high" | "critical";
export type ToolSupportStatus = "live-bridge" | "live-bridge-runtime" | "cli-only" | "planned";
export type ToolSource = "built-in" | "project" | "extension";
/** 区分本项目完整完成证明与官方 attribute 的保守方法返回边界。 */
export type ToolContractProfile = "native" | "official-attribute";
export type PlayModeSupport = "supported" | "runtime-only" | "session-affecting" | "editor-only" | "unknown";

export interface ToolCompletionContract {
  executionClass: "atomic" | "observed-state" | "resumable" | "external";
  completionPredicate: string;
  proofSource: string;
  reloadPolicy: string;
  timeoutError: string;
  outcomeUnknownPolicy: string;
  completionDriverId: string;
  journalPolicy: "required" | "none";
  mutation: boolean;
}

export interface ToolMetadata {
  /** Optional origin override when a live bridge descriptor already knows whether this tool is built-in/extension/project. */
  source?: ToolSource;
  name: string;
  summary: string;
  description: string;
  /** Logical group for discovery and `tools group list <group>`. */
  group: string;
  inputSchema: Record<string, unknown>;
  outputSchema?: Record<string, unknown>;
  destructiveHint: boolean;
  /** Side effect categories this tool may produce, e.g. ['file-write', 'scene-modify']. */
  sideEffects?: string[];
  /** Play Mode execution semantics for AI routing and runtime safety. */
  playModeSupport?: PlayModeSupport;
  supportsDryRun?: boolean;
  timeoutMs?: number;
  permissions?: string[];
  examples?: ToolExample[];
  /** Internal execution route override. Omit when the route can be inferred from the bridge registry or tool name. */
  handler?: ToolHandler;
  /** 内部强类型路由状态，不进入公开 descriptor。 */
  supportStatus?: ToolSupportStatus;
  /** Execution target: "editor" (default), "runtime", or "both". */
  target?: string;
  /** Descriptor 声明的可用范围；live registry 值优先于 target 派生默认值。 */
  availabilityScope?: string;
  /** Whether the tool is currently available based on session state (runtime tools: only in Play Mode). */
  available?: boolean;
  /** 当前不可用的可执行原因；仅在 available=false 时公开。 */
  unavailableReason?: string;
  /** Unity package names required by this tool at runtime. When absent the tool has no package prerequisites. */
  requiresPackages?: string[];
  /** descriptor 的完成保证来源；TS 本地和 IUnityCliTool 默认为 native。 */
  contractProfile?: ToolContractProfile;
  /** 每次直接调用必须命中的机器可执行 completion contract。 */
  completionContract?: ToolCompletionContract;
}

export type ResolvedToolMetadata = ToolMetadata & {
  source: ToolSource;
  handler: ToolHandler;
  playModeSupport: PlayModeSupport;
  availabilityScope: string;
  contractProfile: ToolContractProfile;
  /** 仅供内部安全与路由计算使用，公开 catalog 不会投影。 */
  riskLevel?: ToolRiskLevel;
};
