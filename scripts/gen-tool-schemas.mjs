// 构建期 TS 工具 schema 生成器。
//
// 设计依据:.plan/工具统一形态.md 第八节(schema 派生:构建期生成)
// - 扫描 src/tools/**/*.ts 中 defineTool 的调用文件
// - 从同文件的 Request/Result interface 反射生成 JSON Schema
// - 注入到 defineTool registry(injectToolSchema)
//
// 阶段2b 先建机制(最小路径:能处理样板 SampleEchoTool 的 Request/Result)。
// 阶段5 TS 工具迁移时大规模使用。
//
// 实现策略(最小可行):
//   1. 扫描 src/tools/ 下 .ts 文件,找 defineTool 调用
//   2. 从 defineTool<TReq,TRes> 的类型参数或同文件 export interface 提取 Request/Result
//   3. 用 TypeScript Compiler API 反射 interface → JSON Schema
//   4. 生成一个 schema 注入模块 src/tools/.generated-schemas.ts,运行时 import 触发 injectToolSchema
//
// 注:为避免运行时文件扫描(ESM 无同步扫描),采用构建期生成注入模块的方式。

import { createRequire } from "node:module";
import { writeFileSync, readdirSync, statSync, existsSync, mkdirSync } from "node:fs";
import { join, resolve, dirname, relative } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const ts = require("typescript");

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(__dirname, "..");
const TOOLS_DIR = join(ROOT, "src", "tools");

// ─── 扫描 src/tools 下所有 .ts(递归,排除 define.ts/.generated-schemas.ts)────────

function collectTsFiles(dir, acc = []) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) {
      collectTsFiles(full, acc);
    } else if (entry.endsWith(".ts") && entry !== "define.ts" && !entry.startsWith(".generated-")) {
      acc.push(full);
    }
  }
  return acc;
}

// ─── 从文件找 defineTool 调用 + 提取 Request/Result interface 名 ─────────

// 返回 [{ toolName, reqInterfaceName, resInterfaceName }] 或空
function findDefineTools(sourceFile) {
  const tools = [];
  function visit(node) {
    // export default defineTool({ name: "...", ... })
    if (
      (ts.isExportAssignment(node) || ts.isCallExpression(node)) &&
      ts.isCallExpression(node.expression ? node.expression : node)
    ) {
      const call = ts.isCallExpression(node) ? node : node.expression;
      if (ts.isIdentifier(call.expression) && call.expression.text === "defineTool") {
        // 类型参数 <TReq, TRes>
        let reqName = null;
        let resName = null;
        if (call.typeArguments && call.typeArguments.length >= 2) {
          reqName = typeName(call.typeArguments[0]);
          resName = typeName(call.typeArguments[1]);
        }
        // 从第一个参数对象字面量取 name
        let toolName = null;
        if (call.arguments.length > 0) {
          const arg = call.arguments[0];
          if (ts.isObjectLiteralExpression(arg)) {
            for (const prop of arg.properties) {
              if (ts.isPropertyAssignment(prop) && prop.name.getText(sourceFile) === "name") {
                if (ts.isStringLiteral(prop.initializer)) toolName = prop.initializer.text;
              }
            }
          }
        }
        if (toolName) tools.push({ toolName, reqName, resName });
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(sourceFile);
  return tools;
}

function typeName(node) {
  if (ts.isTypeReferenceNode(node) && ts.isIdentifier(node.typeName)) return node.typeName.text;
  return null;
}

// ─── interface → JSON Schema(最小:基本类型 + 嵌套 + 数组 + 可选)─────────

function mapTypeToSchema(node, interfaceMap, sourceFile, ancestors) {
  if (!node) return {};
  if (node.kind === ts.SyntaxKind.StringKeyword) return { type: "string" };
  if (node.kind === ts.SyntaxKind.NumberKeyword) return { type: "number" };
  if (node.kind === ts.SyntaxKind.BooleanKeyword) return { type: "boolean" };
  if (node.kind === ts.SyntaxKind.UnknownKeyword) return {};

  if (ts.isArrayTypeNode(node)) {
    return { type: "array", items: mapTypeToSchema(node.elementType, interfaceMap, sourceFile, ancestors) };
  }
  if (ts.isTypeReferenceNode(node)) {
    const name = node.typeName.getText(sourceFile);
    // 基本类型别名(string/number/boolean 已在上面处理)
    if (name === "string") return { type: "string" };
    if (name === "number") return { type: "number" };
    if (name === "boolean") return { type: "boolean" };
    // 引用其它 interface
    const ref = interfaceMap.get(name);
    if (ref) {
      if (ancestors.has(name)) return {}; // 递归保护
      return interfaceToSchema(ref, interfaceMap, sourceFile, new Set([...ancestors, name]));
    }
    return {};
  }
  return {};
}

function interfaceToSchema(interfaceDecl, interfaceMap, sourceFile, ancestors) {
  const properties = {};
  const required = [];
  for (const member of interfaceDecl.members) {
    if (!ts.isPropertySignature(member)) continue;
    const propName = member.name && member.name.text;
    if (!propName || !member.type) continue;
    const isOptional = !!member.questionToken;
    properties[propName] = mapTypeToSchema(member.type, interfaceMap, sourceFile, ancestors);
    if (!isOptional) required.push(propName);
  }
  const schema = { type: "object", properties };
  if (required.length > 0) schema.required = required;
  return schema;
}

// 收集文件内所有 export interface(供引用解析)
function collectInterfaces(sourceFile) {
  const map = new Map();
  ts.forEachChild(sourceFile, (node) => {
    if (ts.isInterfaceDeclaration(node) && node.name) {
      map.set(node.name.text, node);
    }
  });
  return map;
}

// ─── 主流程 ──────────────────────────────────────────────────────────────

function generate() {
  if (!existsSync(TOOLS_DIR)) {
    console.log("gen-tool-schemas: src/tools/ not found, skip");
    return;
  }
  const files = collectTsFiles(TOOLS_DIR);
  const injections = []; // { toolName, inputSchema, outputSchema }

  for (const file of files) {
    const sourceText = require("node:fs").readFileSync(file, "utf8");
    const sourceFile = ts.createSourceFile(file, sourceText, ts.ScriptTarget.ES2022, true);
    const interfaceMap = collectInterfaces(sourceFile);
    const tools = findDefineTools(sourceFile);

    for (const t of tools) {
      const reqInterface = t.reqName ? interfaceMap.get(t.reqName) : null;
      const resInterface = t.resName ? interfaceMap.get(t.resName) : null;
      const inputSchema = reqInterface ? interfaceToSchema(reqInterface, interfaceMap, sourceFile, new Set([t.reqName])) : {};
      const outputSchema = resInterface ? interfaceToSchema(resInterface, interfaceMap, sourceFile, new Set([t.resName])) : {};
      injections.push({ toolName: t.toolName, inputSchema, outputSchema });
    }
  }

  // 生成注入模块
  const outDir = join(ROOT, "src", "tools");
  mkdirSync(outDir, { recursive: true });
  const outPath = join(outDir, ".generated-schemas.ts");
  const content = `// <auto-generated by scripts/gen-tool-schemas.mjs />
// 构建期从 src/tools/**/*.ts 的 Request/Result interface 生成。
// 运行时 import 本模块即注入 schema 到 defineTool registry。禁止手改。
import { injectToolSchema } from "./define.js";

${injections
  .map(
    (i) =>
      `injectToolSchema(${JSON.stringify(i.toolName)}, ${JSON.stringify(i.inputSchema)}, ${JSON.stringify(i.outputSchema)});`
  )
  .join("\n")}
`;
  writeFileSync(outPath, content, "utf8");
  console.log(`gen-tool-schemas: injected ${injections.length} tool schema(s)`);
}

generate();
