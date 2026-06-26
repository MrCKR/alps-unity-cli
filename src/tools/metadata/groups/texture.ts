import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

// TextureImporter 枚举值，对齐 CoplayDev manage_texture import_settings 的可读枚举文档
const TEXTURE_TYPE_ENUM = ["Default", "NormalMap", "GUI", "Sprite", "Cursor", "Cookie", "Lightmap", "SingleChannel"];
const WRAP_MODE_ENUM = ["Repeat", "Clamp", "Mirror", "MirrorOnce"];
const FILTER_MODE_ENUM = ["Point", "Bilinear", "Trilinear"];
const COMPRESSION_ENUM = ["Uncompressed", "Compressed", "CompressedLQ", "CompressedHQ"];

export const TEXTURE_TOOLS: ToolMetadata[] = [
  {
    name: "texture.info",
    title: "Texture Info",
    description: "Read dimensions, format, mip/stream flags and TextureImporter settings of a texture asset. Use this before texture.import-settings to inspect current values, or before texture.sprite to confirm the texture type. Use asset.search with filter t:Texture2D to discover paths when unknown.",
    group: "texture",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/* texture path (.png/.jpg/.jpeg/.tga/.psd/.exr)." } }, ["path"]),
    outputSchema: objSchema({
      texture: objSchema({
        path: { type: "string" },
        guid: { type: "string" },
        name: { type: "string" },
        width: { type: "integer" },
        height: { type: "integer" },
        format: { type: "string" },
        mipCount: { type: "integer" },
        isReadable: { type: "boolean" }
      }),
      importSettings: objSchema({
        textureType: { type: "string" },
        textureShape: { type: "string" },
        wrapMode: { type: "string" },
        filterMode: { type: "string" },
        maxTextureSize: { type: "integer" },
        textureCompression: { type: "string" },
        mipmapEnabled: { type: "boolean" },
        sRGBTexture: { type: "boolean" }
      })
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run texture.info",
    docsTopic: "texture",
    equivalentUnityMcp: "manage_texture.get_info",
    examples: [
      { description: "Inspect a texture and its importer settings", argv: ["tools", "run", "texture.info", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeTexture.png\"}"] }
    ]
  },
  {
    name: "texture.create",
    title: "Create Texture",
    description: "Create a texture asset. With a source path it copies an existing image file; otherwise it generates a solid-color .png/.jpg of the given width/height. Refuses to overwrite an existing asset unless overwrite is true. Use texture.generate for patterns/gradients/noise, or texture.import-settings to tune the importer afterward.",
    group: "texture",
    inputSchema: objSchema({
      path: { type: "string", description: "Destination Assets/* texture path. Supported: .png/.jpg/.jpeg/.tga/.psd/.exr (generated output is .png/.jpg only)." },
      source: { type: "string", description: "Optional source image file or Assets/* texture to copy. Omit to generate a solid-color texture." },
      width: { type: "integer", default: 1, description: "Generated texture width (pixels). Ignored when source is supplied." },
      height: { type: "integer", default: 1, description: "Generated texture height (pixels). Ignored when source is supplied." },
      color: { type: "string", description: "Solid fill color for generated textures, e.g. ffffff or #ff00ffff. Defaults to white." },
      overwrite: { type: "boolean", description: "Allow replacing an existing texture asset." }
    }, ["path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    cliCommand: "tools run texture.create",
    docsTopic: "texture",
    equivalentUnityMcp: "manage_texture.create",
    examples: [
      { description: "Create a solid color texture", argv: ["tools", "run", "texture.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedSolid.png\",\"width\":64,\"height\":64,\"color\":\"#ff0000\"}"] }
    ]
  },
  {
    name: "texture.sprite",
    title: "Texture Sprite",
    description: "Convert a texture asset's importer to Sprite type and optionally set sprite mode, pixels-per-unit and pivot. This reimports the texture. Use texture.info first to confirm the current type, and asset.info to find sprite sub-assets afterward.",
    group: "texture",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/* texture path." },
      spriteMode: { type: "string", enum: ["Single", "Multiple"], description: "Sprite import mode. Omit to keep the importer default." },
      pixelsPerUnit: { type: "integer", description: "Sprite pixels-per-unit. Must be > 0." },
      pivotX: { type: "string", description: "Sprite pivot X (0..1 normalized)." },
      pivotY: { type: "string", description: "Sprite pivot Y (0..1 normalized)." }
    }, ["path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    cliCommand: "tools run texture.sprite",
    docsTopic: "texture",
    equivalentUnityMcp: "manage_texture.create_sprite",
    examples: [
      { description: "Convert a texture to a Single sprite", argv: ["tools", "run", "texture.sprite", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeTexture.png\",\"spriteMode\":\"Single\",\"pixelsPerUnit\":100}"] }
    ]
  },
  {
    name: "texture.import-settings",
    title: "Texture Import Settings",
    description: "Read and/or apply TextureImporter settings on a texture asset. With no setting fields it is a read returning current importSettings; with any setting field it reimports. Enum values mirror Unity's TextureImporter. Use texture.info for a pure read that never reimports.",
    group: "texture",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/* texture path." },
      textureType: { type: "string", enum: TEXTURE_TYPE_ENUM, description: "Importer texture type." },
      readable: { type: "boolean", description: "isReadable: CPU-accessible pixel data." },
      mipmap: { type: "boolean", description: "Generate mipmaps." },
      sRGB: { type: "boolean", description: "sRGB color texture (disable for data/normal maps)." },
      alphaIsTransparency: { type: "boolean", description: "Treat alpha as transparency." },
      wrapMode: { type: "string", enum: WRAP_MODE_ENUM, description: "Texture wrap mode." },
      filterMode: { type: "string", enum: FILTER_MODE_ENUM, description: "Sampling filter mode." },
      compression: { type: "string", enum: COMPRESSION_ENUM, description: "Texture compression." },
      maxTextureSize: { type: "integer", description: "Max texture size (32/64/128/.../8192/16384)." }
    }, ["path"]),
    outputSchema: objSchema({
      texture: objSchema({ path: { type: "string" }, guid: { type: "string" } }),
      importSettings: objSchema({ textureType: { type: "string" }, wrapMode: { type: "string" }, filterMode: { type: "string" }, maxTextureSize: { type: "integer" }, textureCompression: { type: "string" } }),
      changed: { type: "boolean", description: "True when settings were applied and the asset reimported." }
    }),
    destructiveHint: false,
    sideEffects: ["asset-import"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    cliCommand: "tools run texture.import-settings",
    docsTopic: "texture",
    equivalentUnityMcp: "manage_texture.set_import_settings",
    examples: [
      { description: "Read current import settings (no mutation)", argv: ["tools", "run", "texture.import-settings", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeTexture.png\"}"] },
      { description: "Set compression and max size", argv: ["tools", "run", "texture.import-settings", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeTexture.png\",\"compression\":\"Compressed\",\"maxTextureSize\":512}"] }
    ]
  },
  {
    name: "texture.generate",
    title: "Generate Texture",
    description: "Generate a procedural texture and save it as PNG under Assets/. Patterns: solid, gradient (linear), radial, noise (Perlin), checkerboard, stripes, dots, grid, brick. color/color2 accept #hex; patternSize controls cell size. Supports --dry-run to preview planned effects without writing. Prefer this over texture.create for test/debug textures.",
    group: "texture",
    inputSchema: objSchema({
      path: { type: "string", description: "Destination Assets/*.png path." },
      width: { type: "integer", default: 256, description: "Texture width, clamped 1..4096." },
      height: { type: "integer", default: 256, description: "Texture height, clamped 1..4096." },
      pattern: { type: "string", enum: ["solid", "gradient", "radial", "noise", "checkerboard", "stripes", "dots", "grid", "brick"], default: "solid", description: "Procedural pattern." },
      color: { type: "string", description: "Primary color (#hex). Defaults to white." },
      color2: { type: "string", description: "Secondary color (#hex). Defaults to black." },
      patternSize: { type: "integer", default: 8, description: "Cell size in pixels for pattern types." }
    }, ["path"]),
    destructiveHint: true,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "low",
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run texture.generate",
    docsTopic: "texture",
    equivalentUnityMcp: "manage_texture.apply_pattern",
    examples: [
      { description: "Preview a checkerboard (dry-run)", argv: ["tools", "run", "texture.generate", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedChecker.png\",\"pattern\":\"checkerboard\",\"patternSize\":32}", "--dry-run"] },
      { description: "Generate a gradient texture", argv: ["tools", "run", "texture.generate", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedGradient.png\",\"pattern\":\"gradient\",\"color\":\"#ff0000\",\"color2\":\"#0000ff\"}"] }
    ]
  }
];
