# Regenerates every logo file from the path below.
#
# The mark is a P that is also a fountain pen nib: the stem sharpens to a writing
# point and a slit runs from it up into the bowl. Paths use a 64 unit grid.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationCore, WindowsBase

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $repositoryRoot "src\ParaPhraser.Desktop\Assets"
$storeAssets = Join-Path $repositoryRoot "packaging\Assets"

# OverlayAccentGradientBrush.
$gradientStart = "#8B5CF6"
$gradientEnd = "#4F8CFF"

# Also pasted into the logo Path in OverlayWindow.xaml.
$mark ="M8 4H34A22 22 0 0 1 34 48H27L17.5 63L8 48Z" +
    "M21 16H33A10 10 0 0 1 33 36H21Z" +
    "M21 36H25L18 62H17Z"

function Get-LogoPng([int]$size, [int]$inset = 0) {
    $scale = ($size - 2 * $inset) / 64
    $brush = [Windows.Media.LinearGradientBrush]::new(
        [Windows.Media.ColorConverter]::ConvertFromString($gradientStart),
        [Windows.Media.ColorConverter]::ConvertFromString($gradientEnd),
        [Windows.Point]::new(0, 0),
        [Windows.Point]::new(1, 1))

    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.PushTransform([Windows.Media.MatrixTransform]::new($scale, 0, 0, $scale, $inset, $inset))
    $context.DrawGeometry($brush, $null, [Windows.Media.Geometry]::Parse($mark))
    $context.Pop()
    $context.Close()

    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(
        $size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    $encoder.Save($stream)
    return , $stream.ToArray()
}

function Write-Ico([string]$path, [int[]]$sizes) {
    $frames = foreach ($size in $sizes) { , (Get-LogoPng $size) }

    $stream = [IO.File]::Create($path)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Length)

        $offset = 6 + 16 * $sizes.Length
        for ($i = 0; $i -lt $sizes.Length; $i++) {
            # The ICO format stores 256 as 0.
            $dimension = [byte]($sizes[$i] % 256)
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }

        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    }
    finally {
        $writer.Dispose()
    }
}

New-Item -ItemType Directory -Force $assets | Out-Null

$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <defs>
    <linearGradient id="accent" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="$gradientStart"/>
      <stop offset="1" stop-color="$gradientEnd"/>
    </linearGradient>
  </defs>
  <path d="$mark" fill="url(#accent)" fill-rule="evenodd"/>
</svg>

"@
[IO.File]::WriteAllText((Join-Path $assets "logo.svg"), $svg)

Write-Ico (Join-Path $assets "ParaPhraser.ico") @(256, 64, 48, 40, 32, 24, 20, 16)

[IO.File]::WriteAllBytes((Join-Path $storeAssets "Square44x44Logo.png"), (Get-LogoPng 44))
[IO.File]::WriteAllBytes((Join-Path $storeAssets "StoreLogo.png"), (Get-LogoPng 50))

# Windows draws this one on a large tile, so it needs breathing room.
[IO.File]::WriteAllBytes((Join-Path $storeAssets "Square150x150Logo.png"), (Get-LogoPng 150 25))
