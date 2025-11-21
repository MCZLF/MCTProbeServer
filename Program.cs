// Program.cs  (.NET 8 Console, non-top-level)
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ProbeServer
{
    internal static class Program
    {
        // ================== 可改常量 ==================
        private static readonly int TcpPort = 17600;
        private static readonly bool UseFrp = true;      // 是否启用 PROXY v2
        private static readonly int MaxUploadPerHour = 0; // 0=不限制

        // ================== 路径 ==================
        private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe");
        private static readonly string SelfLog = Path.Combine(LogDir, "self.log");
        private static readonly string LimitDb = Path.Combine(LogDir, "upload_limit.json");

        // ================== 计数器 ==================
        private static readonly Dictionary<string, int> Counter = new();
        private static int _currentHour = -1;

        // 每日统计
        private static readonly object DailyLock = new();
        private static string _todayFile = string.Empty;
        private static readonly Dictionary<string, int> _versionCounter = new(); // version -> count
        private static int _totalToday = 0;
        private static Timer? _dailyTimer;

        private static void Main()
        {
            Directory.CreateDirectory(LogDir);
            LoadCounter();
            Log("Probe 探针服务端程序启动");
            Log($"[Config]UseFrp:{UseFrp}");

            ResetDailyFile();
            ScheduleDailySummary();

            _ = Task.Run(RunTcpAsync);
            Thread.Sleep(Timeout.Infinite);
        }

        #region 通用工具
        private static void Log(string msg)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
            Console.WriteLine(line);
            File.AppendAllText(SelfLog, line + Environment.NewLine);
        }

        private static void LoadCounter()
        {
            if (!File.Exists(LimitDb)) return;
            var json = File.ReadAllText(LimitDb);
            foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new())
                Counter[kv.Key] = kv.Value;
        }

        private static void SaveCounter() =>
            File.WriteAllText(LimitDb, JsonSerializer.Serialize(Counter));

        private static bool CanUpload(string ip)
        {
            if (MaxUploadPerHour <= 0) return true;
            var now = DateTime.UtcNow.Hour;
            if (now != Interlocked.Exchange(ref _currentHour, now))
            {
                Counter.Clear();
                Log("[Limiter] 整点清零");
            }
            if (Counter.TryGetValue(ip, out var count) && count >= MaxUploadPerHour)
                return false;
            Counter[ip] = count + 1;
            SaveCounter();
            return true;
        }

        private static void ResetDailyFile()
        {
            lock (DailyLock)
            {
                _todayFile = Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}-probe.txt");
                _totalToday = 0;
                _versionCounter.Clear();
            }
        }

        private static void ScheduleDailySummary()
        {
            var now = DateTime.Now;
            var due = now.Date.AddDays(1).AddSeconds(-1) - now;
            if (due <= TimeSpan.Zero)                 // 保险
                due = TimeSpan.FromMilliseconds(1);

            _dailyTimer?.Dispose();
            _dailyTimer = new Timer(_ =>
            {
                try
                {
                    WriteDailySummary();
                    ResetDailyFile();
                    ScheduleDailySummary();
                }
                catch (Exception ex)
                {
                    Log($"[DailyTimer] 异常: {ex}");
                }
            }, null, due, Timeout.InfiniteTimeSpan);
        }

        private static void WriteDailySummary()
        {
            lock (DailyLock)
            {
                /* ---- fix 修复计时器刷屏日志文件 ---- */
                if (_totalToday == 0) return;

                /* ---- 2. 拼汇总（只留核心数据）---- */
                var sb = new StringBuilder();
                sb.AppendLine($"日期：{DateTime.Now:yyyy-MM-dd}");
                sb.AppendLine($"总启动次数：{_totalToday}");
                foreach (var kv in _versionCounter.OrderByDescending(v => v.Value))
                    sb.AppendLine($"版本 {kv.Key}：{kv.Value} 次");

                /* ---- 3. 写 summary.log（纯汇总）---- */
                File.AppendAllText(Path.Combine(LogDir, "summary.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{sb}{Environment.NewLine}");

                /* ---- 4. 原逻辑：把汇总+原始内容写回当天文件 ---- */
                if (File.Exists(_todayFile))
                {
                    var raw = File.ReadAllText(_todayFile);
                    var all = new StringBuilder();
                    all.AppendLine("[summery]");
                    all.AppendLine($"今日共启动 {_totalToday} 次");
                    foreach (var kv in _versionCounter.OrderByDescending(v => v.Value))
                        all.AppendLine($"{kv.Key} : {kv.Value}次");
                    all.AppendLine();
                    all.Append(raw);
                    File.WriteAllText(_todayFile, all.ToString());
                }
            }
        }
        #endregion

        #region TCP 服务
        private static async Task RunTcpAsync()
        {
            var listener = new TcpListener(IPAddress.Any, TcpPort);
            listener.Start();
            Log($"[TCP] Listening on 0.0.0.0:{TcpPort}");

            while (true)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    string clientIp;

                    if (UseFrp)
                    {
                        var (success, realIp) = await ProxyProtocolV2Reader.TryGetRealIpAsync(client.GetStream());
                        clientIp = success ? realIp : "unknown";
                        Log($"[TCP] PROXY v2 解析结果 success={success} real-ip=>{clientIp}");
                    }
                    else
                    {
                        clientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                        Log($"[TCP] 连接 {clientIp}");
                    }

                    using var stream = client.GetStream();
                    var buffer = new byte[1024 * 64];
                    var len = await stream.ReadAsync(buffer, 0, buffer.Length);
                    var raw = Encoding.UTF8.GetString(buffer, 0, len);

                    // 解析客户端文本
                    var lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    string? version = null;
                    var content = new List<string>();
                    bool inBlock = false;
                    foreach (var line in lines)
                    {
                        if (line == "====ProbeContext====") { inBlock = true; continue; }
                        if (inBlock && line.StartsWith("Version = "))
                            version = line["Version = ".Length..].Trim();
                        else
                            content.Add(line);
                    }
                    version ??= "0.0.0.0";

                    if (!CanUpload(clientIp))
                    {
                        Log($"[TCP] {clientIp} 已达 {MaxUploadPerHour} 次/小时，拒绝");
                        byte[] deny = Encoding.UTF8.GetBytes("LIMIT\r\n");
                        await stream.WriteAsync(deny, 0, deny.Length);
                        continue;
                    }

                    lock (DailyLock)
                    {
                        File.AppendAllText(_todayFile, raw + Environment.NewLine);
                        _totalToday++;
                        _versionCounter[version] = _versionCounter.GetValueOrDefault(version) + 1;
                    }

                    Log($"[TCP] 追加写入 => {_todayFile} (今日第 {_totalToday} 条, ver={version})");
                    byte[] ok = Encoding.UTF8.GetBytes("OK\r\n");
                    await stream.WriteAsync(ok, 0, ok.Length);
                }
                catch (Exception ex)
                {
                    Log($"[TCP] 异常: {ex.Message}");
                }
            }
        }
        #endregion

        #region PROXY Protocol v2 解析
        private static class ProxyProtocolV2Reader
        {
            private static readonly byte[] Sig = "\r\n\r\n\0\r\nQUIT\n"u8.ToArray();

            public static async Task<(bool success, string ip)> TryGetRealIpAsync(NetworkStream stream)
            {
                var header = new byte[16];
                if (!await ReadExactlyAsync(stream, header, 0, 16))
                    return (false, string.Empty);

                for (int i = 0; i < 12; i++)
                    if (header[i] != Sig[i])
                        return (false, string.Empty);

                int verCmd = header[12];
                if ((verCmd & 0xF0) != 0x20) return (false, string.Empty);

                int family = header[13] >> 4;
                int len = (header[14] << 8) | header[15];

                var payload = new byte[len];
                if (!await ReadExactlyAsync(stream, payload, 0, len))
                    return (false, string.Empty);

                if (family == 0x01 && len >= 12)
                {
                    byte[] ipBytes = new byte[4];
                    Array.Copy(payload, 0, ipBytes, 0, 4);
                    var ip = new IPAddress(ipBytes).ToString();
                    return (true, ip);
                }

                return (false, string.Empty);
            }

            private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, int offset, int count)
            {
                int read = 0;
                while (read < count)
                {
                    int r = await stream.ReadAsync(buffer, offset + read, count - read);
                    if (r == 0) return false;
                    read += r;
                }
                return true;
            }
        }
        #endregion
    }
}