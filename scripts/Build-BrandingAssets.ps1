[CmdletBinding()]
param(
    [string]$BrowserPath,
    [switch]$KeepTemp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$brandingDir = Join-Path $repoRoot "assets\branding"
$resourcesDir = Join-Path $repoRoot "SmartFileLauncher.UI\Resources"

$iconSizes = @(16, 24, 32, 48, 64, 128, 256)
$emblemPngSize = 512

function Resolve-Browser {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) {
            throw "Belirtilen tarayici bulunamadi: $Explicit"
        }
        return $Explicit
    }

    $candidates = @(
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    throw "Chrome veya Edge bulunamadi. -BrowserPath ile yol verin."
}

function New-SvgPage {
    param(
        [string]$SvgMarkup,
        [int]$Width,
        [int]$Height,
        [string]$Destination
    )

    $body = $SvgMarkup -replace '<\?xml[^>]*\?>', ''
    $style = "html,body{margin:0;padding:0;background:transparent;overflow:hidden}svg{display:block;width:${Width}px;height:${Height}px}"
    $html = "<!doctype html><meta charset=`"utf-8`"><style>$style</style>$body"
    [System.IO.File]::WriteAllText($Destination, $html, (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-BrowserShot {
    param(
        [string]$Browser,
        [string]$PagePath,
        [string]$OutputPath,
        [int]$Width,
        [int]$Height,
        [string]$ProfileDir
    )

    $arguments = @(
        "--headless=new",
        "--disable-gpu",
        "--hide-scrollbars",
        "--force-device-scale-factor=1",
        "--default-background-color=00000000",
        "--virtual-time-budget=4000",
        "--user-data-dir=$ProfileDir",
        "--window-size=$Width,$Height",
        "--screenshot=$OutputPath",
        ("file:///" + ($PagePath -replace '\\', '/'))
    )

    $process = Start-Process -FilePath $Browser -ArgumentList $arguments -NoNewWindow -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Tarayici $($process.ExitCode) koduyla dondu: $OutputPath"
    }
    if (-not (Test-Path -LiteralPath $OutputPath)) {
        throw "Ekran goruntusu uretilemedi: $OutputPath"
    }
}

function Get-RenderedBitmap {
    param(
        [string]$Browser,
        [string]$SvgMarkup,
        [int]$Size,
        [string]$WorkDir,
        [string]$ProfileDir,
        [string]$Tag
    )

    $superSize = [Math]::Min($Size * 4, 2048)
    $pagePath = Join-Path $WorkDir "$Tag-$Size.html"
    $shotPath = Join-Path $WorkDir "$Tag-$Size.png"

    New-SvgPage -SvgMarkup $SvgMarkup -Width $superSize -Height $superSize -Destination $pagePath
    Invoke-BrowserShot -Browser $Browser -PagePath $pagePath -OutputPath $shotPath -Width $superSize -Height $superSize -ProfileDir $ProfileDir

    $source = [System.Drawing.Bitmap]::FromFile($shotPath)
    try {
        if ($source.Width -eq $Size -and $source.Height -eq $Size) {
            return New-Object System.Drawing.Bitmap($source)
        }

        $target = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($target)
        try {
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($source, (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)))
        }
        finally {
            $graphics.Dispose()
        }
        return $target
    }
    finally {
        $source.Dispose()
    }
}

function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Get-DibBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $width = $Bitmap.Width
    $height = $Bitmap.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $width, $height)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $pixels = New-Object byte[] ($data.Stride * $height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)

        $maskStride = [Math]::Floor(($width + 31) / 32) * 4
        $stream = New-Object System.IO.MemoryStream
        $writer = New-Object System.IO.BinaryWriter($stream)
        try {
            $writer.Write([uint32]40)
            $writer.Write([int32]$width)
            $writer.Write([int32]($height * 2))
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]0)
            $writer.Write([uint32]($width * $height * 4 + $maskStride * $height))
            $writer.Write([int32]0)
            $writer.Write([int32]0)
            $writer.Write([uint32]0)
            $writer.Write([uint32]0)

            for ($y = $height - 1; $y -ge 0; $y--) {
                $writer.Write($pixels, $y * $data.Stride, $width * 4)
            }

            $maskRow = New-Object byte[] $maskStride
            for ($y = $height - 1; $y -ge 0; $y--) {
                [Array]::Clear($maskRow, 0, $maskRow.Length)
                for ($x = 0; $x -lt $width; $x++) {
                    $alpha = $pixels[$y * $data.Stride + $x * 4 + 3]
                    if ($alpha -eq 0) {
                        $maskRow[[Math]::Floor($x / 8)] = $maskRow[[Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8))
                    }
                }
                $writer.Write($maskRow, 0, $maskStride)
            }

            $writer.Flush()
            return ,$stream.ToArray()
        }
        finally {
            $writer.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $Bitmap.UnlockBits($data)
    }
}

function Save-Icon {
    param(
        [object[]]$Frames,
        [string]$Destination
    )

    $ordered = @($Frames | Sort-Object -Property Size)
    $stream = [System.IO.File]::Create($Destination)
    $writer = New-Object System.IO.BinaryWriter($stream)

    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$ordered.Count)

        $offset = 6 + 16 * $ordered.Count
        foreach ($frame in $ordered) {
            $payload = $frame.Payload
            $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$payload.Length)
            $writer.Write([uint32]$offset)
            $offset += $payload.Length
        }

        foreach ($frame in $ordered) {
            $payload = [byte[]]$frame.Payload
            $writer.Write($payload, 0, $payload.Length)
        }

        $writer.Flush()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Build-Icon {
    param(
        [string]$Browser,
        [string]$SvgPath,
        [string]$Destination,
        [string]$WorkDir,
        [string]$ProfileDir
    )

    if (-not (Test-Path -LiteralPath $SvgPath)) {
        throw "Kaynak SVG bulunamadi: $SvgPath"
    }

    $markup = [System.IO.File]::ReadAllText($SvgPath)
    $tag = [System.IO.Path]::GetFileNameWithoutExtension($SvgPath)
    $frames = New-Object System.Collections.Generic.List[object]

    foreach ($size in $iconSizes) {
        $bitmap = Get-RenderedBitmap -Browser $Browser -SvgMarkup $markup -Size $size -WorkDir $WorkDir -ProfileDir $ProfileDir -Tag $tag
        try {
            $payload = if ($size -ge 256) { Get-PngBytes -Bitmap $bitmap } else { Get-DibBytes -Bitmap $bitmap }
            $frames.Add([pscustomobject]@{ Size = $size; Payload = $payload })
        }
        finally {
            $bitmap.Dispose()
        }
        Write-Host "  $size px"
    }

    Save-Icon -Frames $frames.ToArray() -Destination $Destination
    Write-Host "Yazildi: $Destination"
}

function Build-EmblemPng {
    param(
        [string]$Browser,
        [string]$SvgPath,
        [string]$Destination,
        [string]$WorkDir,
        [string]$ProfileDir
    )

    if (-not (Test-Path -LiteralPath $SvgPath)) {
        throw "Kaynak SVG bulunamadi: $SvgPath"
    }

    $markup = [System.IO.File]::ReadAllText($SvgPath)
    $stripped = [System.Text.RegularExpressions.Regex]::Replace(
        $markup,
        '<g id="background".*?</g>',
        '',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)

    if ($stripped -eq $markup) {
        throw "Arka plan katmani bulunamadi: $SvgPath"
    }

    $bitmap = Get-RenderedBitmap -Browser $Browser -SvgMarkup $stripped -Size $emblemPngSize -WorkDir $WorkDir -ProfileDir $ProfileDir -Tag "amblem"
    try {
        $bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    Write-Host "Yazildi: $Destination"
}

$browser = Resolve-Browser -Explicit $BrowserPath
Write-Host "Tarayici: $browser"

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("omnispot-branding-" + [Guid]::NewGuid().ToString("N"))
$profileDir = Join-Path $workDir "profile"
New-Item -ItemType Directory -Path $workDir -Force | Out-Null
New-Item -ItemType Directory -Path $profileDir -Force | Out-Null

try {
    Write-Host "app.ico (koyu kobalt)"
    Build-Icon -Browser $browser `
        -SvgPath (Join-Path $brandingDir "omnispot-app-icon-kobalt.svg") `
        -Destination (Join-Path $resourcesDir "app.ico") `
        -WorkDir $workDir -ProfileDir $profileDir

    Write-Host "omnispot-app-icon-kobalt-beyaz.ico"
    Build-Icon -Browser $browser `
        -SvgPath (Join-Path $brandingDir "omnispot-app-icon-kobalt-beyaz.svg") `
        -Destination (Join-Path $brandingDir "omnispot-app-icon-kobalt-beyaz.ico") `
        -WorkDir $workDir -ProfileDir $profileDir

    Write-Host "omnispot-amblem.png"
    Build-EmblemPng -Browser $browser `
        -SvgPath (Join-Path $brandingDir "omnispot-kobalt.svg") `
        -Destination (Join-Path $resourcesDir "omnispot-amblem.png") `
        -WorkDir $workDir -ProfileDir $profileDir
}
finally {
    if ($KeepTemp) {
        Write-Host "Gecici dizin: $workDir"
    }
    else {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
