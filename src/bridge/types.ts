export interface BridgeRuntime {
  bridgeEndpoint?: string;
  dataDir?: string;
  bridgeExecute?: (payload: BridgeRequest) => Promise<BridgeResponse>;
}

export interface BridgeRequest {
  requestId: string;
  command: string;
  args: Record<string, unknown>;
  dryRun?: boolean;
  security?: Record<string, unknown>;
}

export interface BridgeError {
  code?: string;
  message?: string;
  [key: string]: unknown;
}

export interface BridgeResponse {
  ok?: boolean;
  data?: Record<string, unknown>;
  error?: BridgeError;
  nextActions?: string[];
}

export type BridgeFlags = Record<string, string | boolean>;

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
