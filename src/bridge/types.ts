export interface BridgeRuntime {
  bridgeEndpoint?: string;
  dataDir?: string;
  bridgeExecute?: (payload: BridgeRequest) => Promise<BridgeResponse>;
}

export interface BridgeRequest {
  requestId: string;
  command: string;
  args: Record<string, unknown>;
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
  buildArgs: ArgBuilder;
  supportsJob?: boolean;
}
