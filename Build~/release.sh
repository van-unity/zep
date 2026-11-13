#!/usr/bin/env bash
# Builds the publishable package into Build~/out/<package name>/:
#   - everything under Editor ships as source
#   - tests, internal documentation and agent files are left out
#
# Usage: Build~/release.sh [--no-verify]
#   --no-verify   skip compiling the package sources for each installed Unity 6 editor
#                 (only for machines without a Unity editor; never for a real release)
#
# Environment:
#   UNITY_EDITORS  colon-separated Unity "Contents" (macOS) or "Editor/Data" (Linux)
#                  directories to verify against. Default: every Unity 6 editor
#                  installed through Unity Hub.
set -euo pipefail

VERIFY=1
for arg in "$@"; do
  case "$arg" in
    --no-verify) VERIFY=0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

BUILD="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG="$(dirname "$BUILD")"
OUT="$BUILD/out"
ASSEMBLY="Zep.Core"

fail() { echo "release: $*" >&2; exit 1; }
step() { echo; echo "== $*"; }

json_field() { sed -n "s/^[[:space:]]*\"$1\":[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$PKG/package.json" | head -1; }
NAME="$(json_field name)"
VERSION="$(json_field version)"
[ -n "$NAME" ] && [ -n "$VERSION" ] || fail "could not read name/version from package.json"
DIST="$OUT/$NAME"

rm -rf "$OUT"
mkdir -p "$OUT/core"

# ---------------------------------------------------------------------------
step "Run the Core tests"
dotnet test "$BUILD/Core.Tests" -nologo -v quiet --logger "console;verbosity=minimal" > "$OUT/core-tests.log" 2>&1 \
  || { tail -40 "$OUT/core-tests.log" >&2; fail "Core tests failed"; }
grep -E '^(Passed|Failed)!' "$OUT/core-tests.log" || true

# ---------------------------------------------------------------------------
step "Build $ASSEMBLY"
# Not shipped. It proves Core is still plain netstandard2.1 with no Unity
# reference, and the verify step compiles Bridge and UI against it.
dotnet build "$BUILD/Core/$ASSEMBLY.csproj" -c Release -nologo -clp:ErrorsOnly -o "$OUT/core"
[ -f "$OUT/core/$ASSEMBLY.dll" ] || fail "build produced no $ASSEMBLY.dll"

# ---------------------------------------------------------------------------
step "Assemble $NAME $VERSION"
mkdir -p "$DIST/Documentation~"
# Allow-list: a new top-level file is not published until it is added here.
for file in package.json README.md CHANGELOG.md LICENSE.md "Third Party Notices.md"; do
  cp "$PKG/$file" "$PKG/$file.meta" "$DIST/"
done
cp "$PKG/Editor.meta" "$DIST/"
cp "$PKG/Documentation~/overview.md" "$DIST/Documentation~/"
rsync -a --exclude '.*' "$PKG/Editor/" "$DIST/Editor/"

# ---------------------------------------------------------------------------
if [ "$VERIFY" = 1 ]; then
  step "Verify the package sources compile on each installed Unity 6 editor"
  DOTNET_ROOT_DIR="$(dirname "$(dotnet --list-sdks | tail -1 | sed -E 's/^[^[]*\[(.*)\]$/\1/')")"
  SDK_LINE="$(dotnet --list-sdks | tail -1)"
  CSC="$DOTNET_ROOT_DIR/sdk/${SDK_LINE%% *}/Roslyn/bincore/csc.dll"
  [ -f "$CSC" ] || fail "Roslyn compiler not found at $CSC"

  editors=()
  if [ -n "${UNITY_EDITORS:-}" ]; then
    IFS=':' read -r -a editors <<< "$UNITY_EDITORS"
  else
    for dir in /Applications/Unity/Hub/Editor/6000.*/Unity.app/Contents "$HOME"/Unity/Hub/Editor/6000.*/Editor/Data; do
      [ -d "$dir" ] && editors+=("$dir")
    done
  fi
  [ "${#editors[@]}" -gt 0 ] || fail "no Unity 6 editor found; set UNITY_EDITORS, or pass --no-verify"

  compile() { # <log> <output dll> <source dir> <extra refs...>
    local log="$1" out="$2" src="$3"; shift 3
    local sources=()
    while IFS= read -r -d '' f; do sources+=("$f"); done < <(find "$src" -name '*.cs' -print0)
    dotnet "$CSC" -target:library -nologo -nostdlib -noconfig -langversion:9 -nowarn:CS1701,CS1702 \
        "-define:$defines" "${refs[@]}" "$@" "-out:$out" "${sources[@]}" > "$log" 2>&1 \
      || { grep 'error CS' "$log" | head -20 >&2; fail "$(basename "$out") does not compile on Unity $version"; }
  }

  for contents in "${editors[@]}"; do
    version="$(echo "$contents" | grep -oE '6000\.[0-9]+\.[0-9a-z]+' | head -1)"
    minor="$(echo "$version" | cut -d. -f2)"
    managed="$contents/Resources/Scripting/Managed"; netstandard="$contents/Resources/Scripting/NetStandard/ref/2.1.0/netstandard.dll"
    [ -d "$managed" ] || { managed="$contents/Managed"; netstandard="$contents/NetStandard/ref/2.1.0/netstandard.dll"; }
    [ -f "$managed/UnityEditor.dll" ] && [ -f "$netstandard" ] || fail "unrecognised Unity layout: $contents"

    defines="UNITY_EDITOR"
    for n in $(seq 0 "$minor"); do defines="$defines;UNITY_6000_${n}_OR_NEWER"; done
    refs=("-r:$netstandard" "-r:$managed/UnityEngine.dll" "-r:$managed/UnityEditor.dll")
    for module in "$managed"/UnityEngine/*.dll; do refs+=("-r:$module"); done

    work="$OUT/verify/$version"; mkdir -p "$work"
    internal=()
    if [ "$minor" -ge 3 ]; then  # the asmdef is constrained to UNITY_6000_3_OR_NEWER
      compile "$work/internalbridge.log" "$work/Unity.InternalAPIEditorBridge.024.dll" "$DIST/Editor/InternalBridge"
      internal=("-r:$work/Unity.InternalAPIEditorBridge.024.dll")
    fi
    core="-r:$OUT/core/$ASSEMBLY.dll"
    compile "$work/bridge.log" "$work/Zep.Editor.Bridge.dll" "$DIST/Editor/Bridge" "$core"
    compile "$work/ui.log" "$work/Zep.Editor.UI.dll" "$DIST/Editor/UI" "$core" \
        "-r:$work/Zep.Editor.Bridge.dll" ${internal[@]+"${internal[@]}"}
    echo "Unity $version: ok"
  done
else
  echo; echo "!! --no-verify: the package sources were NOT compiled for any Unity editor"
fi

# ---------------------------------------------------------------------------
step "Pack"
# UPM tarballs use the npm layout: one top-level "package" directory.
rm -rf "$OUT/package" && cp -R "$DIST" "$OUT/package"
(cd "$OUT" && COPYFILE_DISABLE=1 tar -czf "$NAME-$VERSION.tgz" package)
rm -rf "$OUT/package"

echo
echo "Package folder : $DIST"
echo "Tarball        : $OUT/$NAME-$VERSION.tgz"
