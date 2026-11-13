#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deps="$root/.deps"
mkdir -p "$deps"
manifest="$root/native-dependencies.json"
read_manifest() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); v=d[sys.argv[2]]
for k in sys.argv[3:]: v=v.get(k, "") if isinstance(v, dict) else ""
print(v)' "$manifest" "$@"; }
# Archives and extractions are named by version and stamped with the archive hash, so a pin change never
# reuses an older download or source tree.
fetch() {
    local url="$1" hash="$2" path="$3"
    if [[ -f "$path" ]] && ! echo "$hash  $path" | sha256sum -c --status -; then rm -f "$path"; fi
    if [[ ! -f "$path" ]]; then
        curl -fL --retry 3 "$url" -o "$path.partial"
        echo "$hash  $path.partial" | sha256sum -c - || { rm -f "$path.partial"; exit 1; }
        mv "$path.partial" "$path"
    fi
}
expand_stamped() {
    local archive="$1" hash="$2" directory="$3" flags="$4"
    local stamp="$directory/.agentterm-archive-sha256"
    if [[ -f "$stamp" && "$(cat "$stamp")" == "$hash" ]]; then return; fi
    rm -rf "$directory"
    local staging
    staging="$(mktemp -d "$deps/extract-XXXXXX")"
    tar "$flags" "$archive" -C "$staging"
    mv "$staging/$(basename "$directory")" "$directory"
    rm -rf "$staging"
    printf '%s' "$hash" > "$stamp"
}
version="$(read_manifest ghostty version)"
ghostty_hash="$(read_manifest ghostty sha256)"
zig_version="$(read_manifest zig version)"
zig_hash="$(read_manifest zig linux sha256)"
source_archive="$deps/libghostty-vt-$version.tar.gz"
zig_archive="$deps/zig-x86_64-linux-$zig_version.tar.xz"
fetch "$(read_manifest ghostty url)" "$ghostty_hash" "$source_archive"
signature_url="$(read_manifest ghostty signature)"
if [[ -n "$signature_url" ]]; then
    if command -v minisign >/dev/null; then
        [[ -f "$source_archive.minisig" ]] || curl -fL --retry 3 "$signature_url" -o "$source_archive.minisig"
        minisign -Vm "$source_archive" -x "$source_archive.minisig" -P "$(read_manifest ghostty minisignPublicKey)" >/dev/null
    else
        echo "warning: minisign is not installed; relying on the pinned SHA256 only." >&2
    fi
fi
fetch "$(read_manifest zig linux url)" "$zig_hash" "$zig_archive"
source="$deps/libghostty-vt-$version"
zig="$deps/zig-x86_64-linux-$zig_version/zig"
expand_stamped "$source_archive" "$ghostty_hash" "$source" -xzf
expand_stamped "$zig_archive" "$zig_hash" "$deps/zig-x86_64-linux-$zig_version" -xJf
prefix="$root/artifacts/native/linux-x64"
rm -f "$prefix/native-build.json"
(cd "$source" && "$zig" build -Demit-lib-vt -Doptimize=ReleaseFast -Dtarget=x86_64-linux-gnu --prefix "$prefix")
# ctypes runs in a separate process and attests the exact library bytes before managed builds.
python3 - "$manifest" "$prefix" <<'PY'
import ctypes, hashlib, json, pathlib, sys
pin = json.load(open(sys.argv[1]))
prefix = pathlib.Path(sys.argv[2])
library = prefix / 'lib/libghostty-vt.so'
native = ctypes.CDLL(str(library))
native.ghostty_version.restype = ctypes.c_char_p
version = native.ghostty_version().decode('utf-8')
if version != pin['ghostty']['version']:
    raise SystemExit(f"Built native library reports {version}; expected {pin['ghostty']['version']}")
(prefix / 'native-build.json').write_text(json.dumps(dict(
    ghostty=version, sourceSha256=pin['ghostty']['sha256'],
    zig=pin['zig']['version'], dllSha256=hashlib.sha256(library.read_bytes()).hexdigest())))
PY
export LD_LIBRARY_PATH="$prefix/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
dotnet run --project "$root/tests/AgentTerm.Terminal.Smoke/AgentTerm.Terminal.Smoke.csproj" -c Release

dotnet run --project "$root/tests/AgentTerm.Agents.Smoke/AgentTerm.Agents.Smoke.csproj" -c Release

dotnet run --project "$root/tests/AgentTerm.Copilot.Smoke/AgentTerm.Copilot.Smoke.csproj" -c Release -- --normalization-only
