$wipPath = "C:\Program Files\WiX Toolset v6.0\bin\x64"
$currentPath = [System.Environment]::GetEnvironmentVariable("PATH", "User")
if ($currentPath -notlike "*$wipPath*") {
    $newPath = "$currentPath;$wipPath"
    [System.Environment]::SetEnvironmentVariable("PATH", $newPath, "User")
}