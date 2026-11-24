using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Lila.Desktop;

internal static class NativeBridgeCli
{
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const int Srccopy = 0x00CC0020;
    private const int Captureblt = 0x40000000;
    private const int SwRestore = 9;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(string lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr ho);

    internal static string GetPipeName()
    {
        var raw = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return $"LilaDesktopBridge_{sb}";
    }

    internal static IDisposable StartPipeServer(Control uiInvoker) => new PipeServerHost(uiInvoker);

    internal static bool TryHandleArgs(string[] args)
    {
        if (args.Length == 0)
            return false;

        if (args[0].Equals("--capture-screen", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                Console.Error.WriteLine("{\"ok\":false,\"error\":\"Missing output path.\"}");
                Environment.ExitCode = 1;
                return true;
            }
            var outPath = args[1];
            Rectangle? region = null;
            if (args.Length >= 6 &&
                int.TryParse(args[2], out var x) &&
                int.TryParse(args[3], out var y) &&
                int.TryParse(args[4], out var w) &&
                int.TryParse(args[5], out var h) &&
                w > 0 && h > 0)
            {
                region = new Rectangle(x, y, w, h);
            }

            try
            {
                var reqObj = region.HasValue
                    ? JsonSerializer.Serialize(new
                    {
                        op = "capture_screen",
                        out_path = outPath,
                        region = new[] { region.Value.X, region.Value.Y, region.Value.Width, region.Value.Height }
                    })
                    : JsonSerializer.Serialize(new { op = "capture_screen", out_path = outPath });

                if (TrySendPipeRequest(reqObj, out var pipeResp))
                {
                    Console.WriteLine(pipeResp);
                    using var doc = JsonDocument.Parse(pipeResp);
                    Environment.ExitCode = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean() ? 0 : 1;
                    return true;
                }

                var (width, height) = CaptureScreenToFile(outPath, region);
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, width, height }));
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
                Environment.ExitCode = 1;
            }
            return true;
        }

        if (args[0].Equals("--input-action", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                Console.Error.WriteLine("{\"ok\":false,\"error\":\"Missing base64 JSON payload.\"}");
                Environment.ExitCode = 1;
                return true;
            }
            try
            {
                var jsonText = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
                using var doc = JsonDocument.Parse(jsonText);
                var reqObj = JsonSerializer.Serialize(new { op = "input_action", payload = doc.RootElement });
                if (TrySendPipeRequest(reqObj, out var pipeResp))
                {
                    Console.WriteLine(pipeResp);
                    using var respDoc = JsonDocument.Parse(pipeResp);
                    Environment.ExitCode = respDoc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean() ? 0 : 1;
                    return true;
                }

                var resultJson = ExecuteInputAction(doc.RootElement);
                Console.WriteLine(resultJson);
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
                Environment.ExitCode = 1;
            }
            return true;
        }

        return false;
    }

    private static bool TrySendPipeRequest(string jsonLine, out string responseLine)
    {
        responseLine = "";
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                GetPipeName(),
                PipeDirection.InOut,
                PipeOptions.CurrentUserOnly);
            client.Connect(250);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            writer.WriteLine(jsonLine);
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
                return false;
            responseLine = line;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static (int Width, int Height) CaptureScreenToFile(string outPath, Rectangle? region)
    {
        var bounds = region ?? (Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            bounds = new Rectangle(0, 0, 1920, 1080);

        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        try
        {
            using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            bmp.Save(outPath, ImageFormat.Png);
            return (bounds.Width, bounds.Height);
        }
        catch (Win32Exception)
        {
            return CaptureViaDisplayDc(outPath, bounds);
        }
    }

    private static (int Width, int Height) CaptureViaDisplayDc(string outPath, Rectangle bounds)
    {
        var hdcScreen = CreateDC("DISPLAY", null, null, IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDC(DISPLAY) failed.");
        var hdcMem = IntPtr.Zero;
        var hBitmap = IntPtr.Zero;
        var hOld = IntPtr.Zero;
        try
        {
            hdcMem = CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleDC failed.");
            hBitmap = CreateCompatibleBitmap(hdcScreen, bounds.Width, bounds.Height);
            if (hBitmap == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleBitmap failed.");
            hOld = SelectObject(hdcMem, hBitmap);
            if (!BitBlt(hdcMem, 0, 0, bounds.Width, bounds.Height, hdcScreen, bounds.X, bounds.Y, Srccopy | Captureblt))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "BitBlt failed.");
            SelectObject(hdcMem, hOld);
            hOld = IntPtr.Zero;
            using var bmp = Image.FromHbitmap(hBitmap);
            bmp.Save(outPath, ImageFormat.Png);
            return (bounds.Width, bounds.Height);
        }
        finally
        {
            if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero)
                SelectObject(hdcMem, hOld);
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero)
                DeleteDC(hdcMem);
            DeleteDC(hdcScreen);
        }
    }

    internal static string ExecuteInputAction(JsonElement payload)
    {
        var action = payload.TryGetProperty("action", out var actEl)
            ? (actEl.GetString() ?? "").Trim().ToLowerInvariant()
            : "";

        switch (action)
        {
            case "ping":
            case "noop":
                return JsonSerializer.Serialize(new { ok = true, action, bridge = "native" });

            case "click":
            {
                var x = payload.GetProperty("x").GetInt32();
                var y = payload.GetProperty("y").GetInt32();
                var btn = payload.TryGetProperty("button", out var bEl)
                    ? (bEl.GetString() ?? "left").Trim().ToLowerInvariant()
                    : "left";
                var clicks = payload.TryGetProperty("clicks", out var cEl) ? Math.Max(1, cEl.GetInt32()) : 1;
                var (down, up) = btn switch
                {
                    "right" => (MouseRightDown, MouseRightUp),
                    "middle" => (MouseMiddleDown, MouseMiddleUp),
                    _ => (MouseLeftDown, MouseLeftUp),
                };
                if (!SetCursorPos(x, y))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed.");
                for (var i = 0; i < clicks; i++)
                {
                    mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(up, 0, 0, 0, UIntPtr.Zero);
                }
                return JsonSerializer.Serialize(new { ok = true, action, x, y, button = btn, clicks });
            }

            case "type_text":
            {
                var text = payload.TryGetProperty("text", out var tEl) ? (tEl.GetString() ?? "") : "";
                SendKeys.SendWait(EscapeSendKeys(text));
                return JsonSerializer.Serialize(new { ok = true, action });
            }

            case "key_combo":
            {
                var sendStr = BuildSendKeysCombo(payload);
                SendKeys.SendWait(sendStr);
                return JsonSerializer.Serialize(new { ok = true, action });
            }

            case "focus_window":
            {
                var window = "";
                if (payload.TryGetProperty("window", out var wEl) && wEl.ValueKind == JsonValueKind.String)
                    window = (wEl.GetString() ?? "").Trim();
                else if (payload.TryGetProperty("title", out var tiEl) && tiEl.ValueKind == JsonValueKind.String)
                    window = (tiEl.GetString() ?? "").Trim();
                if (string.IsNullOrEmpty(window))
                    throw new ArgumentException("window is required for focus_window.");

                var match = Process.GetProcesses()
                    .FirstOrDefault(p =>
                    {
                        try
                        {
                            return p.MainWindowHandle != IntPtr.Zero &&
                                   p.MainWindowTitle.Contains(window, StringComparison.OrdinalIgnoreCase);
                        }
                        catch
                        {
                            return false;
                        }
                    });
                if (match is null)
                    throw new InvalidOperationException($"Window matching '{window}' not found.");
                ShowWindowAsync(match.MainWindowHandle, SwRestore);
                SetForegroundWindow(match.MainWindowHandle);
                return JsonSerializer.Serialize(new { ok = true, action, title = match.MainWindowTitle });
            }

            default:
                throw new ArgumentException($"Unsupported action kind: {action}");
        }
    }

    private static string EscapeSendKeys(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (var ch in text)
        {
            if ("+^%~(){}".Contains(ch))
                sb.Append('{').Append(ch).Append('}');
            else
                sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string BuildSendKeysCombo(JsonElement payload)
    {
        var keys = new List<string>();
        if (payload.TryGetProperty("keys", out var kEl))
        {
            if (kEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in kEl.EnumerateArray())
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        keys.Add(s.Trim());
                }
            }
            else if (kEl.ValueKind == JsonValueKind.String)
            {
                foreach (var part in (kEl.GetString() ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    keys.Add(part);
            }
        }
        if (keys.Count == 0)
            throw new ArgumentException("keys is required for key_combo.");

        var sb = new StringBuilder();
        foreach (var key in keys)
        {
            var low = key.ToLowerInvariant();
            switch (low)
            {
                case "ctrl":
                case "control":
                    sb.Append('^');
                    break;
                case "alt":
                    sb.Append('%');
                    break;
                case "shift":
                    sb.Append('+');
                    break;
                default:
                    if (key.Length == 1)
                        sb.Append(EscapeSendKeys(key));
                    else
                        sb.Append('{').Append(key.ToUpperInvariant()).Append('}');
                    break;
            }
        }
        return sb.ToString();
    }

    private sealed class PipeServerHost : IDisposable
    {
        private readonly Control _uiInvoker;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loopTask;

        public PipeServerHost(Control uiInvoker)
        {
            _uiInvoker = uiInvoker;
            _loopTask = Task.Run(RunLoopAsync);
        }

        private async Task RunLoopAsync()
        {
            var pipeName = GetPipeName();
            while (!_cts.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        4,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    _ = HandleClientAsync(server);
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch
                {
                    server?.Dispose();
                    try { await Task.Delay(200, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream server)
        {
            await using (server)
            {
                try
                {
                    using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                    await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line))
                        return;
                    var resp = ProcessPipeJson(line);
                    await writer.WriteLineAsync(resp).ConfigureAwait(false);
                }
                catch
                {
                    // Client disconnected or timed out.
                }
            }
        }

        private string ProcessPipeJson(string rawJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;
                var op = root.TryGetProperty("op", out var opEl) ? (opEl.GetString() ?? "") : "";
                if (op == "capture_screen")
                {
                    var outPath = root.GetProperty("out_path").GetString() ?? "";
                    Rectangle? region = null;
                    if (root.TryGetProperty("region", out var rEl) && rEl.ValueKind == JsonValueKind.Array && rEl.GetArrayLength() == 4)
                    {
                        region = new Rectangle(
                            rEl[0].GetInt32(),
                            rEl[1].GetInt32(),
                            rEl[2].GetInt32(),
                            rEl[3].GetInt32());
                    }
                    var (w, h) = RunOnUi(() => CaptureScreenToFile(outPath, region));
                    return JsonSerializer.Serialize(new { ok = true, width = w, height = h });
                }
                if (op == "input_action")
                {
                    var payload = root.GetProperty("payload").Clone();
                    return RunOnUi(() => ExecuteInputAction(payload));
                }
                return JsonSerializer.Serialize(new { ok = false, error = $"Unknown pipe operation '{op}'." });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
        }

        private T RunOnUi<T>(Func<T> func)
        {
            if (_uiInvoker.IsHandleCreated && !_uiInvoker.IsDisposed && _uiInvoker.InvokeRequired)
                return (T)_uiInvoker.Invoke(func);
            return func();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _loopTask.Wait(300); } catch { }
            _cts.Dispose();
        }
    }
}
