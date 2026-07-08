/**
 * TS defineContext 注册入口。
 *
 * Stage 6 采用显式导入，避免打包后运行时扫描不稳定；schema 由构建期注入。
 */
import "./tools/groups.js";
import "./tools/custom.js";
import "./.generated-schemas.js";

export { defineContextMetadata, findDefineContext, StructuredContextError } from "./define.js";
