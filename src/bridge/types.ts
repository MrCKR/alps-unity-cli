import type { RootAdmissionLease } from "./admission.js";

export interface BridgeRuntime {
  dataDir?: string;
  /** 单target公开根调用共享的私有准入lease；不得进入业务input或公开输出。 */
  rootAdmission?: RootAdmissionLease;
  /** 统一catalog公开根对每个在线target session分别持有一个slot。 */
  rootAdmissions?: Partial<Record<"editor" | "runtime", RootAdmissionLease>>;
  /** 仅用于隔离测试；真实 CLI transport 始终由 SessionSupervisor 绑定 registry listener。 */
  bridgeExecute?: (payload: BridgeRequest) => Promise<BridgeResponse>;
}

import type {
  BridgeRequest as ProtocolBridgeRequest,
  Envelope as ProtocolEnvelope,
  ToolError
} from "../protocol/envelope.js";

export type BridgeRequest = ProtocolBridgeRequest<Record<string, unknown>>;

export type BridgeError = ToolError<Record<string, unknown>>;

export type BridgeResponse = ProtocolEnvelope<Record<string, unknown>, Record<string, unknown>>;

export type BridgeFlags = Record<string, string | boolean>;

export interface BridgeInvocationOptions {
  mode?: "execute" | "read" | "observeNow";
  mutation?: boolean;
  defaultTimeoutMs?: number;
  /** 私有 observation deadline；调用链内以同一 wall-clock deadline 传递。 */
  observationAbsoluteDeadlineMs?: number;
  /** 私有调用链 deadline；只能收紧调用者预算，绝不进入 Bridge body。 */
  absoluteDeadlineMs?: number;
  /** 仅 editor.state 内部传递已完成的真实选择结果；Symbol 字段不会进入公开请求。 */
  [observationSelectionKey]?: import("../instances.js").InstanceSelection;
  [transitionBindingKey]?: true;
  [transitionResponseKey]?: true;
}

// 不导出 key，外部 flags、CLI 输入和 JSON 均不能伪造这些内部调用字段。
const observationSelectionKey: unique symbol = Symbol("unity-cli.observation-selection");
const transitionBindingKey: unique symbol = Symbol("unity-cli.transition-binding");
const transitionResponseKey: unique symbol = Symbol("unity-cli.transition-response");

export function withObservationSelection(
  options: Omit<BridgeInvocationOptions, typeof observationSelectionKey>,
  selection: import("../instances.js").InstanceSelection,
): BridgeInvocationOptions {
  return { ...options, [observationSelectionKey]: selection };
}

export function observationSelectionFrom(options: BridgeInvocationOptions): import("../instances.js").InstanceSelection | undefined {
  return options[observationSelectionKey];
}

/** 仅白名单内的跨 reload 内部调用可在 registry transitioning 时绑定同一会话。 */
export function withTransitionBinding(options: Omit<BridgeInvocationOptions, typeof transitionBindingKey>): BridgeInvocationOptions {
  return { ...options, [transitionBindingKey]: true };
}

export function transitionBindingAllowed(options: BridgeInvocationOptions): boolean {
  return options[transitionBindingKey] === true;
}

/** 仅 durable status coordinator 可接管 canonical transition 响应并重新观察。 */
export function withTransitionStatusObservation(
  options: Omit<BridgeInvocationOptions, typeof transitionBindingKey | typeof transitionResponseKey | "mode" | "mutation">,
): BridgeInvocationOptions {
  return {
    ...options,
    mode: "read",
    mutation: false,
    [transitionBindingKey]: true,
    [transitionResponseKey]: true,
  };
}

export function transitionResponseAllowed(options: BridgeInvocationOptions): boolean {
  return options[transitionResponseKey] === true;
}

export interface BridgeArgContext {
  command: string;
  flags: BridgeFlags;
  positionalTarget?: string;
}

export type ArgBuilder = (context: BridgeArgContext) => Record<string, unknown> | Promise<Record<string, unknown>>;

export interface BridgeCommandSpec {
  group: string;
  command: string;
  bridgeCommand: string;
  /** When bridgeCommand is "catalog.editor.run", this carries the underlying tool name (e.g. "probuilder.ping"). */
  toolName?: string;
  buildArgs: ArgBuilder;
  destructiveHint?: boolean;
  supportsDryRun?: boolean;
  /** If true, this command is internal and not accessible from CLI. Only used as bridge protocol. */
  internal?: boolean;
  /** If true, native top-level CLI routing is disabled; tools run may still use the spec for argument mapping. */
  toolsOnly?: boolean;
  /** Native argv mapping used internally by tools run for argument builders. */
  nativeCliCommand?: string;
}
