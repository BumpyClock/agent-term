$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('artifacts/provenance-gap-checks/' + [Guid]::NewGuid().ToString('N'))
$platform = if ($IsLinux) { 'linux-x64' } else { 'win-x64' }
$relative = if ($IsLinux) { 'lib/libghostty-vt.so' } else { 'bin/ghostty-vt.dll' }
$native = Join-Path $fixture "artifacts/native/$platform"
$library = Join-Path $native $relative
New-Item -ItemType Directory -Force (Split-Path $library), (Join-Path $fixture 'scripts') | Out-Null
Copy-Item (Join-Path $root 'Directory.Build.targets') $fixture
Copy-Item (Join-Path $root 'scripts/VerifyNativeArtifact.cs') (Join-Path $fixture 'scripts')
Copy-Item (Join-Path $root 'native-dependencies.json') $fixture
$pin = Get-Content (Join-Path $fixture 'native-dependencies.json') -Raw | ConvertFrom-Json
@'
<Project>
  <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
  <Import Project="Directory.Build.targets" />
  <Target Name="PrepareForBuild" />
  <Target Name="ComputeFilesToPublish" />
  <Target Name="Build" DependsOnTargets="PrepareForBuild" />
  <Target Name="Publish" DependsOnTargets="ComputeFilesToPublish" />
</Project>
'@ | Set-Content (Join-Path $fixture 'fixture.proj')
function Save-Provenance { $provenance | ConvertTo-Json | Set-Content (Join-Path $native 'native-build.json') }
function Reset-Fixture {
    [IO.File]::WriteAllText($library, 'isolated fixture bytes')
    $script:provenance = [ordered]@{
        ghostty = $pin.ghostty.version
        sourceSha256 = $pin.ghostty.sha256
        zig = $pin.zig.version
        dllSha256 = (Get-FileHash $library).Hash
    }
    Save-Provenance
}
function Check($name, $target, $success) {
    $output = & dotnet msbuild (Join-Path $fixture 'fixture.proj') "-t:$target" -nologo -v:minimal 2>&1
    $code = $LASTEXITCODE
    $output | Set-Content (Join-Path $fixture "$name.log")
    if (($code -eq 0) -ne $success) { throw "$name unexpected exit $code. $output" }
    if (-not $success -and "$output" -notmatch 'Rebuild from the repository root') { throw "$name missing rebuild guidance. $output" }
    Write-Host "PASS $name"
}
Reset-Fixture
Check 'valid-build' Build $true
Check 'valid-publish' Publish $true
$provenance.ghostty = '1.0.0'; Save-Provenance
Check 'stale-version' Build $false
Reset-Fixture
$provenance.sourceSha256 = 'bad'; Save-Provenance
Check 'stale-source' Build $false
Reset-Fixture
$provenance.zig = 'old'; Save-Provenance
Check 'stale-zig' Build $false
Reset-Fixture
[IO.File]::AppendAllText($library, 'tampered')
Check 'tampered-build' Build $false
Check 'tampered-publish' Publish $false
Reset-Fixture
Remove-Item -LiteralPath $library
Check 'missing-library' Build $false
Reset-Fixture
Remove-Item -LiteralPath (Join-Path $native 'native-build.json')
Check 'missing-manifest' Build $false
Set-Content (Join-Path $native 'native-build.json') '{ broken'
Check 'malformed-manifest' Build $false
Write-Host "Evidence retained in $fixture"
