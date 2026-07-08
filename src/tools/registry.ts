/**
 * TS defineTool 注册入口。
 *
 * 阶段 5 采用显式导入而非运行时文件扫描，保证打包后的 ESM 入口稳定，
 * 同时让构建期生成的 schema 注入模块在所有工具注册完成后执行。
 */
import "./package/list.js";
import "./package/status.js";
import "./package/info.js";
import "./package/search.js";
import "./package/resolve.js";
import "./package/install.js";
import "./package/remove.js";
import "./package/bridge-remove.js";
import "./package/deploy.js";
import "./package/restore.js";
import "./package/registry.js";
import "./package/scoped-registry.js";
import "./asset/refresh.js";
import "./code/exec.js";
import "./docs/list.js";
import "./docs/topic.js";
import "./docs/get.js";
import "./docs/search.js";
import "./reflect/type.js";
import "./reflect/member.js";
import "./reflect/search.js";
import "./editor/launch-dialog-dismiss.js";
import "./editor/refresh.js";
import "./editor/compile-diagnostics.js";
import "./diagnose/capabilities.js";
import "./build/run.js";
import "./build/status.js";
import "./build/cancel.js";
import "./test/run.js";
import "./tools/group.js";
import "./tools/group-list.js";
import "./tools/search.js";
import "./tools/describe.js";
import "./tools/run.js";
import "./.generated-schemas.js";

export { defineToolMetadata, findDefineTool } from "./define.js";
