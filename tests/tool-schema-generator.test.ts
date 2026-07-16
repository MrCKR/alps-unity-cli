import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";

const execFileAsync = promisify(execFile);
const generator = resolve(process.cwd(), "scripts", "gen-tool-schemas.mjs");

async function writeFixture(files: Record<string, string>): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), "unity-cli-schema-"));
  await writeFile(join(root, "tsconfig.json"), JSON.stringify({
    compilerOptions: { strict: true, target: "ES2022", module: "NodeNext", moduleResolution: "NodeNext" },
    include: ["src/**/*.ts"]
  }));
  for (const [path, content] of Object.entries(files)) {
    const fullPath = join(root, path);
    await mkdir(resolve(fullPath, ".."), { recursive: true });
    await writeFile(fullPath, content);
  }
  return root;
}

function generatedArguments(source: string, injector: string, name: string): unknown[] {
  const prefix = `${injector}(${JSON.stringify(name)}, `;
  const line = source.split("\n").find((candidate) => candidate.startsWith(prefix));
  assert.ok(line, `missing generated injection for ${name}`);
  return JSON.parse(`[${line.slice(injector.length + 1, -2)}]`) as unknown[];
}

function assertNoEmptySchema(schema: any, path = "$"): void {
  assert.ok(schema && typeof schema === "object" && Object.keys(schema).length > 0, `empty schema at ${path}`);
  for (const [name, property] of Object.entries(schema.properties ?? {})) assertNoEmptySchema(property, `${path}.${name}`);
  if (schema.items && typeof schema.items === "object") assertNoEmptySchema(schema.items, `${path}[]`);
  for (const [index, item] of (schema.prefixItems ?? []).entries()) assertNoEmptySchema(item, `${path}[${index}]`);
  for (const [index, item] of (schema.oneOf ?? []).entries()) assertNoEmptySchema(item, `${path}.oneOf[${index}]`);
  for (const [name, definition] of Object.entries(schema.$defs ?? {})) assertNoEmptySchema(definition, `${path}.$defs.${name}`);
  if (schema.additionalProperties && typeof schema.additionalProperties === "object") assertNoEmptySchema(schema.additionalProperties, `${path}.*`);
}

test("schema generator resolves the supported TypeScript type surface through TypeChecker", async () => {
  const root = await writeFixture({
    "src/schema/any-json.ts": "export type AnyJson = string | number | boolean | null | AnyJson[] | { [key: string]: AnyJson };\n",
    "src/shared.ts": `
      export enum Mode { Fast = "fast", Safe = "safe" }
      export type Choice = { kind: "text"; value: string } | { kind: "count"; value: number };
      export interface TreeNode { name: string; children?: ReadonlyArray<TreeNode>; }
    `,
    "src/tools/define.ts": "export declare function defineTool<TReq, TRes>(value: unknown): unknown;\n",
    "src/tools/fixture.ts": `
      import { defineTool } from "./define.js";
      import type { AnyJson } from "../schema/any-json.js";
      import { Mode, type Choice, type TreeNode } from "../shared.js";
      interface Request {
        optional?: string | null;
        mode: Mode;
        aliases: Array<string>;
        readonlyAliases: ReadonlyArray<string>;
        pair: [string, number?];
        nested: { enabled: boolean; label?: "a" | "b" };
        dictionary: Record<string, number>;
        choice: Choice;
        tree: TreeNode;
        dynamic: AnyJson;
      }
      type Result = { ok: true; payload: AnyJson } | { ok: false; reason: string };
      export default defineTool<Request, Result>({ name: "fixture.types" });
    `,
    "src/contexts/define.ts": "export declare function defineContext<TReq, TRes>(value: unknown): unknown;\n",
    "src/contexts/fixture.ts": `
      import { defineContext } from "./define.js";
      import type { TreeNode } from "../shared.js";
      export default defineContext<{}, TreeNode>({ name: "fixture.context" });
    `
  });

  const { stdout } = await execFileAsync(process.execPath, [generator, root]);
  assert.match(stdout, /generated 1 tool schema\(s\), 1 context schema\(s\), 0 errors/);

  const toolModule = await readFile(join(root, "src", "tools", ".generated-schemas.ts"), "utf8");
  const [, input, output] = generatedArguments(toolModule, "injectToolSchema", "fixture.types") as [string, any, any];
  assert.equal(input.type, "object");
  assert.deepEqual(input.properties.mode.enum, ["fast", "safe"]);
  assert.equal(input.properties.aliases.items.type, "string");
  assert.equal(input.properties.readonlyAliases.items.type, "string");
  assert.deepEqual(input.properties.pair.prefixItems.map((item: any) => item.type), ["string", "number"]);
  assert.equal(input.properties.nested.properties.enabled.type, "boolean");
  assert.deepEqual(input.properties.nested.properties.label.enum, ["a", "b"]);
  assert.equal(input.properties.dictionary.additionalProperties.type, "number");
  assert.equal(input.properties.choice.oneOf.length, 2);
  assert.equal(input.properties.tree.properties.children.items.$ref, "#/$defs/TreeNode");
  assert.equal(input.$defs.TreeNode.type, "object");
  assert.equal(input.properties.dynamic["x-unityCliAnyJson"], true);
  assert.equal(output.oneOf.length, 2);
  assertNoEmptySchema(input);
  assertNoEmptySchema(output);
});

test("schema generator fails closed with capability and property diagnostics", async () => {
  const root = await writeFixture({
    "src/tools/define.ts": "export declare function defineTool<TReq, TRes>(value: unknown): unknown;\n",
    "src/tools/first.ts": `
      import { defineTool } from "./define.js";
      interface Request { payload: unknown }
      interface Result { ok: boolean }
      export default defineTool<Request, Result>({ name: "fixture.duplicate" });
    `,
    "src/tools/second.ts": `
      import { defineTool } from "./define.js";
      export default defineTool<{}, {}>({ name: "fixture.duplicate" });
    `,
    "src/tools/missing.ts": `
      import { defineTool } from "./define.js";
      export default defineTool({ name: "fixture.missing" });
    `
  });

  await assert.rejects(
    execFileAsync(process.execPath, [generator, root]),
    (error: any) => {
      assert.notEqual(error.code, 0);
      assert.match(error.stderr, /src\/tools\/first\.ts: Tool "fixture\.duplicate", type Request, path \$\.payload, TypeScript type "unknown"/);
      assert.match(error.stderr, /src\/tools\/second\.ts: Tool "fixture\.duplicate".*name already declared in src\/tools\/first\.ts/);
      assert.match(error.stderr, /src\/tools\/missing\.ts: Tool "fixture\.missing".*exactly two explicit generic arguments are required/);
      return true;
    }
  );
});
