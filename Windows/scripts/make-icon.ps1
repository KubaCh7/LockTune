# Builds Windows/LockTune/Assets (LockTune.ico + LockTune.png) from the Mac app icon PNGs.
# The .ico stores PNG images directly (supported since Windows Vista).
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot '../../LockTune/Resources/Assets.xcassets/AppIcon.appiconset'
$out = Join-Path $PSScriptRoot '../LockTune/Assets'
New-Item -ItemType Directory -Force $out | Out-Null

$images = @(
    @{ Size = 16;  File = 'icon_16x16.png' },
    @{ Size = 32;  File = 'icon_32x32.png' },
    @{ Size = 64;  File = 'icon_32x32@2x.png' },
    @{ Size = 128; File = 'icon_128x128.png' },
    @{ Size = 256; File = 'icon_256x256.png' }
)
$data = $images | ForEach-Object { , [IO.File]::ReadAllBytes((Join-Path $src $_.File)) }

$stream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($stream)
$writer.Write([UInt16]0)               # reserved
$writer.Write([UInt16]1)               # type: icon
$writer.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
for ($i = 0; $i -lt $images.Count; $i++) {
    $size = $images[$i].Size
    $side = if ($size -ge 256) { 0 } else { $size }  # 0 means 256
    $writer.Write([byte]$side)
    $writer.Write([byte]$side)
    $writer.Write([byte]0)             # palette
    $writer.Write([byte]0)             # reserved
    $writer.Write([UInt16]1)           # planes
    $writer.Write([UInt16]32)          # bits per pixel
    $writer.Write([UInt32]$data[$i].Length)
    $writer.Write([UInt32]$offset)
    $offset += $data[$i].Length
}
foreach ($bytes in $data) { $writer.Write($bytes) }
$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $out 'LockTune.ico'), $stream.ToArray())
Copy-Item (Join-Path $src 'icon_256x256.png') (Join-Path $out 'LockTune.png') -Force
Get-ChildItem $out | Format-Table Name, Length
