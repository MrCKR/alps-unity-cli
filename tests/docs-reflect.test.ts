import assert from "node:assert/strict";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-docs-reflect-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

async function makeDocsCache(): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "unity-docs-cache-"));
  await writeFile(join(dir, "docs-index.json"), JSON.stringify({
    unityVersion: "2022.3",
    entries: [
      {
        name: "UnityEngine.Camera",
        title: "Camera",
        summary: "A component which creates an image of a particular viewpoint.",
        content: "# Camera\n\nCamera renders scenes for display.",
        url: "https://docs.unity3d.com/ScriptReference/Camera.html",
        kind: "class",
        namespace: "UnityEngine",
        version: "2022.3"
      },
      {
        name: "UnityEngine.Camera.Render",
        title: "Camera.Render",
        summary: "Manually render the camera.",
        kind: "method",
        namespace: "UnityEngine",
        version: "2022.3"
      },
      {
        name: "UnityEngine.Canvas",
        title: "Canvas",
        summary: "Element for UI rendering.",
        kind: "class",
        namespace: "UnityEngine",
        version: "2022.3"
      }
    ]
  }, null, 2));
  return dir;
}

async function makeReflectCache(options: { duplicateCamera?: boolean; assemblyFailureOnly?: boolean } = {}): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "unity-reflect-cache-"));
  const entries = options.assemblyFailureOnly ? [] : [
    {
      name: "Camera",
      fullName: "UnityEngine.Camera",
      namespace: "UnityEngine",
      kind: "class",
      assembly: "UnityEngine.CoreModule",
      signature: "public sealed class Camera : Behaviour",
      summary: "Renders scenes for display.",
      members: [
        {
          name: "Render",
          kind: "method",
          signature: "public void Render()",
          returnType: "void",
          summary: "Manually render the camera."
        },
        {
          name: "fieldOfView",
          kind: "property",
          signature: "public float fieldOfView { get; set; }",
          returnType: "float"
        }
      ]
    },
    {
      name: "Canvas",
      fullName: "UnityEngine.Canvas",
      namespace: "UnityEngine",
      kind: "class",
      assembly: "UnityEngine.UIModule",
      signature: "public sealed class Canvas : Behaviour",
      members: []
    },
    ...(options.duplicateCamera ? [{
      name: "Camera",
      fullName: "Game.Camera",
      namespace: "Game",
      kind: "class",
      assembly: "Assembly-CSharp",
      signature: "public class Camera",
      members: []
    }] : [])
  ];
  await writeFile(join(dir, "reflect-index.json"), JSON.stringify({
    unityVersion: "2022.3.45f1",
    assemblies: [
      { name: "UnityEngine.CoreModule", path: "Library/ScriptAssemblies/UnityEngine.CoreModule.dll", status: "loaded" },
      ...(options.assemblyFailureOnly ? [{ name: "Assembly-CSharp", path: "Library/ScriptAssemblies/Assembly-CSharp.dll", status: "failed", error: "Could not load Assembly-CSharp.dll" }] : [])
    ],
    entries
  }, null, 2));
  return dir;
}

test("docs get reads offline cache and keeps JSON envelope", async () => {
  const runtime = await makeRuntime();
  const docsDir = await makeDocsCache();

  const result = await execute(["tools", "run", "docs.get", "UnityEngine.Camera", "--docs-dir", docsDir, "--format", "text"], runtime);

  assert.equal(result.ok, true);
  const data = result.data as { command: string; mode: string; requiresUnity: boolean; unityVersion: { resolved: string }; format: string; doc: { name: string; content: string }; text: string };
  assert.equal(data.command, "docs.get");
  assert.equal(data.mode, "offline");
  assert.equal(data.requiresUnity, false);
  assert.equal(data.unityVersion.resolved, "2022.3");
  assert.equal(data.format, "text");
  assert.equal(data.doc.name, "UnityEngine.Camera");
  assert.match(data.text, /Camera renders scenes/);
});

test("docs search pages offline results", async () => {
  const runtime = await makeRuntime();
  const docsDir = await makeDocsCache();

  const result = await execute(["tools", "run", "docs.search", "UnityEngine", "--docs-dir", docsDir, "--page-size", "1", "--cursor", "1"], runtime);

  assert.equal(result.ok, true);
  const data = result.data as { items: Array<{ name: string }>; pageInfo: { pageSize: number; cursor: string; nextCursor?: string; total: number } };
  assert.equal(data.items.length, 1);
  assert.equal(data.pageInfo.pageSize, 1);
  assert.equal(data.pageInfo.cursor, "1");
  assert.equal(data.pageInfo.nextCursor, "2");
  assert.equal(data.pageInfo.total, 3);
});

test("docs cache missing returns structured diagnostic", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "docs.search", "Camera", "--docs-dir", join(runtime.dataDir, "missing")], runtime);

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "DOCS_CACHE_MISSING");
  assert.equal(result.error?.next_actions?.[0].includes("--docs-dir"), true);
});

test("docs get falls back online when default cache is missing", async () => {
  const runtime = await makeRuntime();
  const urls: string[] = [];
  const onlineRuntime = Object.assign(runtime, {
    fetch: async (url: string) => {
      urls.push(url);
      return { ok: url.endsWith("Camera.html"), status: url.endsWith("Camera.html") ? 200 : 404, text: async () => "<html><title>Camera - Unity</title><body>Camera renders scenes.</body></html>" };
    }
  });
  const result = await execute(["tools", "run", "docs.get", "UnityEngine.Camera"], onlineRuntime);

  assert.equal(result.ok, true);
  const data = result.data as { mode: string; source: { kind: string; offlineFallbackFrom: string }; doc: { name: string; title: string; url: string }; diagnostics: Array<{ code: string }> };
  assert.equal(data.mode, "online");
  assert.equal(data.source.kind, "online");
  assert.equal(data.source.offlineFallbackFrom, "DOCS_CACHE_MISSING");
  assert.equal(data.doc.name, "UnityEngine.Camera");
  assert.equal(data.doc.title, "Camera");
  assert.ok(urls.some((url) => url.endsWith("Camera.html")));
  assert.equal(data.diagnostics[0].code, "DOCS_ONLINE_FALLBACK");
});

test("docs search falls back online when offline cache has no matches", async () => {
  const runtime = await makeRuntime();
  const docsDir = await makeDocsCache();
  const onlineRuntime = Object.assign(runtime, {
    fetch: async () => ({ ok: true, status: 200, text: async () => "<a href=\"Rigidbody.html\">Rigidbody</a>" })
  });
  const result = await execute(["tools", "run", "docs.search", "Rigidbody", "--docs-dir", docsDir], onlineRuntime);

  assert.equal(result.ok, true);
  const data = result.data as { mode: string; source: { offlineFallbackFrom: string }; items: Array<{ name: string; url: string }> };
  assert.equal(data.mode, "online");
  assert.equal(data.source.offlineFallbackFrom, "DOCS_QUERY_NOT_FOUND");
  assert.equal(data.items[0].name, "Rigidbody");
  assert.match(data.items[0].url, /Rigidbody\.html$/);
});

test("reflect type and member read signatures and assembly source", async () => {
  const runtime = await makeRuntime();
  const reflectDir = await makeReflectCache();

  const type = await execute(["tools", "run", "reflect.type", "UnityEngine.Camera", "--reflect-dir", reflectDir, "--format", "text"], runtime);
  const member = await execute(["tools", "run", "reflect.member", "UnityEngine.Camera.Render", "--reflect-dir", reflectDir], runtime);

  assert.equal(type.ok, true);
  assert.equal(member.ok, true);
  const typeData = type.data as { type: { assembly: string; members: Array<{ name: string; signature: string }> }; text: string; assemblies: Array<{ name: string }> };
  const memberData = member.data as { member: { declaringType: string; signature: string; assembly: string } };
  assert.equal(typeData.type.assembly, "UnityEngine.CoreModule");
  assert.equal(typeData.type.members[0].signature, "public void Render()");
  assert.match(typeData.text, /UnityEngine.Camera/);
  assert.equal(typeData.assemblies[0].name, "UnityEngine.CoreModule");
  assert.equal(memberData.member.declaringType, "UnityEngine.Camera");
  assert.equal(memberData.member.signature, "public void Render()");
  assert.equal(memberData.member.assembly, "UnityEngine.CoreModule");
});

test("reflect search returns paged type and member results", async () => {
  const runtime = await makeRuntime();
  const reflectDir = await makeReflectCache();

  const result = await execute(["tools", "run", "reflect.search", "Render", "--reflect-dir", reflectDir, "--page-size", "1"], runtime);

  assert.equal(result.ok, true);
  const data = result.data as { mode: string; items: Array<{ kind: string; member?: { name: string } }>; pageInfo: { total: number } };
  assert.equal(data.mode, "offline");
  assert.equal(data.items.length, 1);
  assert.equal(data.items[0].kind, "member");
  assert.equal(data.items[0].member?.name, "Render");
  assert.equal(data.pageInfo.total, 2);
});

test("reflect ambiguous type returns structured diagnostic", async () => {
  const runtime = await makeRuntime();
  const reflectDir = await makeReflectCache({ duplicateCamera: true });

  const result = await execute(["tools", "run", "reflect.type", "Camera", "--reflect-dir", reflectDir], runtime);

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "REFLECT_TYPE_AMBIGUOUS");
  assert.match(result.error?.next_actions?.[0] ?? "", /UnityEngine.Camera/);
});

test("reflect assembly load failure is surfaced before query", async () => {
  const runtime = await makeRuntime();
  const reflectDir = await makeReflectCache({ assemblyFailureOnly: true });

  const result = await execute(["tools", "run", "reflect.search", "Camera", "--reflect-dir", reflectDir], runtime);

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "REFLECT_ASSEMBLY_LOAD_FAILED");
  assert.equal(result.error?.next_actions?.[0].includes("Regenerate"), true);
});

test("reflect cache missing returns structured diagnostic", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "reflect.type", "UnityEngine.Camera", "--reflect-dir", join(runtime.dataDir, "missing")], runtime);

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "REFLECT_CACHE_MISSING");
  assert.equal(result.error?.next_actions?.[0].includes("--reflect-dir"), true);
});

async function makeMarkdownDocsCache(): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "unity-markdown-docs-"));
  await mkdir(join(dir, "ScriptReference"), { recursive: true });
  await writeFile(join(dir, "ScriptReference", "Vector3.md"), "# Vector3\n\nRepresentation of 3D vectors and points.");
  return dir;
}

test("docs get can read markdown-only offline cache", async () => {
  const runtime = await makeRuntime();
  const docsDir = await makeMarkdownDocsCache();

  const result = await execute(["tools", "run", "docs.get", "Vector3", "--docs-dir", docsDir], runtime);

  assert.equal(result.ok, true);
  const data = result.data as { doc: { name: string; content: string; path: string } };
  assert.equal(data.doc.name, "Vector3");
  assert.match(data.doc.content, /Representation of 3D vectors/);
  assert.equal(data.doc.path, "ScriptReference/Vector3.md");
});
