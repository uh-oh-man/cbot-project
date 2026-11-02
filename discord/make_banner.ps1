# Draws the Discord profile banner (680x240): a striped PvZ lawn with a row of little seedlings and the bot's tagline.
Add-Type -AssemblyName System.Drawing
$W = 680; $H = 240; $bmp = New-Object Drawing.Bitmap $W, $H; $g = [Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
function C($r, $gg, $b) { [Drawing.Color]::FromArgb(255, $r, $gg, $b) }
function B($c) { New-Object Drawing.SolidBrush $c }
for ($i = 0; $i -lt 6; $i++) { $g.FillRectangle((B $(if ($i % 2) { C 66 124 45 } else { C 76 140 52 })), 0, $i * 40, $W, 40) }
$g.FillRectangle((B ([Drawing.Color]::FromArgb(150, 18, 26, 20))), 0, 70, $W, 100)   # dark band for the text
# seedlings along the bottom
for ($x = 30; $x -lt $W; $x += 70) {
  $g.DrawLine((New-Object Drawing.Pen (C 50 95 35), 5), $x, 232, $x, 205)
  $g.FillEllipse((B (C 120 200 70)), $x - 20, 195, 20, 12); $g.FillEllipse((B (C 140 215 85)), $x, 192, 20, 12)
}
$f1 = New-Object Drawing.Font 'Segoe UI', 34, ([Drawing.FontStyle]::Bold); $f2 = New-Object Drawing.Font 'Segoe UI', 15
$sf = New-Object Drawing.StringFormat; $sf.Alignment = 'Center'
$g.DrawString('Sproutbot', $f1, (B (C 196 255 120)), (New-Object Drawing.RectangleF 0, 74, $W, 50), $sf)
$g.DrawString('plays Plants vs. Zombies Extended so you don''t have to', $f2, (B (C 230 240 225)), (New-Object Drawing.RectangleF 0, 128, $W, 30), $sf)
$out = 'C:\cbot\discord\banner.png'; $bmp.Save($out, [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose(); $out
