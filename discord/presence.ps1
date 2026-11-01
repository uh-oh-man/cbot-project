# Keeps the Discord bot shown as "online" (a gateway connection with heartbeats). Receives no message events (intents 0).
$ErrorActionPreference = "Stop"
$dir = "C:\cbot\discord"
$tok = (New-Object System.Net.NetworkCredential("", (Get-Content "$dir\token.dpapi" | ConvertTo-SecureString))).Password
Add-Type -TypeDefinition @"
using System; using System.Net.WebSockets; using System.Text; using System.Threading; using System.Threading.Tasks; using System.Text.RegularExpressions;
public static class Presence {
  static string Recv(ClientWebSocket ws) {
    var buf = new byte[65536]; var sb = new StringBuilder();
    while (true) {
      var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
      if (r.MessageType == WebSocketMessageType.Close) return null;
      sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
      if (r.EndOfMessage) return sb.ToString();
    }
  }
  static void Send(ClientWebSocket ws, string s) { ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(s)), WebSocketMessageType.Text, true, CancellationToken.None).Wait(); }
  public static void Run(string token, string logPath, string stopFlag) {
    while (!System.IO.File.Exists(stopFlag)) {
      try {
        var ws = new ClientWebSocket();
        ws.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), CancellationToken.None).Wait();
        string hello = Recv(ws);
        int interval = int.Parse(Regex.Match(hello, "\"heartbeat_interval\":(\\d+)").Groups[1].Value);
        Send(ws, "{\"op\":2,\"d\":{\"token\":\"" + token + "\",\"intents\":0,\"properties\":{\"os\":\"windows\",\"browser\":\"pvz-relay\",\"device\":\"pvz-relay\"},\"presence\":{\"status\":\"online\",\"since\":null,\"afk\":false,\"activities\":[{\"name\":\"Plants vs. Zombies\",\"type\":0}]}}}");
        System.IO.File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " presence: connected, showing online" + Environment.NewLine);
        string seq = "null";
        var reader = Task.Run(() => { while (ws.State == WebSocketState.Open) { var m = Recv(ws); if (m == null) break; var sm = Regex.Match(m, "\"s\":(\\d+)"); if (sm.Success) seq = sm.Groups[1].Value; } });
        while (ws.State == WebSocketState.Open && !System.IO.File.Exists(stopFlag)) {
          Thread.Sleep(interval);
          Send(ws, "{\"op\":1,\"d\":" + seq + "}");
        }
      } catch (Exception e) { System.IO.File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " presence: " + e.GetBaseException().Message + " - reconnecting" + Environment.NewLine); }
      Thread.Sleep(5000);
    }
  }
}
"@
[Presence]::Run($tok, "$dir\relay.log", "$dir\stop.flag")
