using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ProbeServer
{
    internal static class Program
    {
        private static readonly int TcpPort = 17600;
        private static readonly bool UseFrp = true;
        private static readonly int MaxUploadPerHour = 0;
        private static readonly int MaxPayloadBytes = 1024 * 512;

        private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe");
        private static readonly string CrashDir = Path.Combine(LogDir, "crash");
        private static readonly string SelfLog = Path.Combine(LogDir, "self.log");
        private static readonly string LimitDb = Path.Combine(LogDir, "upload_limit.json");

        private static readonly Dictionary<string, int> Counter = new();
        private static int _currentHour = -1;

        private static readonly object DailyLock = new();
        private static readonly object CrashLock = new();
        private static string _todayFile = string.Empty;
        private static readonly Dictionary<string, int> _versionCounter = new();
        private static int _totalToday = 0;
        private static Timer? _dailyTimer;

        private static void Main()
        {
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(CrashDir);
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
            if (due <= TimeSpan.Zero)
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
                if (_totalToday == 0) return;

                var sb = new StringBuilder();
                sb.AppendLine($"日期：{DateTime.Now:yyyy-MM-dd}");
                sb.AppendLine($"总启动次数：{_totalToday}");
                foreach (var kv in _versionCounter.OrderByDescending(v => v.Value))
                    sb.AppendLine($"版本 {kv.Key}：{kv.Value} 次");

                File.AppendAllText(Path.Combine(LogDir, "summary.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{sb}{Environment.NewLine}");

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
                    string clientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                    using var stream = client.GetStream();
                    var raw = await ReadPayloadWithOptionalProxyProtocolAsync(stream);

                    if (raw.ProxySuccess)
                    {
                        clientIp = raw.ProxyIp;
                        Log($"[TCP] PROXY v2 解析成功 real-ip=>{clientIp}");
                    }
                    else
                    {
                        Log($"[TCP] 连接 {clientIp}");
                    }

                    var payload = raw.Payload;

                    if (!CanUpload(clientIp))
                    {
                        Log($"[TCP] {clientIp} 已达 {MaxUploadPerHour} 次/小时，拒绝");
                        await WriteResponseAsync(stream, "LIMIT\r\n");
                        continue;
                    }

                    if (payload.StartsWith("====CrashReport====", StringComparison.Ordinal))
                    {
                        HandleCrashReport(payload, clientIp);
                    }
                    else if (payload.StartsWith("====ProbeContext====", StringComparison.Ordinal))
                    {
                        HandleProbe(payload, clientIp);
                    }
                    else
                    {
                        Log($"[TCP] 未识别的数据包，ip={clientIp}, length={Encoding.UTF8.GetByteCount(payload)}");
                    }

                    await WriteResponseAsync(stream, "OK\r\n");
                }
                catch (Exception ex)
                {
                    Log($"[TCP] 异常: {ex.Message}");
                }
            }
        }

        private static async Task<(string Payload, bool ProxySuccess, string ProxyIp)> ReadPayloadWithOptionalProxyProtocolAsync(NetworkStream stream)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            var firstRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (firstRead == 0) return (string.Empty, false, string.Empty);

            var offset = 0;
            var count = firstRead;
            var proxySuccess = false;
            var proxyIp = string.Empty;

            if (UseFrp && ProxyProtocolV2Reader.TryParse(buffer, firstRead, out var headerLength, out proxyIp))
            {
                proxySuccess = true;
                offset = headerLength;
                count = firstRead - headerLength;
            }

            if (count > 0)
                ms.Write(buffer, offset, count);

            await ReadRemainingPayloadAsync(stream, ms, buffer);

            return (Encoding.UTF8.GetString(ms.ToArray()), proxySuccess, proxyIp);
        }

        private static async Task ReadRemainingPayloadAsync(NetworkStream stream, MemoryStream ms, byte[] buffer)
        {
            using var timeoutCts = new CancellationTokenSource(300);
            while (ms.Length < MaxPayloadBytes)
            {
                try
                {
                    var readTask = stream.ReadAsync(buffer, 0, buffer.Length, timeoutCts.Token);
                    var completedTask = await Task.WhenAny(readTask, Task.Delay(50, timeoutCts.Token));
                    if (completedTask != readTask) break;

                    var read = await readTask;
                    if (read == 0) break;
                    ms.Write(buffer, 0, read);

                    if (!stream.DataAvailable) break;
                }
                catch
                {
                    break;
                }
            }
        }

        private static async Task WriteResponseAsync(NetworkStream stream, string response)
        {
            var data = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(data, 0, data.Length);
        }

        private static void HandleProbe(string raw, string clientIp)
        {
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

            lock (DailyLock)
            {
                File.AppendAllText(_todayFile, raw + Environment.NewLine);
                _totalToday++;
                _versionCounter[version] = _versionCounter.GetValueOrDefault(version) + 1;
            }

            Log($"[TCP] Probe 追加写入 => {_todayFile} (今日第 {_totalToday} 条, ip={clientIp}, ver={version})");
        }

        private static void HandleCrashReport(string raw, string clientIp)
        {
            var parsed = ParseCrashReport(raw);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var safeVersion = MakeSafeFileName(parsed.Version ?? "Unknown");
            var safeIp = MakeSafeFileName(clientIp);
            var fileName = $"crash_{timestamp}_{safeIp}_{safeVersion}.txt";
            var filePath = Path.Combine(CrashDir, fileName);

            lock (CrashLock)
            {
                Directory.CreateDirectory(CrashDir);
                File.WriteAllText(filePath, raw, Encoding.UTF8);
                File.AppendAllText(Path.Combine(CrashDir, "index.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ip={clientIp} version={parsed.Version ?? "Unknown"} file={fileName} clientFile={parsed.FileName ?? "Unknown"}{Environment.NewLine}");
            }

            Log($"[TCP] CrashReport 已保存 => {filePath} (ip={clientIp}, ver={parsed.Version ?? "Unknown"})");
        }

        private static (string? Version, string? FileName) ParseCrashReport(string raw)
        {
            string? version = null;
            string? fileName = null;
            var lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                if (line.Length == 0) break;
                if (line.StartsWith("Version = ", StringComparison.Ordinal))
                    version = line["Version = ".Length..].Trim();
                else if (line.StartsWith("FileName = ", StringComparison.Ordinal))
                    fileName = line["FileName = ".Length..].Trim();
            }

            return (version, fileName);
        }

        private static string MakeSafeFileName(string value)
        {
            var safe = value;
            foreach (var invalidChar in Path.GetInvalidFileNameChars())
                safe = safe.Replace(invalidChar, '_');
            return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
        }
        #endregion

        #region PROXY Protocol v2 解析
        private static class ProxyProtocolV2Reader
        {
            private static readonly byte[] Sig = "\r\n\r\n\0\r\nQUIT\n"u8.ToArray();

            public static bool TryParse(byte[] buffer, int length, out int headerLength, out string ip)
            {
                headerLength = 0;
                ip = string.Empty;

                if (length < 16) return false;

                for (int i = 0; i < 12; i++)
                    if (buffer[i] != Sig[i])
                        return false;

                int verCmd = buffer[12];
                if ((verCmd & 0xF0) != 0x20) return false;

                int family = buffer[13] >> 4;
                int len = (buffer[14] << 8) | buffer[15];
                headerLength = 16 + len;

                if (length < headerLength) return false;

                if (family == 0x01 && len >= 12)
                {
                    byte[] ipBytes = new byte[4];
                    Array.Copy(buffer, 16, ipBytes, 0, 4);
                    ip = new IPAddress(ipBytes).ToString();
                    return true;
                }

                return false;
            }
        }
        #endregion
    }
}
