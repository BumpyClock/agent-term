# Create ICO file from PNG for Windows installer with multiple resolutions
# Creates proper ICO with 16x16, 32x32, 48x48, and 256x256 pixel sizes

Add-Type -AssemblyName System.Drawing

$pngPath = Join-Path $PSScriptRoot "..\assets\agentterm.png"
$icoPath = Join-Path $PSScriptRoot "..\wix\agentterm.ico"

if (-not (Test-Path $pngPath)) {
    Write-Error "PNG file not found: $pngPath"
    exit 1
}

# Sizes to include in the ICO file
$sizes = @(16, 32, 48, 256)

# Load the source image
$sourceImage = [System.Drawing.Image]::FromFile((Resolve-Path $pngPath).Path)

# Create resized images and convert to PNG bytes
$imageData = @()
foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    $graphics.DrawImage($sourceImage, 0, 0, $size, $size)
    $graphics.Dispose()

    # Save as PNG to memory stream
    $ms = New-Object System.IO.MemoryStream
    $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $bitmap.Dispose()

    $imageData += @{
        Size = $size
        Data = $bytes
    }
}

$sourceImage.Dispose()

# Build ICO file
# ICO Header: 6 bytes
# - Reserved: 2 bytes (always 0)
# - Type: 2 bytes (1 for ICO)
# - Count: 2 bytes (number of images)

$icoStream = New-Object System.IO.MemoryStream

# Write ICO header
$writer = New-Object System.IO.BinaryWriter $icoStream
$writer.Write([UInt16]0)           # Reserved
$writer.Write([UInt16]1)           # Type: 1 = ICO
$writer.Write([UInt16]$sizes.Count) # Number of images

# Calculate starting offset for image data
# Header (6) + Directory entries (16 * count)
$dataOffset = 6 + (16 * $sizes.Count)

# Write directory entries
foreach ($img in $imageData) {
    $width = if ($img.Size -eq 256) { 0 } else { [byte]$img.Size }
    $height = if ($img.Size -eq 256) { 0 } else { [byte]$img.Size }

    $writer.Write([byte]$width)        # Width (0 means 256)
    $writer.Write([byte]$height)       # Height (0 means 256)
    $writer.Write([byte]0)             # Color palette (0 for PNG)
    $writer.Write([byte]0)             # Reserved
    $writer.Write([UInt16]1)           # Color planes
    $writer.Write([UInt16]32)          # Bits per pixel
    $writer.Write([UInt32]$img.Data.Length) # Size of image data
    $writer.Write([UInt32]$dataOffset) # Offset to image data

    $dataOffset += $img.Data.Length
}

# Write image data
foreach ($img in $imageData) {
    $writer.Write($img.Data)
}

$writer.Flush()

# Write to file
[System.IO.File]::WriteAllBytes($icoPath, $icoStream.ToArray())

$writer.Dispose()
$icoStream.Dispose()

$fileInfo = Get-Item $icoPath
Write-Host "ICO file created: $icoPath"
Write-Host "File size: $($fileInfo.Length) bytes"
Write-Host "Includes sizes: $($sizes -join ', ') pixels"
