#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deps="$root/.deps"
mkdir -p "$deps"
manifest="$root/native-dependencies.json"
read_manifest() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d[sys.argv[2]][sys.argv[3]] if len(sys.argv)==4 else d[sys.argv[2]][sys.argv[3]][sys.argv[4]])' "$manifest" "$@"; }
fetch() {
    local url="$1" hash="$2" path="$3"
    if [[ ! -f "$path" ]]; then curl -fL --retry 3 "$url" -o "$path"; fi
    echo "$hash  $path" | sha256sum -c -
}
version="$(read_manifest ghostty version)"
zig_version="$(read_manifest zig version)"
fetch "$(read_manifest ghostty url)" "$(read_manifest ghostty sha256)" "$deps/ghostty.tar.gz"
fetch "$(read_manifest zig linux url)" "$(read_manifest zig linux sha256)" "$deps/zig-linux.tar.xz"
source="$deps/libghostty-vt-$version"
zig="$deps/zig-x86_64-linux-$zig_version/zig"
[[ -d "$source" ]] || tar -xzf "$deps/ghostty.tar.gz" -C "$deps"
[[ -x "$zig" ]] || tar -xJf "$deps/zig-linux.tar.xz" -C "$deps"
prefix="$root/artifacts/native/linux-x64"
(cd "$source" && "$zig" build -Demit-lib-vt -Doptimize=ReleaseFast -Dtarget=x86_64-linux-gnu --prefix "$prefix")
export LD_LIBRARY_PATH="$prefix/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
dotnet run --project "$root/tests/AgentTerm.Terminal.Smoke/AgentTerm.Terminal.Smoke.csproj" -c Release

dotnet run --project "$root/tests/AgentTerm.Agents.Smoke/AgentTerm.Agents.Smoke.csproj" -c Release

dotnet run --project "$root/tests/AgentTerm.Copilot.Smoke/AgentTerm.Copilot.Smoke.csproj" -c Release -- --normalization-only
