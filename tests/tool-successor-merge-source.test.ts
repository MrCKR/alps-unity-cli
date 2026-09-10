import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import test from "node:test";

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

  const volume = await readFile("unitypkg/Editor/Tools/volume/list.cs", "utf8");
  assert.match(volume, /Request \{ public string Target[\s\S]*public int PageSize[\s\S]*public string Cursor/);
  assert.match(volume, /Result \{ public List<VolumeInfo> Volumes[\s\S]*public long Count[\s\S]*public PageInfo PageInfo/);
  assert.match(volume, /HasMore = false, PageSize = pageSize/);

});
