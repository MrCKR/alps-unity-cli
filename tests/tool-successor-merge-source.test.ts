import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import test from "node:test";
import { staticCapabilityCatalog } from "../src/static-capability-catalog.js";

const removedReadOwners = [
  "unitypkg/Editor/Contexts/scene/renderer-features.cs",
  "unitypkg/Editor/Contexts/scene/volumes.cs",
];

test("approved read fields are merged into canonical Tools", async () => {
  for (const path of removedReadOwners) {
    await assert.rejects(access(path), { code: "ENOENT" });
  }

  const featureList = await readFile("extensions/urp/unitypkg/Editor/Urp/feature-list.cs", "utf8");
  const featureSupport = await readFile("extensions/urp/unitypkg/Editor/Urp/UrpFeatureToolSupport.cs", "utf8");
  assert.match(featureList, /IUnityCliTool<UrpFeatureListTool\.Request, UrpFeatureListResult>/);
  assert.match(featureSupport, /UrpFeatureListResult : UrpFeatureResult/);
  for (const field of ["Available", "Renderer", "Diagnostics", "NextActions"]) {
    assert.match(featureSupport, new RegExp(`public .* ${field} \\{ get; set; \\}`));
  }

  const volume = await readFile("unitypkg/Editor/Tools/graphics/volume.cs", "utf8");
  assert.match(volume, /Request \{ public string Target[\s\S]*public int PageSize[\s\S]*public string Cursor/);
  assert.match(volume, /Result \{ public List<VolumeInfo> Volumes[\s\S]*public long Count[\s\S]*public PageInfo PageInfo/);
  assert.match(volume, /HasMore = false, PageSize = pageSize/);

  const catalog = staticCapabilityCatalog();
  assert.equal(Object.hasOwn(catalog, "contexts"), false);
  const tools = new Map(catalog.tools.map((tool) => [String(tool.name), tool]));
  const feature = tools.get("urp.feature.list") as { outputSchema: { properties: Record<string, unknown> } } | undefined;
  assert.ok(feature);
  const featureSchema = feature.outputSchema.properties;
  for (const field of ["available", "renderer", "diagnostics", "nextActions"]) assert.ok(field in featureSchema);
  const graphics = tools.get("graphics.volume") as { inputSchema: { properties: Record<string, unknown> }; outputSchema: { properties: Record<string, unknown> } } | undefined;
  assert.ok(graphics);
  const graphicsInput = graphics.inputSchema.properties;
  const graphicsOutput = graphics.outputSchema.properties;
  for (const field of ["target", "cursor", "pageSize"]) assert.ok(field in graphicsInput);
  for (const field of ["volumes", "count", "pageInfo"]) assert.ok(field in graphicsOutput);
});
