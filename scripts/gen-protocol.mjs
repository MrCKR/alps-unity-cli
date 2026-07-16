// 传输层协议 TS → C# DTO 单向生成器。
//
// 设计依据:.plan/协议框架.md(核心原则2:单向代码生成;原则6:C# 零新依赖)。
//
// 输入:src/protocol/*.ts 的 exported interface
// 输出:unitypkg/Editor/Protocol/*.cs(生成产物,提交 git,禁止手改)
//
// 类型映射:
//   string       -> string
//   number       -> double(可选 -> double?)
//   boolean      -> bool(可选 -> bool?)
//   unknown      -> object
//   T[]          -> List<T>
//   interface X  -> X(同文件生成的类)
//   泛型参数     -> object(传输层基类用 object,运行时由序列化器反射处理具体 DTO)
//   可选(prop?)  -> 可空类型,序列化时省略(null/proto3 语义);必填 -> 始终输出
//
// 命名:TS camelCase -> C# PascalCase 属性;C# 序列化器反射时映射回 camelCase 输出。
//
// 注:协议框架文档原提"ts-morph",本生成器直接用已安装的 typescript Compiler API,
// 避免新增依赖(更精简,符合项目风格)。此偏离记录于阶段1执行日志。

import { createRequire } from "node:module";
import { writeFileSync, mkdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const ts = require("typescript");

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(__dirname, "..");
const SRC_DIR = join(ROOT, "src", "protocol");
const OUT_DIR = join(ROOT, "unitypkg", "Editor", "Protocol");

// ─── 类型映射 ────────────────────────────────────────────────────────────

// 将 TS 类型节点映射为 C# 类型字符串。typeParams 为本 interface 声明的泛型参数名集合。
function mapType(node, typeParams, sourceFile) {
  // 关键字类型(TS 中 boolean/string/number/unknown 是 KeywordTypeNode,非 TypeReference)
  if (node.kind === ts.SyntaxKind.StringKeyword) return "string";
  if (node.kind === ts.SyntaxKind.NumberKeyword) return "double";
  if (node.kind === ts.SyntaxKind.BooleanKeyword) return "bool";
  if (node.kind === ts.SyntaxKind.UnknownKeyword) return "object";

  // 数组 T[]
  if (ts.isArrayTypeNode(node)) {
    return `System.Collections.Generic.List<${mapType(node.elementType, typeParams, sourceFile)}>`;
  }
  // Array<T>
  if (ts.isTypeReferenceNode(node)) {
    const name = node.typeName.getText(sourceFile);
    if (name === "Array") {
      const arg = node.typeArguments && node.typeArguments[0];
      if (arg) return `System.Collections.Generic.List<${mapType(arg, typeParams, sourceFile)}>`;
    }
    // 接口声明的泛型参数 -> object
    if (typeParams && typeParams.has(name)) return "object";
    // 其它 interface 引用 -> 同名 C# 类
    return name;
  }

  // boolean/string 字面量按其基础类型生成。
  if (ts.isLiteralTypeNode(node)) {
    if (node.literal.kind === ts.SyntaxKind.TrueKeyword || node.literal.kind === ts.SyntaxKind.FalseKeyword) return "bool";
    if (ts.isStringLiteral(node.literal)) return "string";
    if (ts.isNumericLiteral(node.literal)) return "double";
  }
  if (ts.isUnionTypeNode(node)) return "object";
  return "object";
}

// TS camelCase -> C# PascalCase
function toPascalCase(name) {
  if (!name) return name;
  return name.charAt(0).toUpperCase() + name.slice(1);
}

// ─── 单 interface -> C# 类块 ─────────────────────────────────────────────

function emitClass(interfaceDecl, sourceFile) {
  const className = (interfaceDecl.name && interfaceDecl.name.text) || "Unnamed";
  // 收集本 interface 的泛型参数名 -> 映射为 object
  const typeParams = new Set();
  if (interfaceDecl.typeParameters) {
    for (const tp of interfaceDecl.typeParameters) {
      if (tp.name) typeParams.add(tp.name.text);
    }
  }
  const propLines = [];

  for (const member of interfaceDecl.members) {
    if (!ts.isPropertySignature(member)) continue;
    // propertyName 是 Identifier,直接用 .text 避免 getText 需要 sourceFile
    const propName = member.name && member.name.text;
    if (!propName) continue;
    if (!member.type) continue;

    const csharpType = mapType(member.type, typeParams, sourceFile);
    const isOptional = !!member.questionToken;
    // Unity 2022.3 的 C# 9 编译环境不启用 nullable reference types。
    const isValueType = csharpType === "bool" || csharpType === "double" || csharpType === "int" || csharpType === "long" || csharpType === "float";
    const nullableSuffix = isOptional && isValueType ? "?" : "";
    const pascalName = toPascalCase(propName);

    propLines.push(`        public ${csharpType}${nullableSuffix} ${pascalName} { get; set; }`);
  }

  // 只返回类块,由调用方统一包装 namespace/文件头
  return `    public sealed partial class ${className}\n    {\n${propLines.join("\n")}\n    }`;
}

// ─── 主流程 ──────────────────────────────────────────────────────────────

function generate() {
  const inputFiles = ["envelope.ts"]; // 传输层协议源头文件(扩展时在此加)
  const program = ts.createProgram(
    inputFiles.map((f) => join(SRC_DIR, f)),
    { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext, strict: true }
  );

  mkdirSync(OUT_DIR, { recursive: true });

  let generated = 0;
  for (const sourceFile of program.getSourceFiles()) {
    if (sourceFile.isDeclarationFile) continue;
    const fileName = sourceFile.fileName.replace(/\\/g, "/");
    if (!fileName.includes("/protocol/")) continue;

    const outName = fileName.split("/").pop().replace(/\.ts$/, ".cs");
    const classes = [];

    ts.forEachChild(sourceFile, (node) => {
      if (ts.isInterfaceDeclaration(node)) {
        const isExported = node.modifiers && node.modifiers.some((m) => m.kind === ts.SyntaxKind.ExportKeyword);
        if (isExported) classes.push(emitClass(node, sourceFile));
      }
    });

    if (classes.length > 0) {
      const content = `// <auto-generated by scripts/gen-protocol.mjs />
// 源文件:src/protocol/${outName.replace(/\.cs$/, ".ts")}
// 传输层协议 C# DTO。禁止手改——改 src/protocol 后重生成。
#if UNITY_EDITOR
namespace UnityCli.Protocol
{
${classes.join("\n\n")}
}
#endif
`;
      writeFileSync(join(OUT_DIR, outName), content, "utf8");
      generated++;
      console.log(`generated: unitypkg/Editor/Protocol/${outName}`);
    }
  }

  console.log(`done: ${generated} file(s)`);
}

generate();
