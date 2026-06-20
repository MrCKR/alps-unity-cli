/**
 * Context system: CLI-first equivalent of MCP Resources.
 *
 * Contexts are read-only data sources that expose Unity Editor / project state
 * without side effects.
 *
 * Commands:
 *   context list          — list available contexts
 *   context describe <name> — inspect context descriptor
 *   context read <name>   — read context data via Unity Bridge
 */

export { handleContextCommand, type ContextRuntime } from "./commands.js";
export {
  builtinContextDescriptors,
  builtinContextCategories,
  findBuiltinContextByName,
  filterContextsByCategory,
  searchContexts,
  extensionContextDescriptors,
  discoverCustomContexts,
  customContextToDescriptor,
  allContextDescriptors,
  toListEntry,
  type ContextBridgeRuntime,
} from "./registry.js";
export type {
  ContextSource,
  ContextTarget,
  ContextAvailabilityScope,
  ContextDescriptor,
  ContextListEntry,
  ContextDescribeEntry,
  ContextReadResult,
  CustomContextDescriptor,
} from "./types.js";
