# Gera Assets\app.ico (multi-tamanho) a partir de um PNG quadrado (ex.: a logo do app).
# Uso: powershell -File scripts\make-icon.ps1 -Source <png> -Out <ico>
# Requisito: Windows (System.Drawing). Script 100% ASCII (PS 5.1 le sem BOM como ANSI).

param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Out
)

Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Image]::FromFile($Source)
try {
    $sizes = @(256, 128, 64, 48, 32, 24, 16)
    $pngs = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.DrawImage($src, 0, 0, $s, $s)
            } finally { $g.Dispose() }

            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngs += , $ms.ToArray()
        } finally { $bmp.Dispose() }
    }

    # Empacota o ICO: cabecalho ICONDIR + entradas ICONDIRENTRY + blobs PNG
    # (entradas PNG sao validas no ICO desde o Vista; 256 = 0 no campo de largura/altura)
    $count = $pngs.Count
    $offset = 6 + (16 * $count)
    $dir = [System.IO.MemoryStream]::new()
    $w = [System.IO.BinaryWriter]::new($dir)
    $w.Write([uint16]0)      # reserved
    $w.Write([uint16]1)      # type = icon
    $w.Write([uint16]$count) # image count
    for ($i = 0; $i -lt $count; $i++) {
        $s = $sizes[$i]
        $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # width
        $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # height
        $w.Write([byte]0)        # palette
        $w.Write([byte]0)        # reserved
        $w.Write([uint16]1)      # color planes
        $w.Write([uint16]32)     # bits per pixel
        $w.Write([uint32]$pngs[$i].Length)  # data size
        $w.Write([uint32]$offset)           # data offset
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $w.Write($p) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($Out, $dir.ToArray())
    Write-Host "OK: $Out ($($sizes.Count) tamanhos, $($dir.Length) bytes)"
} finally {
    $src.Dispose()
}
