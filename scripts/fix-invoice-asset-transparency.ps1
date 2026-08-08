#Requires -Version 5.1
<#
.SYNOPSIS
    Rebuilds sales-invoice overlay assets (logo / watermark / stamp / signature) with a real alpha channel.

.DESCRIPTION
    The scanned template assets shipped as fully opaque images with a white background, so every overlay
    printed as a visible white/grey rectangle on the letterhead. This script keys the paper-white out of
    each asset and stores true alpha:

        alpha = 255 - min(r, g, b)                      (ink stays ink, paper becomes air)
        rgb   = 255 * (channel - 255 + alpha) / alpha   (un-premultiply against white)

    Compositing the result over white paper reproduces the original pixels exactly, so no artwork is lost
    while the rectangle disappears. Fully transparent margins are then trimmed so `object-fit: contain`
    sizes the real ink instead of an empty canvas.

    The transform is idempotent: already-transparent pixels are skipped and keyed pixels map to themselves.

.EXAMPLE
    .\scripts\fix-invoice-asset-transparency.ps1
#>
[CmdletBinding()]
param(
    [string]$TemplatesRoot = "src/PTGOilSystem.Web/wwwroot/invoice-templates",
    [ValidateRange(200, 255)]
    [int]$WhiteThreshold = 246,
    [string[]]$AssetNames = @("logo.png", "watermark.png", "stamp.png", "signature-seller.png")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class InvoiceAssetAlpha
{
    public static string KeyWhiteToAlpha(string path, int whiteThreshold)
    {
        // Clone (never DrawImage) so GDI+ cannot round-trip the channels through a
        // premultiplied surface; the transform must stay byte-exact and idempotent.
        Bitmap source;
        byte[] raw = File.ReadAllBytes(path);
        using (MemoryStream input = new MemoryStream(raw))
        using (Bitmap loaded = new Bitmap(input))
        {
            source = loaded.Clone(
                new Rectangle(0, 0, loaded.Width, loaded.Height),
                PixelFormat.Format32bppArgb);
        }

        int width = source.Width;
        int height = source.Height;
        Rectangle full = new Rectangle(0, 0, width, height);
        BitmapData data = source.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        byte[] buffer = new byte[stride * height];
        Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
        source.UnlockBits(data);
        source.Dispose();

        int minX = width, minY = height, maxX = -1, maxY = -1;
        long keptPixels = 0;
        long clearedPixels = 0;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + (x * 4);
                byte blue = buffer[i];
                byte green = buffer[i + 1];
                byte red = buffer[i + 2];
                byte alpha = buffer[i + 3];

                if (alpha == 0)
                {
                    continue;
                }

                int minChannel = Math.Min(red, Math.Min(green, blue));
                if (minChannel >= whiteThreshold)
                {
                    buffer[i] = 0;
                    buffer[i + 1] = 0;
                    buffer[i + 2] = 0;
                    buffer[i + 3] = 0;
                    clearedPixels++;
                    continue;
                }

                int newAlpha = 255 - minChannel;
                buffer[i] = Unpremultiply(blue, newAlpha);
                buffer[i + 1] = Unpremultiply(green, newAlpha);
                buffer[i + 2] = Unpremultiply(red, newAlpha);
                buffer[i + 3] = (byte)((newAlpha * alpha) / 255);
                keptPixels++;

                if (x < minX) { minX = x; }
                if (x > maxX) { maxX = x; }
                if (y < minY) { minY = y; }
                if (y > maxY) { maxY = y; }
            }
        }

        bool hasInk = maxX >= minX && maxY >= minY;
        if (!hasInk)
        {
            minX = 0;
            minY = 0;
            maxX = width - 1;
            maxY = height - 1;
        }

        Rectangle crop = new Rectangle(minX, minY, (maxX - minX) + 1, (maxY - minY) + 1);
        string trimText = crop.Width == width && crop.Height == height
            ? "canvas kept"
            : string.Format("trimmed {0}x{1} -> {2}x{3}", width, height, crop.Width, crop.Height);

        using (Bitmap output = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb))
        {
            BitmapData target = output.LockBits(
                new Rectangle(0, 0, crop.Width, crop.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = crop.Width * 4;
                for (int y = 0; y < crop.Height; y++)
                {
                    int sourceOffset = ((crop.Y + y) * stride) + (crop.X * 4);
                    IntPtr targetRow = IntPtr.Add(target.Scan0, y * target.Stride);
                    Marshal.Copy(buffer, sourceOffset, targetRow, rowBytes);
                }
            }
            finally
            {
                output.UnlockBits(target);
            }

            using (MemoryStream result = new MemoryStream())
            {
                output.Save(result, ImageFormat.Png);
                File.WriteAllBytes(path, result.ToArray());
            }
        }

        return string.Format(
            "{0,-22} ink={1,-9} paper={2,-9} {3}",
            Path.GetFileName(path),
            keptPixels,
            clearedPixels,
            trimText);
    }

    private static byte Unpremultiply(byte channel, int alpha)
    {
        if (alpha <= 0)
        {
            return 0;
        }

        int value = (255 * (channel - 255 + alpha)) / alpha;
        if (value < 0) { return 0; }
        if (value > 255) { return 255; }
        return (byte)value;
    }
}
"@

$root = Resolve-Path -LiteralPath $TemplatesRoot
$templateFolders = Get-ChildItem -LiteralPath $root -Directory | Sort-Object Name
if (-not $templateFolders) {
    throw "No invoice template folders found under '$root'."
}

foreach ($folder in $templateFolders) {
    Write-Host "[$($folder.Name)]" -ForegroundColor Cyan
    foreach ($assetName in $AssetNames) {
        $assetPath = Join-Path $folder.FullName $assetName
        if (-not (Test-Path -LiteralPath $assetPath)) {
            Write-Host ("  {0,-22} missing - skipped" -f $assetName) -ForegroundColor DarkYellow
            continue
        }

        $report = [InvoiceAssetAlpha]::KeyWhiteToAlpha($assetPath, $WhiteThreshold)
        Write-Host "  $report"
    }
}

Write-Host "Done. Invoice overlay assets now carry real alpha." -ForegroundColor Green
