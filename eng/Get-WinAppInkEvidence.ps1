#requires -Version 5.1
<#
.SYNOPSIS
Reads saved ink for the winapp UI regression without launching FluentCanvas.
.EXAMPLE
powershell.exe -NoProfile -STA -File .\eng\Get-WinAppInkEvidence.ps1 -Path .\lesson.icart
.NOTES
The ink hash covers the uncompressed ink entry or standalone ink stream. The geometry
hash covers ordered points (including pressure) and the drawing attributes listed
below; it does not cover embedded images, media, or custom stroke properties.
Each stroke also has a geometry hash independent of its index in the collection,
so an erase operation can identify which original stroke remains.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName PresentationCore,PresentationFramework,System.IO.Compression
Add-Type -Path (Join-Path $PSScriptRoot 'NativeInkEvidence.cs') -ReferencedAssemblies PresentationCore,WindowsBase,System.Core

function Get-Sha256([byte[]]$Bytes, [int]$Offset = 0, [int]$Count = -1) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    if ($Count -lt 0) { $Count = $Bytes.Length - $Offset }
    try { return ([BitConverter]::ToString($hasher.ComputeHash($Bytes, $Offset, $Count))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
}

function Get-Bounds($Bounds) {
    if ($Bounds.IsEmpty) { return $null }
    return [ordered]@{ x = $Bounds.X; y = $Bounds.Y; width = $Bounds.Width; height = $Bounds.Height }
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).ProviderPath
$extension = [IO.Path]::GetExtension($resolvedPath).ToLowerInvariant()
if ($extension -notin @('.icart', '.icstk')) { throw 'Expected an .icart or .icstk document.' }
$maxInkBytes = 32 * 1024 * 1024
$native = $false
$inkStream = [IO.MemoryStream]::new()
try {
    $inputStream = [IO.File]::OpenRead($resolvedPath)
    try {
        if ($extension -eq '.icart') {
            $archive = [IO.Compression.ZipArchive]::new($inputStream, [IO.Compression.ZipArchiveMode]::Read, $true)
            try {
                $entries = @($archive.Entries | Where-Object { $_.FullName -cin @('ink.fcink', 'strokes.icstk') })
                if ($entries.Count -ne 1) { throw 'The archive must contain exactly one ink entry.' }
                $native = $entries[0].FullName -ceq 'ink.fcink'
                if ($native) { $maxInkBytes = [NativeInkEvidence]::MaxFileBytes }
                if ($entries[0].Length -gt $maxInkBytes) { throw 'Ink data exceeds the document limit.' }
                $source = $entries[0].Open()
                try { $source.CopyTo($inkStream) }
                finally { $source.Dispose() }
            }
            finally { $archive.Dispose() }
        }
        else {
            if ($inputStream.Length -gt $maxInkBytes) { throw 'Ink data exceeds the document limit.' }
            $inputStream.CopyTo($inkStream)
        }
    }
    finally { $inputStream.Dispose() }

    $inkHash = Get-Sha256 $inkStream.ToArray()
    $inkStream.Position = 0
    $strokes = if ($native) { ,([NativeInkEvidence]::Read($inkStream)) }
    else { ,([Windows.Ink.StrokeCollection]::new($inkStream)) }
    $strokeEvidence = @()
    $totalPoints = 0
    $geometry = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($geometry)
    try {
        $writer.Write([string]'FluentCanvas.InkEvidence.Geometry.v1')
        $writer.Write([int]$strokes.Count)
        for ($index = 0; $index -lt $strokes.Count; $index++) {
            $strokeStart = $geometry.Position
            $stroke = $strokes[$index]
            $attributes = $stroke.DrawingAttributes
            $min = [double]::PositiveInfinity
            $max = [double]::NegativeInfinity
            $sum = 0.0
            $writer.Write([int]$stroke.StylusPoints.Count)
            foreach ($point in $stroke.StylusPoints) {
                $pressure = [double]$point.PressureFactor
                $min = [Math]::Min($min, $pressure)
                $max = [Math]::Max($max, $pressure)
                $sum += $pressure
                $writer.Write([double]$point.X)
                $writer.Write([double]$point.Y)
                $writer.Write([single]$point.PressureFactor)
            }
            $totalPoints += $stroke.StylusPoints.Count
            $writer.Write([double]$attributes.Width)
            $writer.Write([double]$attributes.Height)
            $writer.Write([string]$attributes.Color.ToString())
            $writer.Write([bool]$attributes.IgnorePressure)
            $writer.Write([bool]$attributes.FitToCurve)
            $writer.Write([bool]$attributes.IsHighlighter)
            $writer.Write([int]$attributes.StylusTip)
            $transform = $attributes.StylusTipTransform
            foreach ($number in @($transform.M11, $transform.M12, $transform.M21, $transform.M22, $transform.OffsetX, $transform.OffsetY)) {
                $writer.Write([double]$number)
            }
            $writer.Flush()
            # Hash the existing canonical slice without adding bytes to the document
            # hash or copying the growing buffer once per stroke.
            $strokeGeometryHash = Get-Sha256 -Bytes $geometry.GetBuffer() -Offset ([int]$strokeStart) -Count ([int]($geometry.Position - $strokeStart))
            $strokeEvidence += [ordered]@{
                index = $index
                geometrySha256 = $strokeGeometryHash
                pointCount = $stroke.StylusPoints.Count
                bounds = Get-Bounds $stroke.GetBounds()
                pressure = [ordered]@{ min = $min; max = $max; mean = $sum / $stroke.StylusPoints.Count }
                drawingAttributes = [ordered]@{
                    width = $attributes.Width; height = $attributes.Height
                    color = $attributes.Color.ToString(); ignorePressure = $attributes.IgnorePressure
                    fitToCurve = $attributes.FitToCurve; isHighlighter = $attributes.IsHighlighter
                    stylusTip = $attributes.StylusTip.ToString()
                    stylusTipTransform = @($transform.M11, $transform.M12, $transform.M21, $transform.M22, $transform.OffsetX, $transform.OffsetY)
                }
            }
        }
        $writer.Flush()
        $geometryHash = Get-Sha256 $geometry.ToArray()
    }
    finally { $writer.Dispose(); $geometry.Dispose() }

    $result = [ordered]@{
        schemaVersion = 1
        path = $resolvedPath
        format = $extension.Substring(1)
        count = $strokes.Count
        pointCount = $totalPoints
        bounds = Get-Bounds $strokes.GetBounds()
        inkBytes = $inkStream.Length
        inkSha256 = $inkHash
        geometrySha256 = $geometryHash
        strokes = @($strokeEvidence)
    }
    $json = $result | ConvertTo-Json -Depth 8
    if ($OutputPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json, [Text.UTF8Encoding]::new($false)) }
    $json
}
finally { $inkStream.Dispose() }
