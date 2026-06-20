---
name: probuilder
description: Operate ProBuilder mesh editing tools in Unity via alps-unity-cli. Use when you need to create, edit, inspect, or repair ProBuilder meshes through the 36 fine-grained ProBuilder extension tools. Requires the com.unity.probuilder Unity package.
---

# ProBuilder Extension — AI Operator Skill

This skill teaches you how to use the ProBuilder extension tools in `unity_cli` for 3D mesh modeling and level design. The ProBuilder extension provides 36 single-action tools for shape creation, face/edge/vertex editing, smoothing, and mesh repair.

## Prerequisites

The target Unity project must have `com.unity.probuilder` installed. If missing, tools return `E_EXTENSION_DEPENDENCY_MISSING`.

**Check availability:**
```bash
unity_cli probuilder ping
unity_cli extensions list --project <path>
```

**Install if needed:**
```bash
unity_cli extensions install probuilder --project <path>
unity_cli package install com.unity.probuilder --project <path>
```

---

## Discovery Workflow

### List all ProBuilder tools
```bash
unity_cli tools list --group probuilder
```

### Describe a specific tool
```bash
unity_cli tools describe probuilder.mesh-info
unity_cli tools describe probuilder.create-shape
unity_cli tools describe probuilder.extrude-faces
```

### Run a tool
```bash
unity_cli tools run probuilder.create-shape --input '{"shape":"cube","name":"MyCube"}'
```

Or use the CLI shorthand:
```bash
unity_cli probuilder create-shape cube --name "MyCube"
```

---

## Tool Categories

### 🔍 Utility & Inspection (3 tools)

| Tool | Purpose |
|------|---------|
| `probuilder.ping` | Check ProBuilder package and bridge availability |
| `probuilder.mesh-info` | Get mesh vertex/face/edge counts and bounds |
| `probuilder.validate-mesh` | Validate mesh for degenerate triangles and unused vertices |

**Typical flow:**
```bash
unity_cli probuilder ping
unity_cli probuilder mesh-info <target>
unity_cli probuilder validate-mesh <target>
```

### 🏗️ Shape Creation (2 tools)

| Tool | Purpose |
|------|---------|
| `probuilder.create-shape` | Create a ProBuilder primitive (cube, sphere, cylinder, plane, cone, torus, pipe, arch, stair) |
| `probuilder.create-poly-shape` | Create a mesh from 2D polygon points extruded to a height |

**Examples:**
```bash
unity_cli probuilder create-shape cube --name "Wall"
unity_cli probuilder create-shape sphere --name "Ball" --parent "Environment"
unity_cli probuilder create-shape stair --name "Stairs"
```

### ✂️ Face Operations (10 tools)

| Tool | Purpose |
|------|---------|
| `probuilder.extrude-faces` | Extrude faces by a distance |
| `probuilder.delete-faces` | Delete faces from mesh |
| `probuilder.detach-faces` | Detach faces into a new GameObject |
| `probuilder.merge-faces` | Merge faces into one |
| `probuilder.subdivide` | Subdivide faces (increase face count) |
| `probuilder.flip-normals` | Flip face normals |
| `probuilder.select-faces` | Select faces by direction/grow/flood/loop/ring |
| `probuilder.set-face-material` | Assign material to faces |
| `probuilder.set-face-color` | Set vertex color on faces |
| `probuilder.set-face-uvs` | Set UV parameters on faces |

**Examples:**
```bash
unity_cli probuilder extrude-faces <target> --indices 1,2,3 --distance 0.5
unity_cli probuilder delete-faces <target> --indices 4,5
unity_cli probuilder set-face-material <target> --material "Assets/Materials/Wall.mat"
```

### 🔗 Edge Operations (4 tools)

| Tool | Purpose |
|------|---------|
| `probuilder.extrude-edges` | Extrude edges |
| `probuilder.bevel-edges` | Bevel edges by an amount |
| `probuilder.bridge-edges` | Bridge between two edges |
| `probuilder.connect-elements` | Connect edges/faces with a new edge loop |

### 🔀 Mesh Combine/Merge (4 tools)

| Tool | Purpose |
|------|---------|
| `probuilder.combine-meshes` | Combine multiple meshes into one |
| `probuilder.merge-objects` | Merge GameObjects into one ProBuilder object |
| `probuilder.duplicate-and-flip` | Duplicate faces and flip normals |
| `probuilder.create-polygon` | Create a new polygon face from vertices |

### 📍 Vertex Operations (7 tools)

| Tool | Purpose |
|------|---------|
| `probuilder.merge-vertices` | Merge vertices into one |
| `probuilder.weld-vertices` | Weld vertices within a radius |
| `probuilder.split-vertices` | Split shared vertices |
| `probuilder.move-vertices` | Move vertices by offset |
| `probuilder.insert-vertex` | Insert vertex on edge/face |
| `probuilder.append-vertices-to-edge` | Append vertices along edge |
| `probuilder.create-polygon` | Create polygon from selected vertices |

### 🎨 Smoothing & Utility (7 tools)

| Tool | Purpose |
|------|---------|
| `probuilder.convert-to-probuilder` | Convert regular mesh to ProBuilder mesh |
| `probuilder.set-smoothing` | Set smoothing group for faces |
| `probuilder.auto-smooth` | Auto-smooth based on angle threshold |
| `probuilder.center-pivot` | Center pivot to bounds center |
| `probuilder.freeze-transform` | Bake transform into vertices |
| `probuilder.set-pivot` | Set pivot to specific position |
| `probuilder.repair-mesh` | Repair degenerate triangles and unused vertices |

---

## Common Workflows

### Create and Edit a Wall
```bash
# 1. Create a cube shape
unity_cli probuilder create-shape cube --name "Wall"

# 2. Extrude the top face to make it taller
unity_cli probuilder extrude-faces "Wall" --indices 1 --distance 2.0

# 3. Check the result
unity_cli probuilder mesh-info "Wall"
```

### Repair and Validate a Mesh
```bash
# 1. Validate the mesh
unity_cli probuilder validate-mesh <target>

# 2. Repair issues
unity_cli probuilder repair-mesh <target>

# 3. Re-validate
unity_cli probuilder validate-mesh <target>
```

### Combine Multiple Meshes
```bash
unity_cli probuilder combine-meshes --targets "Wall1,Wall2,Wall3" --name "CombinedWall"
```

---

## Error Handling

### Dependency Missing
- **Error:** `E_EXTENSION_DEPENDENCY_MISSING` or `E_PROBUILDER_PACKAGE_NOT_FOUND`
- **Cause:** `com.unity.probuilder` not installed in the project
- **Recovery:**
  ```bash
  unity_cli package install com.unity.probuilder --project <path>
  ```

### Target Not Found
- **Error:** `E_TARGET_NOT_FOUND`
- **Cause:** The specified GameObject does not exist or is not a ProBuilder mesh
- **Recovery:** Verify the target name, use `tools run scene.hierarchy` to list objects

### Schema Invalid
- **Error:** `E_SCHEMA_INVALID`
- **Cause:** Required parameters missing or wrong type
- **Recovery:** Run `tools describe probuilder.<tool>` to see the expected schema

---

## Batch Job Mode

Long-running operations (create, extrude, combine, etc.) can be run as batch jobs:

```bash
unity_cli probuilder create-shape cube --job --wait
unity_cli probuilder combine-meshes --targets "A,B" --job --wait
```

Check job status:
```bash
unity_cli job status --id <jobId>
unity_cli job logs --id <jobId> --tail 50
```

---

## Diagnostics

```bash
# Full doctor check including ProBuilder extension
unity_cli doctor --project <path>

# Extension-specific diagnostics
unity_cli extensions doctor --project <path>

# Check ProBuilder availability
unity_cli probuilder ping
```
