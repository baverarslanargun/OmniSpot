param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$bytes = [System.IO.File]::ReadAllBytes($resolvedPath)
$signature = [byte[]](
    0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
    0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
    0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
    0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
)

$signatureOffset = -1
for ($index = 8; $index -le $bytes.Length - $signature.Length; $index++) {
    if ($bytes[$index] -ne $signature[0]) {
        continue
    }

    $matches = $true
    for ($signatureIndex = 1; $signatureIndex -lt $signature.Length; $signatureIndex++) {
        if ($bytes[$index + $signatureIndex] -ne $signature[$signatureIndex]) {
            $matches = $false
            break
        }
    }

    if ($matches) {
        $signatureOffset = $index
        break
    }
}

if ($signatureOffset -lt 8) {
    throw "Single-file bundle imzası bulunamadı: $resolvedPath"
}

$headerOffset = [System.BitConverter]::ToInt64($bytes, $signatureOffset - 8)
if ($headerOffset -lt 0 -or $headerOffset -ge $bytes.LongLength) {
    throw "Single-file bundle header konumu geçersiz: $headerOffset"
}

$stream = [System.IO.File]::OpenRead($resolvedPath)
$reader = $null
try {
    $reader = [System.IO.BinaryReader]::new($stream, [System.Text.Encoding]::UTF8, $true)
    $stream.Position = $headerOffset

    $majorVersion = $reader.ReadUInt32()
    $minorVersion = $reader.ReadUInt32()
    $entryCount = $reader.ReadInt32()
    $bundleId = $reader.ReadString()

    if ($majorVersion -lt 6) {
        throw "Bundle sürümü sıkıştırma alanını doğrulamak için çok eski: $majorVersion.$minorVersion"
    }

    $null = $reader.ReadInt64()
    $null = $reader.ReadInt64()
    $null = $reader.ReadInt64()
    $null = $reader.ReadInt64()
    $null = $reader.ReadUInt64()

    $compressedEntries = [System.Collections.Generic.List[string]]::new()
    for ($entryIndex = 0; $entryIndex -lt $entryCount; $entryIndex++) {
        $null = $reader.ReadInt64()
        $null = $reader.ReadInt64()
        $compressedSize = $reader.ReadInt64()
        $null = $reader.ReadByte()
        $relativePath = $reader.ReadString()

        if ($compressedSize -gt 0) {
            $compressedEntries.Add($relativePath)
        }
    }
}
finally {
    if ($null -ne $reader) {
        $reader.Dispose()
    }
    $stream.Dispose()
}

if ($compressedEntries.Count -ne 0) {
    throw "Bundle içinde sıkıştırılmış dosya bulundu: $($compressedEntries -join ', ')"
}

[pscustomobject]@{
    Path = $resolvedPath
    BundleVersion = "$majorVersion.$minorVersion"
    BundleId = $bundleId
    EntryCount = $entryCount
    CompressedEntryCount = $compressedEntries.Count
}
