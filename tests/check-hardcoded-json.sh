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
# Format:
#   path/to/file.cs             allow every match in a file
#   path/to/file.cs:needle      allow lines in a file containing needle
#   :needle                     allow any line containing needle
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
    ':"{}"'
    ':\"{}\"'

    # Empty arrays are allowed when returning no items/hits/matches
    ':return "[]"'

    # Test fixtures may contain raw JSON
    "tests/"

    # Shader template generation (not JSON)
    "ShaderCommands.cs:EscapeShaderName"

    # Debug/fingerprint strings use \n separator, not JSON
    "ProjectToolRegistry.cs:destructiveHint="
    "ProjectToolRegistry.cs:architectureSensitive="
    "ProjectToolRegistry.cs:supportsJob="
)

is_allowed() {
    local file="$1"
    local line="$2"
    local pattern path needle

    for pattern in "${ALLOWLIST[@]}"; do
        if [[ "$pattern" == :* ]]; then
            needle="${pattern:1}"
            if [[ "$line" == *"$needle"* ]]; then
                return 0
            fi
        elif [[ "$pattern" == *:* ]]; then
            path="${pattern%%:*}"
            needle="${pattern#*:}"
            if [[ "$file" == *"$path"* && "$line" == *"$needle"* ]]; then
                return 0
            fi
        elif [[ "$file" == *"$pattern"* ]]; then
            return 0
        fi
    done

    return 1
}

check_lines() {
    local title="$1"
    local skip_regex="${2:-}"
    shift 2

    echo "=== Checking $title ==="
    while IFS= read -r line; do
        local file
        file=$(echo "$line" | cut -d: -f1 | sed "s|^$ROOT/||")
        if [[ -n "$skip_regex" ]] && echo "$line" | grep -qE "$skip_regex"; then
            continue
        fi
        if is_allowed "$file" "$line"; then
            continue
        fi
        echo "  VIOLATION: $line"
        FOUND=1
    done < <("$@" 2>/dev/null || true)
}

check_lines 'SuccessJson(" with string concat' '' \
    grep -rn 'SuccessJson("' "$ROOT/unitypkg/Editor/" --include="*.cs"

check_lines 'items.Add with Quote-based JSON' '' \
    grep -rn 'items\.Add.*Quote(' "$ROOT/unitypkg/Editor/" --include="*.cs"

check_lines 'string.Join for JSON array construction' \
    'FindAssets|filterParts|names\.ToArray|HierarchyPath|CoverageScope|ArtifactKinds|RequiredInput' \
    grep -rn 'string\.Join.*items\|string\.Join.*effects\|string\.Join.*quotedCommands\|string\.Join.*tools' "$ROOT/unitypkg/Editor/" --include="*.cs"

check_lines 'QuotePair usage' '' \
    grep -rn 'QuotePair' "$ROOT/unitypkg/Editor/" --include="*.cs"

check_lines 'custom Escape methods' 'EscapeShaderName' \
    grep -rn 'private static string Escape(' "$ROOT/unitypkg/Editor/" --include="*.cs"

check_lines '$"" interpolation for JSON' 'BoolArgAtPath|definition\.' \
    grep -rnE '\$".*\{.*(Quote|Bool|Number)' "$ROOT/unitypkg/Editor/" --include="*.cs"

check_lines 'return "{ JSON object literals' '' \
    grep -rnF 'return "{' "$ROOT/unitypkg/Editor/" --include="*.cs"

check_lines 'return "[ JSON array literals' '' \
    grep -rnF 'return "[' "$ROOT/unitypkg/Editor/" --include="*.cs"

# Summary
echo ""
if [[ "$FOUND" == "0" ]]; then
    echo "✅ All checks passed — no hardcoded JSON patterns detected"
else
    echo "❌ Violations found — see above for details"
fi

exit $FOUND
