import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { execute } from "../src/cli.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function runtime(overrides: Record<string, unknown> = {}) {
  return { dataDir: await mkdtemp(join(tmpdir(), "unity-cli-docs-reflect-data-")), projectTools: [], runtimeTools: [], ...overrides } as never;
}

async function docsCache(): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "unity-docs-cache-"));
  await writeFile(join(dir, "docs-index.json"), JSON.stringify({
    unityVersion: "2022.3",
    entries: [
      { name: "UnityEngine.Camera", title: "Camera", summary: "Creates an image.", content: "# Camera\n\nCamera renders scenes.", url: "https://docs.unity3d.com/ScriptReference/Camera.html", kind: "class", namespace: "UnityEngine", version: "2022.3" },
      { name: "UnityEngine.Camera.Render", title: "Camera.Render", summary: "Render the camera.", kind: "method", namespace: "UnityEngine", version: "2022.3" },
      { name: "UnityEngine.Canvas", title: "Canvas", summary: "UI rendering.", kind: "class", namespace: "UnityEngine", version: "2022.3" },
    ],
  }), "utf8");
  return dir;
}

async function reflectCache(options: { duplicate?: boolean; failed?: boolean } = {}): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "unity-reflect-cache-"));
  const entries = options.failed ? [] : [
    {
      name: "Camera", fullName: "UnityEngine.Camera", namespace: "UnityEngine", kind: "class", assembly: "UnityEngine.CoreModule",
      signature: "public sealed class Camera : Behaviour", summary: "Renders scenes.",
      members: [
        { name: "Render", kind: "method", signature: "public void Render()", returnType: "void", summary: "Render." },
        { name: "fieldOfView", kind: "property", signature: "public float fieldOfView { get; set; }", returnType: "float" },
      ],
    },
    { name: "Canvas", fullName: "UnityEngine.Canvas", namespace: "UnityEngine", kind: "class", assembly: "UnityEngine.UIModule", signature: "public sealed class Canvas : Behaviour", members: [] },
    ...(options.duplicate ? [{ name: "Camera", fullName: "Game.Camera", namespace: "Game", kind: "class", assembly: "Assembly-CSharp", signature: "public class Camera", members: [] }] : []),
  ];
  await writeFile(join(dir, "reflect-index.json"), JSON.stringify({
    unityVersion: "2022.3.61f1",
    assemblies: options.failed
      ? [{ name: "Assembly-CSharp", path: "Library/ScriptAssemblies/Assembly-CSharp.dll", status: "failed", error: "Could not load Assembly-CSharp.dll" }]
      : [{ name: "UnityEngine.CoreModule", path: "Library/ScriptAssemblies/UnityEngine.CoreModule.dll", status: "loaded" }],
    entries,
  }), "utf8");
  return dir;
}

async function run(name: string, input: Record<string, unknown>, overrides: Record<string, unknown> = {}) {
  return execute(["tools", "run", name, "--input", JSON.stringify(input)], await runtime(overrides));
}

test("docs.get reads offline content without generic execution fields", async () => {
  const result = await run("docs.get", { api: "UnityEngine.Camera", docsDir: await docsCache(), format: "text" });
  assert.ok(result.ok, JSON.stringify(result));
  const data = result.data as { mode: string; unityVersion: { resolved: string }; doc: { name: string; content: string }; text: string };
  assert.equal(data.mode, "offline");
  assert.equal(data.unityVersion.resolved, "2022.3");
  assert.equal(data.doc.name, "UnityEngine.Camera");
  assert.match(data.text, /Camera renders scenes/);
  assert.equal(Object.hasOwn(data, "command"), false);
});

test("docs.search returns deterministic pagination", async () => {
  const result = await run("docs.search", { query: "UnityEngine", docsDir: await docsCache(), pageSize: 1, cursor: "1" });
  assert.ok(result.ok, JSON.stringify(result));
  const data = result.data as { items: unknown[]; pageInfo: { pageSize: number; cursor: string; nextCursor?: string; total: number } };
  assert.equal(data.items.length, 1);
  assert.deepEqual(data.pageInfo, { pageSize: 1, cursor: "1", nextCursor: "2", total: 3 });
});

test("docs cache missing is structured and default cache can fall back online", async () => {
  const missing = await run("docs.search", { query: "Camera", docsDir: "Z:/missing-docs", offlineOnly: true });
  assert.ok(!missing.ok);
  assert.equal(missing.error?.code, "DOCS_CACHE_MISSING");

  const online = await run("docs.get", { api: "UnityEngine.Camera" }, {
    fetch: async (url: string) => ({ ok: url.endsWith("Camera.html"), status: url.endsWith("Camera.html") ? 200 : 404, text: async () => "<html><title>Camera - Unity</title><body>Camera renders scenes.</body></html>" }),
  });
  assert.ok(online.ok, JSON.stringify(online));
  assert.equal((online.data as { mode: string }).mode, "online");
});

test("reflect.type and reflect.member return assembly-backed signatures", async () => {
  const dir = await reflectCache();
  const type = await run("reflect.type", { name: "UnityEngine.Camera", reflectDir: dir, format: "text" });
  const member = await run("reflect.member", { member: "UnityEngine.Camera.Render", reflectDir: dir });
  assert.ok(type.ok, JSON.stringify(type));
  assert.ok(member.ok, JSON.stringify(member));
  assert.equal((type.data as { type: { assembly: string } }).type.assembly, "UnityEngine.CoreModule");
  assert.equal((member.data as { member: { signature: string } }).member.signature, "public void Render()");
});

test("reflect.search pages type and member matches", async () => {
  const result = await run("reflect.search", { query: "Render", reflectDir: await reflectCache(), pageSize: 1 });
  assert.ok(result.ok, JSON.stringify(result));
  const data = result.data as { items: Array<{ kind: string }>; pageInfo: { total: number } };
  assert.equal(data.items.length, 1);
  assert.equal(data.items[0].kind, "member");
  assert.equal(data.pageInfo.total, 2);
});

test("reflect ambiguity, assembly failure, and missing cache fail closed", async () => {
  const ambiguous = await run("reflect.type", { name: "Camera", reflectDir: await reflectCache({ duplicate: true }) });
  assert.ok(!ambiguous.ok);
  assert.equal(ambiguous.error?.code, "REFLECT_TYPE_AMBIGUOUS");

  const failed = await run("reflect.search", { query: "Camera", reflectDir: await reflectCache({ failed: true }) });
  assert.ok(!failed.ok);
  assert.equal(failed.error?.code, "REFLECT_ASSEMBLY_LOAD_FAILED");

  const missing = await run("reflect.type", { name: "Camera", reflectDir: "Z:/missing-reflect" });
  assert.ok(!missing.ok);
  assert.equal(missing.error?.code, "REFLECT_CACHE_MISSING");
});
