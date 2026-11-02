# Draws the Discord bot's avatar: a robot head with a seedling antenna and zombie-green eyes (512x512 PNG).
Add-Type -AssemblyName System.Drawing
$S = 512; $bmp = New-Object Drawing.Bitmap $S, $S; $g = [Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::FromArgb(255, 24, 33, 28))
function C($r, $gg, $b) { [Drawing.Color]::FromArgb(255, $r, $gg, $b) }
function B($c) { New-Object Drawing.SolidBrush $c }
function P($c, $w) { $p = New-Object Drawing.Pen $c, $w; $p.LineJoin = 'Round'; $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p }
function RR($x, $y, $w, $h, $r) { $gp = New-Object Drawing.Drawing2D.GraphicsPath; $d = $r * 2
  $gp.AddArc($x, $y, $d, $d, 180, 90); $gp.AddArc($x + $w - $d, $y, $d, $d, 270, 90); $gp.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $gp.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $gp.CloseFigure(); $gp }

# lawn-green backdrop disc with darker stripes, like the PvZ lawn
$g.FillEllipse((B (C 76 140 52)), 16, 16, 480, 480)
$clip = New-Object Drawing.Drawing2D.GraphicsPath; $clip.AddEllipse(16, 16, 480, 480); $g.SetClip($clip)
for ($i = 0; $i -lt 6; $i++) { if ($i % 2) { $g.FillRectangle((B (C 66 124 45)), 16, 16 + $i * 80, 480, 80) } }
$g.ResetClip()

# seedling antenna
$g.DrawLine((P (C 60 110 40) 14), 256, 150, 256, 92)
$leaf = New-Object Drawing.Drawing2D.GraphicsPath; $leaf.AddBezier(256, 100, 210, 60, 180, 90, 196, 112); $leaf.AddBezier(196, 112, 215, 125, 245, 118, 256, 100); $g.FillPath((B (C 120 200 70)), $leaf)
$leaf2 = New-Object Drawing.Drawing2D.GraphicsPath; $leaf2.AddBezier(256, 92, 300, 46, 336, 72, 322, 98); $leaf2.AddBezier(322, 98, 305, 112, 272, 108, 256, 92); $g.FillPath((B (C 140 215 85)), $leaf2)

# robot head
$head = RR 116 150 280 250 56
$g.FillPath((B (C 168 178 186)), $head); $g.DrawPath((P (C 70 78 86) 10), $head)
$g.FillPath((B (C 196 205 212)), (RR 136 166 240 40 20))   # top shine
# ear bolts
foreach ($x in 96, 396) { $g.FillEllipse((B (C 120 128 136)), $x, 250, 22, 50); $g.DrawEllipse((P (C 70 78 86) 6), $x, 250, 22, 50) }

# visor + zombie eyes
$g.FillPath((B (C 30 38 34)), (RR 150 214 212 92 40))
foreach ($ex in 196, 316) {
  $g.FillEllipse((B (C 40 70 40)), $ex - 38, 222, 76, 76)
  $g.FillEllipse((B (C 168 255 96)), $ex - 30, 230, 60, 60)    # zombie-green glow
  $g.FillEllipse((B (C 230 255 200)), $ex - 26, 236, 22, 18)   # glint
  $g.FillEllipse((B (C 22 30 22)), $ex - 6, 252, 18, 22)       # pupil (looking slightly right)
}

# stitched zombie grin
$g.DrawLine((P (C 70 78 86) 8), 196, 352, 316, 352)
for ($x = 208; $x -le 304; $x += 24) { $g.DrawLine((P (C 70 78 86) 6), $x, 340, $x, 364) }

$out = 'C:\cbot\discord\pfp.png'; $bmp.Save($out, [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose(); $out
