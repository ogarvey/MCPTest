param(
    [string]$Path,
    [int]$Offset,
    [int]$Length,
    [int]$Bpp = 4
)
$ErrorActionPreference='Stop'

$bytes = [System.IO.File]::ReadAllBytes($Path)

if ($Bpp -eq 4) {
    $nibbles = New-Object byte[] ($Length * 2)
    for ($i = 0; $i -lt $Length; $i++) {
        $b = $bytes[$Offset + $i]
        $nibbles[$i*2]   = $b -band 0x0F
        $nibbles[$i*2+1] = ($b -shr 4) -band 0x0F
    }
    $samples = $nibbles
    $palette = 16
} else {
    $samples = New-Object byte[] $Length
    [System.Array]::Copy($bytes, $Offset, $samples, 0, $Length)
    $palette = 256
}

$total = $samples.Length
$widths = 64,128,256,384,512,640,768,1024
foreach ($w in $widths) {
    if ($total % $w -ne 0) { continue }
    $h = $total / $w
    $sameH = 0; $sameV = 0
    for ($y = 0; $y -lt $h; $y++) {
        $row = $y * $w
        for ($x = 1; $x -lt $w; $x++) {
            if ($samples[$row+$x] -eq $samples[$row+$x-1]) { $sameH++ }
        }
    }
    if ($h -gt 1) {
        for ($y = 1; $y -lt $h; $y++) {
            for ($x = 0; $x -lt $w; $x++) {
                if ($samples[$y*$w+$x] -eq $samples[($y-1)*$w+$x]) { $sameV++ }
            }
        }
    }
    $hPct = [math]::Round(100.0 * $sameH / ($h * ($w-1)), 2)
    $vDen = if ($h -gt 1) { ($h-1) * $w } else { 1 }
    $vPct = [math]::Round(100.0 * $sameV / $vDen, 2)
    "{0}bpp width={1,5} h={2,5}   horiz={3,6}%   vert={4,6}%" -f $Bpp,$w,$h,$hPct,$vPct
}
$baseline = [math]::Round(100.0 / $palette, 2)
"random baseline (1/$palette) = $baseline%"
