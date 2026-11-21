# Renders Kenshi message-banner labels (MessageSkin from Kenshi_UI.png, Exo 2 SemiBold) as transparent PNGs.
# -Style Modern: a dark rounded chip in the bottom-left corner of a transparent 1920x1080 frame, for video overlays.
# Usage: .\make-labels.ps1 [-Labels "Vanilla","Faithful","Meitou"] [-Style Modern [-Center] [-Top]] [-Scale 1.5] [-Width 480] [-Align Left] [-Padding 40] [-HeightScale 1] [-Cap 1] [-Out .\labels]
param(
  [string[]]$Labels = @('Vanilla', 'Faithful', 'Meitou'),
  [ValidateSet('Classic', 'Modern')][string]$Style = 'Classic',
  [switch]$Center,           # Modern: chip centred horizontally instead of at the left
  [switch]$Top,              # Modern: chip at the top edge instead of the bottom
  [double]$Scale = 1.5,
  [int]$Width = 480,
  [string]$Out = (Join-Path $PSScriptRoot 'labels'),
  [string]$Game = 'C:\Games\Kenshi_RE\data\gui',
  [ValidateSet('Center', 'Left')][string]$Align = 'Center',
  [double]$Padding = 40,      # space beside the text, in source pixels (x Scale)
  [double]$HeightScale = 1,  # squash or stretch the banner vertically
  [double]$Cap = 1           # squash the worn end caps horizontally
)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null

$atlas = [System.Drawing.Bitmap]::FromFile("$Game\gfx\Kenshi_UI.png")
$fonts = New-Object System.Drawing.Text.PrivateFontCollection
$fonts.AddFontFile("$Game\fonts\Exo2-SemiBold.ttf")
$family = $fonts.Families[0]

if ($Style -eq 'Modern') {
  # Chip at 24,966, 90 high, corners 14; rims 2px #1A1514 and 2px #231E19 around #100C0A; text #AFA68B, 32.3px, ink 40px in from each side.
  function Rounded([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90); $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90); $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure(); $p
  }
  $size = 32.3; $cx = 24; $cy = 966; $ch = 90; $baseline = 1024
  if ($Top) { $cy = 24; $baseline = 82 }   # the same 24px margin, from the top edge
  $ascent = $size * $family.GetCellAscent(0) / $family.GetEmHeight(0)
  # **text** is set in Exo 2 Bold, on the same baseline.
  $boldFonts = New-Object System.Drawing.Text.PrivateFontCollection
  $boldFonts.AddFontFile("$Game\fonts\Exo2-Bold.ttf")
  $bold = $boldFonts.Families[0]
  $boldStyle = if ($bold.IsStyleAvailable([System.Drawing.FontStyle]::Regular)) { 0 } else { [int][System.Drawing.FontStyle]::Bold }
  $boldShift = $ascent - $size * $bold.GetCellAscent($boldStyle) / $bold.GetEmHeight($boldStyle)
  $measure = [System.Drawing.Graphics]::FromImage($atlas)
  $typographic = [System.Drawing.StringFormat]::GenericTypographic.Clone()
  $typographic.FormatFlags = $typographic.FormatFlags -bor [System.Drawing.StringFormatFlags]::MeasureTrailingSpaces
  foreach ($label in $Labels) {
    $text = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x = 0; $isBold = $false
    foreach ($part in $label -split '\*\*') {
      if ($part) {
        $fam = if ($isBold) { $bold } else { $family }; $st = if ($isBold) { $boldStyle } else { 0 }
        $text.AddString($part, $fam, $st, [single]$size, (New-Object System.Drawing.PointF([single]$x, [single]$(if ($isBold) { $boldShift } else { 0 }))), $typographic)
        $font = New-Object System.Drawing.Font($fam, [single]$size, [System.Drawing.FontStyle]$st, [System.Drawing.GraphicsUnit]::Pixel)
        $x += $measure.MeasureString($part, $font, [System.Drawing.PointF]::Empty, $typographic).Width; $font.Dispose()
      }
      $isBold = -not $isBold
    }
    $ink = $text.GetBounds()
    $cw = [math]::Round($ink.Width) + 80
    if ($Center) { $cx = [math]::Round((1920 - $cw) / 2) }
    $m = New-Object System.Drawing.Drawing2D.Matrix; $m.Translate([single]($cx + 40 - $ink.X), [single]($baseline - $ascent)); $text.Transform($m)
    $bmp = New-Object System.Drawing.Bitmap(1920, 1080, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent); $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    foreach ($layer in @(@(0, 14, '#1A1514'), @(2, 12, '#231E19'), @(4, 10, '#100C0A'))) {
      $i = $layer[0]; $path = Rounded ($cx + $i) ($cy + $i) ($cw - 2 * $i) ($ch - 2 * $i) $layer[1]
      $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($layer[2]))), $path); $path.Dispose()
    }
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#AFA68B'))), $text)
    $g.Dispose(); $text.Dispose()
    $slug = ($label.ToLower() -replace '&', 'and' -replace '[^a-z0-9]+', '-').Trim('-')
    $file = Join-Path $Out "$slug.png"
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "{0}  chip {1}x{2}" -f $file, $cw, $ch
  }
  $atlas.Dispose(); $fonts.Dispose(); return
}

# MessageSkin: source 761,251 170x70; left cap 82, stretched middle 35, right cap 53.
$sx = 761; $sy = 251; $sh = 70; $L = 82; $R = 53; $M = 35
$pad = 6                                   # transparent margin so the worn edges never clip
$H = [int][math]::Round($sh * $Scale * $HeightScale)
$cl = [int][math]::Round($L * $Scale * $Cap); $cr = [int][math]::Round($R * $Scale * $Cap)
$W = [math]::Max($Width, $cl + $cr + 1)
# Widen every banner in the set to fit the longest label, so they all match.
$probe = New-Object System.Drawing.Bitmap(1, 1); $pg = [System.Drawing.Graphics]::FromImage($probe)
$pf = New-Object System.Drawing.Font($family, [single](22 * $Scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
foreach ($label in $Labels) { $W = [math]::Max($W, [int]($pg.MeasureString($label, $pf).Width + 2 * $Padding * $Scale)) }
$pg.Dispose(); $probe.Dispose(); $pf.Dispose()

$attr = New-Object System.Drawing.Imaging.ImageAttributes
$attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)   # no edge bleed when stretching
function Draw-Part($g, $dx, $dw, $srcX, $srcW) {
  $dest = New-Object System.Drawing.Rectangle(($pad + $dx), $pad, $dw, $H)
  $g.DrawImage($atlas, $dest, $srcX, $sy, $srcW, $sh, [System.Drawing.GraphicsUnit]::Pixel, $attr)
}

foreach ($label in $Labels) {
  $bmp = New-Object System.Drawing.Bitmap(($W + 2 * $pad), ($H + 2 * $pad), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

  Draw-Part $g 0 $cl $sx $L
  Draw-Part $g $cl ($W - $cl - $cr) ($sx + $L) $M
  Draw-Part $g ($W - $cr) $cr ($sx + $L + $M) $R

  # Text: the game's Secondary colour on paper, Floater size (22px) scaled with the banner.
  $font = New-Object System.Drawing.Font($family, [single](22 * $Scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
  $fmt = New-Object System.Drawing.StringFormat
  $fmt.Alignment = if ($Align -eq 'Left') { [System.Drawing.StringAlignment]::Near } else { [System.Drawing.StringAlignment]::Center }
  $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
  $brush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#140806'))
  $inset = if ($Align -eq 'Left') { $Padding * $Scale } else { 0 }   # left-aligned text starts inside the worn edge
  $rect = New-Object System.Drawing.RectangleF([single]($pad + $inset), [single]($pad - 1 * $Scale), [single]($W - $inset), [single]$H)
  $g.DrawString($label, $font, $brush, $rect, $fmt)

  $g.Dispose(); $font.Dispose(); $brush.Dispose()
  $slug = ($label.ToLower() -replace '&', 'and' -replace '[^a-z0-9]+', '-').Trim('-')
  $file = Join-Path $Out "$slug.png"
  $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  "{0}  {1}x{2}" -f $file, ($W + 2 * $pad), ($H + 2 * $pad)
}
$atlas.Dispose(); $fonts.Dispose()
