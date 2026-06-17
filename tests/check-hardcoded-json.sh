#!/usr/bin/env bash
# check-hardcoded-json.sh — Anti-regression check for hardcoded JSON patterns
# Scans C# source files for patterns that should have been migrated to UnityCliJsonWriter.
# Returns 0 if clean, 1 if violations found.
#
# Usage:
#   bash tests/check-hardcoded-json.sh [project-root]
#
# The project-root defaults to the current directory.

set -euo pipefail

ROOT="${1:-.}"
FOUND=0

# Allowlist: files/patterns that are explicitly permitted to use raw JSON
# Format: <relative-path-pattern>
ALLOWLIST=(
    # UnityCliJsonWriter itself implements JSON writing — its internal string ops are by design
    "unitypkg/Editor/Infrastructure/UnityCliJsonWriter.cs"

    # UnityCliEnvelope uses writer internally — any remaining patterns are envelope-internal
    "unitypkg/Editor/Infrastructure/UnityCliEnvelope.cs"

    # Tool attribute schemas are compile-time constants, not runtime JSON output
    "unitypkg/Editor/Samples/SampleEchoTool.cs:InputSchema="
    "unitypkg/Editor/Samples/SampleEchoTool.cs:OutputSchema="
    "unitypkg/Editor/Samples/SampleRuntimeEchoTool.cs:InputSchema="
    "unitypkg/Editor/Samples/SampleRuntimeEchoTool.cs:OutputSchema="
    "unitypkg/Editor/Samples/RuntimeWaitTool.cs:InputSchema="
    "unitypkg/Editor/Samples/RuntimeWaitTool.cs:OutputSchema="

    # Default {} values for optional JSON args are not construction
    ":\"{}\""
    ":\\\"{}\\\""

    # Test fixtures may contain raw JSON
    "tests/"

    # Shader template generation (not JSON)
    "ShaderCommands.cs:EscapeShaderName"

    # Debug/fingerprint strings use \n separator, not JSON
    "ProjectToolRegistry.cs:readOnlyHint="
    "ProjectToolRegistry.cs:destructiveHint="
    "ProjectToolRegistry.cs:architectureSensitive="
    "ProjectToolRegistry.cs:supportsJob="
)

# Pattern 1: SuccessJson(" with string concatenation
echo "=== Checking SuccessJson(\" with string concat ==="
while IFS= read -r line; do
    file=$(echo "$line" | cut -d: -f1 | sed "s|^$ROOT/||")
    allowed=false
    for pattern in "${ALLOWLIST[@]}"; do
        if [[ "$file" == *"${pattern%%:*}"* ]] || [[ "$line" == *"$pattern"* ]]; then
            allowed=true
            break
        fi
    done
    if [[ "$allowed" == "false" ]]; then
        echo "  VIOLATION: $line"
        FOUND=1
    fi
done < <(grep -rn 'SuccessJson("' "$ROOT/unitypkg/Editor/" --include="*.cs" 2>/dev/null || true)

# Pattern 2: items.Add with JSON object string concatenation (Quote() based)
echo "=== Checking items.Add with Quote-based JSON ==="
while IFS= read -r line; do
    file=$(echo "$line" | cut -d: -f1 | sed "s|^$ROOT/||")
    allowed=false
    for pattern in "${ALLOWLIST[@]}"; do
        if [[ "$file" == *"${pattern%%:*}"* ]] || [[ "$line" == *"$pattern"* ]]; then
            allowed=true
            break
        fi
    done
    if [[ "$allowed" == "false" ]]; then
        echo "  VIOLATION: $line"
        FOUND=1
    fi
done < <(grep -rn 'items\.Add.*Quote(' "$ROOT/unitypkg/Editor/" --include="*.cs" 2>/dev/null || true)

# Pattern 3: string.Join to build JSON arrays
echo "=== Checking string.Join for JSON array construction ==="
while IFS= read -r line; do
    file=$(echo "$line" | cut -d: -f1 | sed "s|^$ROOT/||")
    # Skip non-JSON string.Join usages
    if echo "$line" | grep -qE 'FindAssets|filterParts|names\.ToArray|HierarchyPath|CoverageScope|ArtifactKinds|RequiredInput'; then
        continue
    fi
    allowed=false
    for pattern in "${ALLOWLIST[@]}"; do
        if [[ "$file" == *"${pattern%%:*}"* ]] || [[ "$line" == *"$pattern"* ]]; then
            allowed=true
            break
        fi
    done
    if [[ "$allowed" == "false" ]]; then
        echo "  VIOLATION: $line"
        FOUND=1
    fi
done < <(grep -rn 'string\.Join.*items\|string\.Join.*effects\|string\.Join.*quotedCommands\|string\.Join.*tools' "$ROOT/unitypkg/Editor/" --include="*.cs" 2>/dev/null || true)

# Pattern 4: QuotePair (removed — should not exist)
echo "=== Checking QuotePair usage ==="
while IFS= read -r line; do
    echo "  VIOLATION: $line"
    FOUND=1
done < <(grep -rn 'QuotePair' "$ROOT/unitypkg/Editor/" --include="*.cs" 2>/dev/null || true)

# Pattern 5: Custom Escape methods (replaced by writer)
echo "=== Checking custom Escape methods ==="
while IFS= read -r line; do
    file=$(echo "$line" | cut -d: -f1 | sed "s|^$ROOT/||")
    # Allow ShaderCommands.EscapeShaderName (not JSON)
    if echo "$line" | grep -q 'EscapeShaderName'; then
        continue
    fi
    echo "  VIOLATION: $line"
    FOUND=1
done < <(grep -rn 'private static string Escape(' "$ROOT/unitypkg/Editor/" --include="*.cs" 2>/dev/null || true)

# Pattern 6: $"" string interpolation for JSON construction
echo "=== Checking \$\"\" interpolation for JSON ==="
while IFS= read -r line; do
    file=$(echo "$line" | cut -d: -f1 | sed "s|^$ROOT/||")
    # Skip non-JSON interpolation
    if echo "$line" | grep -qE 'BoolArgAtPath|definition\.'; then
        continue
    fi
    allowed=false
    for pattern in "${ALLOWLIST[@]}"; do
        if [[ "$file" == *"${pattern%%:*}"* ]] || [[ "$line" == *"$pattern"* ]]; then
            allowed=true
            break
        fi
    done
    if [[ "$allowed" == "false" ]]; then
        echo "  VIOLATION: $line"
        FOUND=1
    fi
done < <(grep -rn '\$".*{.*Quote\|Bool\|Number' "$ROOT/unitypkg/Editor/" --include="*.cs" 2>/dev/null || true)

# Summary
echo ""
if [[ "$FOUND" == "0" ]]; then
    echo "✅ All checks passed — no hardcoded JSON patterns detected"
else
    echo "❌ Violations found — see above for details"
fi

exit $FOUND
