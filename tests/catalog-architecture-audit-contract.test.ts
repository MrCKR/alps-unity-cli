import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

function countForbiddenJsonFields(value: unknown): number {
  if (Array.isArray(value)) return value.reduce((total, item) => total + countForbiddenJsonFields(item), 0);
  if (!value || typeof value !== "object") return 0;
  const record = value as Record<string, unknown>;
  const descriptor = (typeof record.name === "string" || typeof record.toolName === "string")
    && (Object.hasOwn(record, "summary") || Object.hasOwn(record, "group") || Object.hasOwn(record, "inputSchema"));
  const own = Number(Object.hasOwn(record, "descriptorFingerprint")) + Number(Object.hasOwn(record, "handlerFingerprint"))
    + Number(descriptor && Object.hasOwn(record, "fingerprint"));
  return own + Object.values(record).reduce((total, item) => total + countForbiddenJsonFields(item), 0);
}

async function filesUnder(root: string): Promise<string[]> {
  const result: string[] = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) result.push(...await filesUnder(path));
    else result.push(path.replaceAll("\\", "/"));
  }
  return result;
}

test("catalog construction audit observes direct forbidden I/O without voluntary counters", async () => {
  const [audit, bridge, registry, authoring, journal, ownership, attributes, schemaValidator, schemaDeriver, serializer] = await Promise.all([
    readFile("scripts/audit-final-architecture.mjs", "utf8"),
    readFile("unitypkg/Editor/UnityCliBridge.cs", "utf8"),
    readFile("unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", "utf8"),
    readFile("unitypkg/Editor/ProjectTools/IUnityCliTool.cs", "utf8"),
    readFile("unitypkg/Editor/ProjectTools/RegisteredTool.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/RegistryOwnershipResolver.cs", "utf8"),
    readFile("unitypkg/Editor/ProjectTools/OfficialAttributeToolRegistry.cs", "utf8"),
    readFile("unitypkg/Editor/ProjectTools/ProjectToolSchemaValidator.cs", "utf8"),
    readFile("unitypkg/Editor/ProjectTools/UnityCliSchemaDeriver.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliSerializer.cs", "utf8"),
  ]);
  assert.doesNotMatch(journal, /AssetDatabaseOperationCount|SourceFileReadCount|PersistedToolValueOperationCount|PerToolSuccessLogCount/);
  assert.match(audit, /id: "catalogConstructionForbiddenIo"/);
  assert.match(audit, /staticConstructorStart = bridgeSource\.indexOf\("static UnityCliBridge\(\)"\)/);
  assert.match(audit, /constructionStart = catalogRegistry\.indexOf\("private static void InitializeProjectToolCatalog\(\)"\)/);
  assert.match(audit, /constructionEnd = catalogRegistry\.indexOf\("private static bool ProjectToolCatalogMatches"/);
  assert.match(audit, /AssetDatabase\|EditorPrefs\|SessionState\|PlayerPrefs/);
  assert.match(audit, /File\|Directory/);

  const start = registry.indexOf("private static void InitializeProjectToolCatalog()");
  const end = registry.indexOf("private static bool ProjectToolCatalogMatches", start);
  assert.ok(start >= 0 && end > start);
  const staticStart = bridge.indexOf("static UnityCliBridge()");
  const staticEnd = bridge.indexOf("private static", staticStart + 1);
  const staticConstructor = bridge.slice(staticStart, staticEnd).replace("AssetDatabase.IsAssetImportWorkerProcess()", "AllowedAssetImportWorkerGuard()");
  const productionConstructionGraph = [staticConstructor, registry.slice(start, end), authoring, journal, ownership, attributes, schemaValidator, schemaDeriver, serializer].join("\n");
  const forbidden = /\b(?:AssetDatabase|EditorPrefs|SessionState|PlayerPrefs)\b|\b(?:File|Directory)\s*\.|WriteLog\([^\r\n]*(?:completed|registered|success)/gi;
  assert.equal(productionConstructionGraph.match(forbidden)?.length ?? 0, 0);
  assert.equal((`${productionConstructionGraph}\nFile.ReadAllText(path);`).match(forbidden)?.length, 1, "a direct source read must fail independently of Record calls");
});

test("static catalog forbidden fields are absent from generated and contract surfaces", async () => {
  const audit = await readFile("scripts/audit-final-architecture.mjs", "utf8");
  assert.match(audit, /id: "staticCatalogForbiddenFields"/);
  assert.match(audit, /\["descriptor", "Fingerprint"\]\.join\(""\)/);
  assert.match(audit, /\["handler", "Fingerprint"\]\.join\(""\)/);
  assert.match(audit, /\["finger", "print"\]\.join\(""\)/);
  for (const root of ["contracts", "schemas", "scripts", "src", "unitypkg"]) {
    for (const path of await filesUnder(root)) {
      const inScope = path.startsWith("contracts/") || path.startsWith("schemas/") || path.startsWith("scripts/")
        || path.includes("/.generated-") || path.includes("/Generated/") || path.endsWith(".g.cs");
      if (!inScope || path === "scripts/audit-final-architecture.mjs") continue;
      const source = await readFile(path, "utf8");
      if (path.endsWith(".json")) {
        assert.equal(countForbiddenJsonFields(JSON.parse(source)), 0, path);
      } else {
        const forbiddenField = /["'](?:descriptorFingerprint|handlerFingerprint|fingerprint)["']\s*:|\b(?:descriptorFingerprint|handlerFingerprint|fingerprint)\s*(?:[?]?:|\{|=)/gi;
        assert.doesNotMatch(source, forbiddenField, path);
      }
    }
  }
});
