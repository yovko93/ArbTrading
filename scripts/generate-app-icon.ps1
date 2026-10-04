#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run this script in an STA PowerShell session: powershell.exe -NoProfile -STA -File scripts/generate-app-icon.ps1'
}

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repositoryRoot 'src/Arbitrage.Desktop/Resources/Branding.xaml'
$assetDirectory = Join-Path $repositoryRoot 'src/Arbitrage.Desktop/Assets'
$iconPath = Join-Path $assetDirectory 'ArbitrageTrading.ico'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

$reader = [System.Xml.XmlReader]::Create($sourcePath)
try {
    $resources = [System.Windows.Markup.XamlReader]::Load($reader)
}
finally {
    $reader.Dispose()
}
$mark = $resources['AppMark']
if ($mark -isnot [System.Windows.Media.DrawingImage]) {
    throw 'Branding.xaml must contain the AppMark DrawingImage.'
}
$mark.Freeze()

$frames = foreach ($size in $sizes) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $drawing = $visual.RenderOpen()
    try {
        $drawing.DrawImage($mark, (New-Object System.Windows.Rect 0, 0, $size, $size))
    }
    finally {
        $drawing.Close()
    }

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    try {
        $encoder.Save($stream)
        [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    }
    finally {
        $stream.Dispose()
    }
}

$iconStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $iconStream
try {
    $writer.Write([uint16]0) # Reserved
    $writer.Write([uint16]1) # Windows icon
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0) # True color
        $writer.Write([byte]0) # Reserved
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) {
        $writer.Write([byte[]]$frame.Bytes)
    }
    $writer.Flush()

    [System.IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
    [System.IO.File]::WriteAllBytes($iconPath, $iconStream.ToArray())
}
finally {
    $writer.Dispose()
    $iconStream.Dispose()
}

Write-Output ('Generated {0} with {1} PNG frames: {2} px.' -f $iconPath, $frames.Count, ($sizes -join ', '))
