import type { AnyJson } from "../schema/any-json.js";

export type CompletionProbeContext = "scene.gameobject" | "scene.gameobject.component";
export type CompletionProbeOperator = "equals" | "notEquals" | "approx" | "exists" | "lengthEquals" | "contains";

/** 高风险同步动作的声明式 before/after proof。 */
export interface CompletionProbe {
  context: CompletionProbeContext;
  input: Record<string, AnyJson>;
  assertions: CompletionProbeAssertion[];
}

export interface CompletionProbeAssertion {
  path: string;
  operator: CompletionProbeOperator;
  before: AnyJson;
  after: AnyJson;
  tolerance?: number;
}

export interface CompletionProbeReadback {
  assertions: Array<{ path: string; value: AnyJson }>;
}
