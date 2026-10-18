# Builds res/app.ico: the app mark drawn in code and packed as a multi-size ICO.
# Same composition as Draw.AppIcon -- a glass plate, the rope, the clip and one
# photo -- so the shell icon and the tray icon come from one design.
# Pure System.Drawing, no external tools. Run from the repo root:
#   pwsh -File res/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$ico  = Join-Path $PSScriptRoot 'app.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-RoundedRect([System.Drawing.RectangleF]$r, [float]$radius) {
  $d = [Math]::Min($radius * 2, [Math]::Min($r.Width, $r.Height))
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
  $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
  $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
  $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}

function New-IconPng([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)
  $u = $size / 256.0

  # glass plate
  $plate = New-RoundedRect (New-Object System.Drawing.RectangleF (8*$u), (8*$u), (240*$u), (240*$u)) (52*$u)
  $plateBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF 0, 0),
    (New-Object System.Drawing.PointF $size, $size),
    [System.Drawing.Color]::FromArgb(255, 252, 252, 253),
    [System.Drawing.Color]::FromArgb(255, 208, 219, 233))
  $g.FillPath($plateBrush, $plate)
  $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(38, 0, 0, 0)), ([float][Math]::Max(1, 2*$u))
  $g.DrawPath($edge, $plate)

  # the rope: a quadratic sag, same shape the strip draws
  $rope = New-Object System.Drawing.Drawing2D.GraphicsPath
  $rope.AddBezier(36*$u, 92*$u, 92*$u, 128*$u, 164*$u, 128*$u, 220*$u, 92*$u)
  $ropePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 126, 130, 138)), ([float][Math]::Max(1, 9*$u))
  $ropePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $ropePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  $g.DrawPath($ropePen, $rope)

  # the clip
  $clipPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 150, 154, 160)), ([float][Math]::Max(1, 15*$u))
  $clipPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $clipPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  $g.DrawLine($clipPen, 128*$u, 104*$u, 128*$u, 150*$u)

  # one photo hanging on it
  $card = New-RoundedRect (New-Object System.Drawing.RectangleF (74*$u), (148*$u), (108*$u), (82*$u)) (16*$u)
  $cardBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF (74*$u), (148*$u)),
    (New-Object System.Drawing.PointF (182*$u), (230*$u)),
    [System.Drawing.Color]::FromArgb(255, 92, 138, 214),
    [System.Drawing.Color]::FromArgb(255, 204, 106, 152))
  $g.FillPath($cardBrush, $card)
  $cardEdge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 0, 0, 0)), ([float][Math]::Max(1, 2*$u))
  $g.DrawPath($cardEdge, $card)

  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  # The comma stops PowerShell from unrolling the byte[] into loose bytes.
  return ,$ms.ToArray()
}

# --- pack into an ICO container (PNG-compressed entries, Vista and later) ---
$blobs = New-Object System.Collections.ArrayList
foreach ($s in $sizes) { [void]$blobs.Add([byte[]](New-IconPng $s)) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]
  $dim = if ($s -ge 256) { 0 } else { $s }
  $w.Write([byte]$dim); $w.Write([byte]$dim)
  $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$blobs[$i].Length)
  $w.Write([uint32]$offset)
  $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $w.Write($b) }
$w.Flush()
[System.IO.File]::WriteAllBytes($ico, $out.ToArray())
$w.Dispose(); $out.Dispose()

Write-Host ("[icon] {0}  ({1} bytes, {2} sizes)" -f $ico, (Get-Item $ico).Length, $sizes.Count)
