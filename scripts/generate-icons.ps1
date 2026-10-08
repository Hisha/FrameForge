#!/usr/bin/env pwsh
#Requires -Version 5.1

param(
    [string]$Source,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $Root 'icon.png' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $Root 'assets\branding' }

Add-Type -AssemblyName System.Drawing

$Sizes = @(16, 24, 32, 48, 64, 128, 256)
$PngSize = 512
$PngPath = Join-Path $OutputDirectory 'frameforge-icon.png'
$IcoPath = Join-Path $OutputDirectory 'frameforge-icon.ico'

if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    throw "Authoritative icon source is missing: $Source"
}

function New-SquareIconBitmap {
    param(
        [System.Drawing.Image]$Image,
        [int]$Size
    )

    $Bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $Graphics = [System.Drawing.Graphics]::FromImage($Bitmap)
    $Attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    try {
        $Graphics.Clear([System.Drawing.Color]::Transparent)
        $Graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $Graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $Graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $Graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $Attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)

        # Fit the complete, non-square source into a transparent square without cropping,
        # distortion, or upscaling. The source's longest edge determines the scale.
        $Scale = [Math]::Min(1.0, $Size / [double][Math]::Max($Image.Width, $Image.Height))
        $Width = [Math]::Max(1, [int][Math]::Round($Image.Width * $Scale))
        $Height = [Math]::Max(1, [int][Math]::Round($Image.Height * $Scale))
        $X = [int][Math]::Floor(($Size - $Width) / 2.0)
        $Y = [int][Math]::Floor(($Size - $Height) / 2.0)
        $Destination = [System.Drawing.Rectangle]::new($X, $Y, $Width, $Height)
        $SourceRectangle = [System.Drawing.Rectangle]::new(0, 0, $Image.Width, $Image.Height)
        $Graphics.DrawImage(
            $Image,
            $Destination,
            0,
            0,
            $Image.Width,
            $Image.Height,
            [System.Drawing.GraphicsUnit]::Pixel,
            $Attributes)
        return $Bitmap
    }
    finally {
        $Attributes.Dispose()
        $Graphics.Dispose()
    }
}

function Convert-BitmapToPngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $Stream = [System.IO.MemoryStream]::new()
    try {
        $Bitmap.Save($Stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$Stream.ToArray()
    }
    finally {
        $Stream.Dispose()
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$SourceImage = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source))
try {
    if ($SourceImage.RawFormat.Guid -ne [System.Drawing.Imaging.ImageFormat]::Png.Guid) {
        throw "Authoritative icon must be a PNG: $Source"
    }
    if ($SourceImage.Width -lt $PngSize -or $SourceImage.Height -lt $PngSize) {
        throw "Source is $($SourceImage.Width)x$($SourceImage.Height); refusing to upscale it to ${PngSize}x${PngSize}."
    }

    $LinuxBitmap = New-SquareIconBitmap -Image $SourceImage -Size $PngSize
    try {
        $LinuxBitmap.Save($PngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $LinuxBitmap.Dispose()
    }

    $Frames = foreach ($Size in $Sizes) {
        $Bitmap = New-SquareIconBitmap -Image $SourceImage -Size $Size
        try {
            [pscustomobject]@{ Size = $Size; Bytes = Convert-BitmapToPngBytes $Bitmap }
        }
        finally {
            $Bitmap.Dispose()
        }
    }

    $Stream = [System.IO.File]::Open($IcoPath, [System.IO.FileMode]::Create)
    $Writer = [System.IO.BinaryWriter]::new($Stream)
    try {
        $Writer.Write([uint16]0) # reserved
        $Writer.Write([uint16]1) # ICO
        $Writer.Write([uint16]$Frames.Count)
        $Offset = 6 + (16 * $Frames.Count)
        foreach ($Frame in $Frames) {
            $Dimension = if ($Frame.Size -eq 256) { 0 } else { $Frame.Size }
            $Writer.Write([byte]$Dimension)
            $Writer.Write([byte]$Dimension)
            $Writer.Write([byte]0) # palette
            $Writer.Write([byte]0) # reserved
            $Writer.Write([uint16]1) # planes
            $Writer.Write([uint16]32) # bits per pixel
            $Writer.Write([uint32]$Frame.Bytes.Length)
            $Writer.Write([uint32]$Offset)
            $Offset += $Frame.Bytes.Length
        }
        foreach ($Frame in $Frames) { $Writer.Write([byte[]]$Frame.Bytes) }
    }
    finally {
        $Writer.Dispose()
        $Stream.Dispose()
    }

    Write-Host "Generated $PngPath (${PngSize}x${PngSize})"
    Write-Host "Generated $IcoPath ($($Sizes -join ', ') px)"
}
finally {
    $SourceImage.Dispose()
}
