using System.Net.Http.Headers;
using System.Text.Json;
using Lila.Desktop.Core.Api;
using Lila.Desktop.Core.Security;

namespace Lila.Desktop;

/// <summary>
/// Dark-themed inspector window for live daemon harness state:
/// Tasks (/tasks, /tasks/schedules), Environment / Devices (/environment),
/// Eval &amp; Traces (/eval, /traces), Experiences (/experiences),
/// and Capabilities (/capabilities/engine, /capabilities/gaps, /capabilities/candidates, /capabilities/audit).
/// </summary>
public sealed class HarnessInspectorForm : Form
{
    private static readonly Color BgWindow = Color.FromArgb(17, 21, 32);
    private static readonly Color BgPanel = Color.FromArgb(23, 28, 43);
    private static readonly Color BgInput = Color.FromArgb(13, 16, 24);
    private static readonly Color BorderSubtle = Color.FromArgb(52, 62, 86);
    private static readonly Color TextPrimary = Color.FromArgb(243, 245, 250);
    private static readonly Color TextMuted = Color.FromArgb(154, 163, 184);
    private static readonly Color AccentBlue = Color.FromArgb(124, 156, 255);

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;
    private readonly LilaApiClient _apiClient;

    private readonly Label _statusLabel = new();
    private readonly Button _refreshButton = new();
    private readonly Button[] _tabButtons;
    private readonly Panel[] _tabPanels;
    private readonly Label[] _tabSummaries;
    private readonly TextBox[] _tabOutputs;
    private readonly TextBox _capabilityInput = new();
    private readonly Button _discoverButton = new();
    private readonly Button _activateButton = new();
    private readonly Button _deactivateButton = new();
    private int _activeTabIndex;
    private CancellationTokenSource? _refreshCts;

    public HarnessInspectorForm(HttpClient http, ICredentialStore credentials)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _apiClient = new LilaApiClient(_http, _credentials);

        Text = "Harness Inspector";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(720, 480);
        Size = new Size(860, 580);
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = BgWindow;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16),
            BackColor = BgWindow
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        // Header bar
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = BgWindow,
            Margin = new Padding(0, 0, 0, 8)
        };
        var title = new Label
        {
            Text = "Harness Inspector",
            Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
            ForeColor = TextPrimary,
            AutoSize = true,
            Location = new Point(0, 2)
        };
        _statusLabel.Text = "Loading…";
        _statusLabel.Font = new Font("Segoe UI", 8.5f);
        _statusLabel.ForeColor = TextMuted;
        _statusLabel.AutoSize = true;
        _statusLabel.Location = new Point(2, 26);

        _refreshButton.Text = "Refresh";
        _refreshButton.FlatStyle = FlatStyle.Flat;
        _refreshButton.FlatAppearance.BorderColor = AccentBlue;
        _refreshButton.FlatAppearance.BorderSize = 1;
        _refreshButton.BackColor = BgPanel;
        _refreshButton.ForeColor = TextPrimary;
        _refreshButton.Size = new Size(96, 30);
        _refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refreshButton.Location = new Point(header.Width - 96, 6);
        _refreshButton.Cursor = Cursors.Hand;
        _refreshButton.Click += async (_, _) => await RefreshAllAsync();

        header.Controls.Add(title);
        header.Controls.Add(_statusLabel);
        header.Controls.Add(_refreshButton);
        root.Controls.Add(header, 0, 0);

        // Tab bar
        var tabBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            BackColor = BgWindow,
            Margin = new Padding(0, 0, 0, 8)
        };
        var tabTitles = new[]
        {
            "Tasks",
            "Environment / Devices",
            "Eval & Traces",
            "Experiences",
            "Capabilities"
        };
        _tabButtons = new Button[tabTitles.Length];
        _tabPanels = new Panel[tabTitles.Length];
        _tabSummaries = new Label[tabTitles.Length];
        _tabOutputs = new TextBox[tabTitles.Length];

        var contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BgPanel,
            Padding = new Padding(12)
        };

        for (var i = 0; i < tabTitles.Length; i++)
        {
            var idx = i;
            var btn = new Button
            {
                Name = $"TabButton:{tabTitles[i]}",
                Text = tabTitles[i],
                AutoSize = true,
                MinimumSize = new Size(110, 30),
                Padding = new Padding(10, 4, 10, 4),
                Margin = new Padding(0, 0, 6, 0),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.Click += (_, _) => SelectTab(idx);
            _tabButtons[i] = btn;
            tabBar.Controls.Add(btn);

            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgPanel,
                Visible = i == 0
            };
            var summary = new Label
            {
                Name = $"TabSummary:{tabTitles[i]}",
                Dock = DockStyle.Top,
                Height = 24,
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 9f),
                Text = "Waiting for refresh…"
            };
            var output = new TextBox
            {
                Name = $"TabOutput:{tabTitles[i]}",
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = BgInput,
                ForeColor = TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9.5f)
            };
            panel.Controls.Add(output);
            if (tabTitles[i] == "Capabilities")
            {
                var capToolbar = BuildCapabilityToolbar();
                panel.Controls.Add(capToolbar);
            }
            panel.Controls.Add(summary);
            _tabPanels[i] = panel;
            _tabSummaries[i] = summary;
            _tabOutputs[i] = output;
            contentHost.Controls.Add(panel);
        }

        root.Controls.Add(tabBar, 0, 1);
        root.Controls.Add(contentHost, 0, 2);
        Controls.Add(root);

        SelectTab(0);

        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                Close();
            }
            else if (e.KeyCode == Keys.F5)
            {
                e.SuppressKeyPress = true;
                await RefreshAllAsync();
            }
        };

        Shown += async (_, _) => await RefreshAllAsync();
        FormClosed += (_, _) =>
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
        };
    }

    private void SelectTab(int index)
    {
        _activeTabIndex = index;
        for (var i = 0; i < _tabPanels.Length; i++)
        {
            var active = i == index;
            _tabPanels[i].Visible = active;
            _tabButtons[i].BackColor = active ? BgPanel : BgInput;
            _tabButtons[i].ForeColor = active ? TextPrimary : TextMuted;
            _tabButtons[i].FlatAppearance.BorderColor = active ? AccentBlue : BorderSubtle;
        }
    }

    public async Task RefreshAllAsync()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = _refreshCts.Token;

        _refreshButton.Enabled = false;
        _statusLabel.Text = "Refreshing…";
        try
        {
            var tasksTask = FetchSectionAsync("tasks", ct);
            var schedulesTask = FetchSectionAsync("tasks/schedules", ct);
            var envTask = FetchSectionAsync("environment", ct);
            var evalTask = FetchSectionAsync("eval", ct);
            var tracesTask = FetchSectionAsync("traces?limit=50", ct);
            var expTask = FetchSectionAsync("experiences", ct);
            var capEngineTask = FetchSectionAsync("capabilities/engine", ct);
            var capGapsTask = FetchSectionAsync("capabilities/gaps", ct);
            var capCandTask = FetchSectionAsync("capabilities/candidates", ct);
            var capAuditTask = FetchSectionAsync("capabilities/audit?limit=25", ct);

            await Task.WhenAll(
                tasksTask, schedulesTask, envTask, evalTask,
                tracesTask, expTask, capEngineTask, capGapsTask,
                capCandTask, capAuditTask);

            var tasksResult = await tasksTask;
            var schedulesResult = await schedulesTask;
            var envResult = await envTask;
            var evalResult = await evalTask;
            var tracesResult = await tracesTask;
            var expResult = await expTask;
            var capEngineResult = await capEngineTask;
            var capGapsResult = await capGapsTask;
            var capCandResult = await capCandTask;
            var capAuditResult = await capAuditTask;

            if (IsDisposed)
                return;

            // Tab 0: Tasks + Schedules
            var taskCount = CountArrayProperty(tasksResult.Doc, "tasks");
            var schedCount = CountArrayProperty(schedulesResult.Doc, "schedules");
            _tabSummaries[0].Text = tasksResult.Error ?? schedulesResult.Error
                ?? $"Tasks: {taskCount}   |   Scheduled background runs: {schedCount}";
            _tabOutputs[0].Text = $"=== GET /tasks ===\r\n{tasksResult.Text}\r\n\r\n=== GET /tasks/schedules ===\r\n{schedulesResult.Text}";

            // Tab 1: Environment / Devices
            _tabSummaries[1].Text = envResult.Error ?? "EnvironmentalWorldModel & workspace devices snapshot (/environment)";
            _tabOutputs[1].Text = envResult.Text;

            // Tab 2: Eval & Traces
            var traceCount = CountArrayProperty(tracesResult.Doc, "traces");
            _tabSummaries[2].Text = evalResult.Error ?? tracesResult.Error
                ?? $"Evaluation summary (/eval)   |   Recent traces: {traceCount} (/traces)";
            _tabOutputs[2].Text = $"=== GET /eval ===\r\n{evalResult.Text}\r\n\r\n=== GET /traces ===\r\n{tracesResult.Text}";

            // Tab 3: Experiences
            var expCount = CountArrayProperty(expResult.Doc, "experiences");
            _tabSummaries[3].Text = expResult.Error ?? $"Verified experiences: {expCount} (/experiences)";
            _tabOutputs[3].Text = expResult.Text;

            // Tab 4: Capabilities (Engine + Gaps + Candidates + Audit)
            var bundleCount = CountArrayProperty(capEngineResult.Doc, "bundles");
            var skillCount = CountArrayProperty(capEngineResult.Doc, "skills");
            var gapCount = CountArrayProperty(capGapsResult.Doc, "gaps");
            var candCount = CountArrayProperty(capCandResult.Doc, "candidates");
            var auditOk = ReadAuditIntegrityOk(capAuditResult.Doc);
            var activeCaps = ReadActiveCapabilitiesSummary(capEngineResult.Doc);
            _tabSummaries[4].Text = capEngineResult.Error ?? capGapsResult.Error ?? capAuditResult.Error
                ?? $"Bundles: {bundleCount} | Skills: {skillCount} | Active: {activeCaps} | Gaps: {gapCount} | Candidates: {candCount} | Audit: {(auditOk ? "OK" : "CHECK")}";
            _tabOutputs[4].Text =
                $"=== GET /capabilities/engine ===\r\n{capEngineResult.Text}\r\n\r\n" +
                $"=== GET /capabilities/gaps ===\r\n{capGapsResult.Text}\r\n\r\n" +
                $"=== GET /capabilities/candidates ===\r\n{capCandResult.Text}\r\n\r\n" +
                $"=== GET /capabilities/audit?limit=25 ===\r\n{capAuditResult.Text}";

            _statusLabel.Text = $"Last refreshed at {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
                _statusLabel.Text = "Refresh timed out or cancelled.";
        }
        finally
        {
            if (!IsDisposed)
                _refreshButton.Enabled = true;
        }
    }

    private async Task<(string Text, JsonDocument? Doc, string? Error)> FetchSectionAsync(
        string route, CancellationToken ct)
    {
        var token = _credentials.ReadToken();
        if (string.IsNullOrWhiteSpace(token))
            return ("No Lila API credential is saved. Add it in Settings.", null, "Missing API credential.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(LilaApiClient.BaseAddress, route));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return ($"HTTP {(int)response.StatusCode}: {raw}", null, $"HTTP {(int)response.StatusCode} from /{route}");

            var doc = JsonDocument.Parse(raw);
            var pretty = JsonSerializer.Serialize(doc.RootElement, PrettyJson).Replace("\n", "\r\n");
            return (pretty, doc, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return ($"Failed to load /{route}: {ex.Message}", null, $"Error loading /{route}");
        }
    }

    private Control BuildCapabilityToolbar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            WrapContents = false,
            BackColor = BgPanel,
            Padding = new Padding(0, 2, 0, 4)
        };

        var hint = new Label
        {
            Text = "Query / Bundle ID:",
            AutoSize = true,
            ForeColor = TextMuted,
            Margin = new Padding(0, 6, 6, 0)
        };

        _capabilityInput.Name = "CapabilityQueryBox";
        _capabilityInput.Width = 240;
        _capabilityInput.BackColor = BgInput;
        _capabilityInput.ForeColor = TextPrimary;
        _capabilityInput.BorderStyle = BorderStyle.FixedSingle;
        _capabilityInput.Text = "cap.sandbox-lab";
        _capabilityInput.Margin = new Padding(0, 2, 8, 0);

        ConfigureActionButton(_discoverButton, "CapDiscoverBtn", "Discover", async () => await RunCapabilityActionAsync("discover"));
        ConfigureActionButton(_activateButton, "CapActivateBtn", "Activate", async () => await RunCapabilityActionAsync("activate"));
        ConfigureActionButton(_deactivateButton, "CapDeactivateBtn", "Deactivate", async () => await RunCapabilityActionAsync("deactivate"));

        bar.Controls.Add(hint);
        bar.Controls.Add(_capabilityInput);
        bar.Controls.Add(_discoverButton);
        bar.Controls.Add(_activateButton);
        bar.Controls.Add(_deactivateButton);
        return bar;
    }

    private void ConfigureActionButton(Button btn, string name, string text, Func<Task> onClick)
    {
        btn.Name = name;
        btn.Text = text;
        btn.AutoSize = true;
        btn.MinimumSize = new Size(86, 26);
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderColor = AccentBlue;
        btn.FlatAppearance.BorderSize = 1;
        btn.BackColor = BgInput;
        btn.ForeColor = TextPrimary;
        btn.Cursor = Cursors.Hand;
        btn.Margin = new Padding(0, 1, 6, 0);
        btn.Click += async (_, _) => await onClick();
    }

    public async Task RunCapabilityActionAsync(string action)
    {
        var value = (_capabilityInput.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            _statusLabel.Text = "Enter a search query or capability_id first.";
            return;
        }

        _discoverButton.Enabled = false;
        _activateButton.Enabled = false;
        _deactivateButton.Enabled = false;
        try
        {
            CapabilityControlResult res = action switch
            {
                "discover" => await _apiClient.DiscoverCapabilitiesAsync(value),
                "activate" => await _apiClient.ActivateCapabilityAsync(value, reason: "Activated via Windows Harness Inspector"),
                "deactivate" => await _apiClient.DeactivateCapabilityAsync(value, outcomeSummary: "Deactivated via Windows Harness Inspector"),
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
            await RefreshAllAsync();
            if (!IsDisposed)
            {
                _tabOutputs[4].Text =
                    $"=== LAST ACTION ({action.ToUpperInvariant()}: {value}) ===\r\n{res.Output.Replace("\n", "\r\n")}\r\n\r\n" +
                    _tabOutputs[4].Text;
                _statusLabel.Text = res.Output.Split('\n')[0];
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                _statusLabel.Text = $"Capability {action} failed: {ex.Message}";
        }
        finally
        {
            if (!IsDisposed)
            {
                _discoverButton.Enabled = true;
                _activateButton.Enabled = true;
                _deactivateButton.Enabled = true;
            }
        }
    }

    private static bool ReadAuditIntegrityOk(JsonDocument? doc)
    {
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return false;
        return doc.RootElement.TryGetProperty("integrity_ok", out var prop) &&
               prop.ValueKind == JsonValueKind.True;
    }

    private static string ReadActiveCapabilitiesSummary(JsonDocument? doc)
    {
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return "none";
        if (!doc.RootElement.TryGetProperty("summary", out var summary) ||
            summary.ValueKind != JsonValueKind.Object ||
            !summary.TryGetProperty("active_capabilities", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            return "none";
        }
        if (arr.GetArrayLength() == 0)
            return "none";
        var ids = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                ids.Add(s);
        }
        return ids.Count == 0 ? "none" : string.Join(", ", ids);
    }

    private static int CountArrayProperty(JsonDocument? doc, string propertyName)
    {
        if (doc is null)
            return 0;
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty(propertyName, out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            return arr.GetArrayLength();
        }
        return 0;
    }
}
