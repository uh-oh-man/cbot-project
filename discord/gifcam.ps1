# Posts short GIFs of the GAME WINDOW ONLY to the owner's chosen Discord channel (authorized by the owner, uh_oh_man).
# Triggers: Pro wins a level, Pro's first death on each new level, and a status clip every 30 minutes.
param([string]$ChannelId = "1566179139835134013", [int]$StatusMin = 30)
$ErrorActionPreference = "Stop"
$dir = "C:\cbot\discord"
$tok = (New-Object System.Net.NetworkCredential("", (Get-Content "$dir\token.dpapi" | ConvertTo-SecureString))).Password
Add-Type -ReferencedAssemblies System.Drawing, System.Net.Http -TypeDefinition @"
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging; using System.IO; using System.Linq;
using System.Net.Http; using System.Runtime.InteropServices; using System.Threading; using System.Diagnostics;
public static class GifCam {
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  struct RECT { public int L, T, R, B; } struct POINT { public int X, Y; }
  static readonly Queue<Bitmap> ring = new Queue<Bitmap>();
  const int W = 400, H = 300, KEEP = 25;
  public static string Log;
  static void L(string m) { try { File.AppendAllText(Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " gifcam: " + m + Environment.NewLine); } catch { } }
  public static void Capture() {
    var p = Process.GetProcessesByName("LawnProject").FirstOrDefault(); if (p == null) return;
    IntPtr h = p.MainWindowHandle; RECT r; if (!GetClientRect(h, out r)) return;
    var o = new POINT(); ClientToScreen(h, ref o);
    int w = r.R - r.L, hh = r.B - r.T; if (w < 100 || hh < 100) return;
    using (var full = new Bitmap(w, hh)) {
      using (var g = Graphics.FromImage(full)) g.CopyFromScreen(o.X, o.Y, 0, 0, new Size(w, hh));   // the game's client area only
      var small = new Bitmap(W, H);
      using (var g = Graphics.FromImage(small)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear; g.DrawImage(full, 0, 0, W, H); }
      lock (ring) { ring.Enqueue(small); while (ring.Count > KEEP) ring.Dequeue().Dispose(); }
    }
  }
  // Animated GIF from single-frame GIFs: each frame's palette becomes a local color table.
  public static byte[] Encode(int delayCs) {
    List<byte[]> frames; lock (ring) frames = ring.Select(b => { using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Gif); return ms.ToArray(); } }).ToList();
    if (frames.Count == 0) return null;
    var outp = new MemoryStream();
    var f0 = frames[0];
    outp.Write(f0, 0, 6);                       // GIF89a
    outp.Write(f0, 6, 4);                       // width, height
    outp.WriteByte((byte)(f0[10] & 0x70)); outp.WriteByte(0); outp.WriteByte(0);   // no global table
    var ns = new byte[] { 0x21, 0xFF, 0x0B, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0', 0x03, 0x01, 0x00, 0x00, 0x00 };
    outp.Write(ns, 0, ns.Length);
    foreach (var f in frames) {
      int flags = f[10]; int gctBits = flags & 7; int gctLen = (flags & 0x80) != 0 ? 3 * (1 << (gctBits + 1)) : 0;
      int pos = 13 + gctLen;
      while (pos < f.Length && f[pos] == 0x21) { pos += 2; while (f[pos] != 0) pos += f[pos] + 1; pos++; }   // skip extensions
      if (pos >= f.Length || f[pos] != 0x2C) continue;
      var gce = new byte[] { 0x21, 0xF9, 0x04, 0x00, (byte)(delayCs & 0xFF), (byte)(delayCs >> 8), 0x00, 0x00 };
      outp.Write(gce, 0, gce.Length);
      outp.Write(f, pos, 9);                                             // image descriptor (minus packed byte)
      outp.WriteByte((byte)(0x80 | gctBits));                            // local color table of this frame's palette
      outp.Write(f, 13, gctLen);
      int dataStart = pos + 10, end = f.Length - 1;                      // LZW data up to the trailer
      outp.Write(f, dataStart, end - dataStart);
    }
    outp.WriteByte(0x3B);
    return outp.ToArray();
  }
  public static void Post(string token, string channel, string caption, byte[] gif) {
    using (var http = new HttpClient()) {
      http.DefaultRequestHeaders.Add("Authorization", "Bot " + token);
      http.DefaultRequestHeaders.Add("User-Agent", "DiscordBot (local-pvz-relay, 1.0)");
      var form = new MultipartFormDataContent();
      string json = "{\"content\":" + Json(caption) + ",\"allowed_mentions\":{\"parse\":[]}}";
      form.Add(new StringContent(json, System.Text.Encoding.UTF8, "application/json"), "payload_json");
      var file = new ByteArrayContent(gif); file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/gif");
      form.Add(file, "files[0]", "pvz.gif");
      var r = http.PostAsync("https://discord.com/api/v10/channels/" + channel + "/messages", form).Result;
      L("posted gif (" + gif.Length / 1024 + " KB): " + (int)r.StatusCode + " " + caption);
    }
  }
  static string Json(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\""; }
  static string LevelName(int id) { return ((id - 1) / 10 + 1) + "-" + ((id - 1) % 10 + 1); }
  public static void Run(string token, string channel, string recordPath, string stopFlag, int statusMin) {
    long seen = File.Exists(recordPath) ? new FileInfo(recordPath).Length : 0;
    var postedDeathFor = new HashSet<int>(); DateTime lastStatus = DateTime.Now; DateTime lastPost = DateTime.MinValue;
    // Levels that already had a death before this process started: their "first try" clip was already posted.
    try { foreach (var l in File.ReadAllLines(recordPath)) { var mm = System.Text.RegularExpressions.Regex.Match(l, "PRO lvl (\\d+) DIED"); if (mm.Success) postedDeathFor.Add(int.Parse(mm.Groups[1].Value)); } } catch { }
    L("running (channel " + channel + ")");
    while (!File.Exists(stopFlag)) {
      try { Capture(); } catch (Exception e) { L("capture: " + e.Message); }
      try {
        long len = File.Exists(recordPath) ? new FileInfo(recordPath).Length : 0;
        if (len > seen) {
          string added; using (var fs = new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { fs.Seek(seen, SeekOrigin.Begin); added = new StreamReader(fs).ReadToEnd(); }
          seen = len;
          foreach (var line in added.Split('\n').Where(x => x.Contains("PRO lvl"))) {
            var m = System.Text.RegularExpressions.Regex.Match(line, "PRO lvl (\\d+) (WON|DIED)(?: wave (\\d+)/(\\d+))?");
            if (!m.Success) continue; int lvl = int.Parse(m.Groups[1].Value);
            string cap = null;
            if (m.Groups[2].Value == "WON") cap = "Pro beat **" + LevelName(lvl) + "**!";
            else if (postedDeathFor.Add(lvl)) cap = "Pro's first try on **" + LevelName(lvl) + "** ended at wave " + m.Groups[3].Value + "/" + m.Groups[4].Value + ".";
            if (cap != null && (DateTime.Now - lastPost).TotalSeconds > 20) { var gif = Encode(10); if (gif != null) { Post(token, channel, cap, gif); lastPost = DateTime.Now; lastStatus = DateTime.Now; } }
          }
        }
        if ((DateTime.Now - lastStatus).TotalMinutes >= statusMin) { var gif = Encode(10); if (gif != null) { Post(token, channel, "Status clip - the last few seconds of play.", gif); lastStatus = DateTime.Now; lastPost = DateTime.Now; } }
      } catch (Exception e) { L("loop: " + e.GetBaseException().Message); }
      Thread.Sleep(400);
    }
    L("stopped");
  }
}
"@
[GifCam]::Log = "$dir\relay.log"
if ($args -contains "-TestNow") { 1..25 | % { [GifCam]::Capture(); Start-Sleep -Milliseconds 400 }; $g = [GifCam]::Encode(10); [IO.File]::WriteAllBytes("$dir\test.gif", $g); "test gif: $($g.Length) bytes"; exit }
if ($args -contains "-PostNow") { 1..25 | % { [GifCam]::Capture(); Start-Sleep -Milliseconds 400 }; [GifCam]::Post($tok, $ChannelId, "GIF updates are live: you'll get a clip when Pro beats a level, its first try on each new level, and a status clip every 30 min.", [GifCam]::Encode(10)); exit }
[GifCam]::Run($tok, $ChannelId, "C:\cbot\pro_record.txt", "$dir\stop.flag", $StatusMin)
