/**
 * TS defineTool 注册入口。
 *
 * 阶段 5 采用显式导入而非运行时文件扫描，保证打包后的 ESM 入口稳定，
 * 同时让构建期生成的 schema 注入模块在所有工具注册完成后执行。
 */
import "./package/list.js";
import "./.generated-schemas.js";

export { defineToolMetadata, findDefineTool } from "./define.js";
