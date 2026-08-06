/** TS Context 注册入口；能力清单与 schema 均由构建期生成。 */
import "./.generated-registry.js";

export { defineContextMetadata, findDefineContext, StructuredContextError } from "./define.js";
export {
  builtinContextCategories,
  builtinContextDescriptors,
  filterContextsByCategory,
  findBuiltinContextByName,
  loadContextCatalog,
  parseLiveDescriptor,
  resolveCatalogContext,
  searchContexts,
  toContextListEntry
} from "./catalog.js";
export type { ContextCatalogRuntime, ContextCatalogResult } from "./catalog.js";
export type * from "./types.js";
