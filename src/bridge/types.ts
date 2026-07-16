export interface BridgeRuntime {
  bridgeEndpoint?: string;
  dataDir?: string;
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

/** 生成 destructive proof 所需的最小 descriptor；动态 Tool 必须传入 live 值。 */
export interface BridgeSecurityDescriptor {
  name: string;
  destructiveHint: boolean;
  supportsDryRun: boolean;
  bridgeCommand: string;
  liveDescriptorFingerprint?: string;
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
  /** When bridgeCommand is "project-tool.run", this carries the underlying tool name (e.g. "probuilder.ping"). */
  toolName?: string;
  buildArgs: ArgBuilder;
  supportsJob?: boolean;
  destructiveHint?: boolean;
  supportsDryRun?: boolean;
  /** If true, this command is internal and not accessible from CLI. Only used as bridge protocol. */
  internal?: boolean;
  /** If true, native top-level CLI routing is disabled; tools run may still use the spec for argument mapping. */
  toolsOnly?: boolean;
  /** Native argv mapping used internally by tools run for argument builders. */
  nativeCliCommand?: string;
}
