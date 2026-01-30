using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using PuppeteerSharp;
using System.Net.Mail;
using System.IO;
using System.Linq;
using System.Text;

namespace ChatService2
{
    public sealed class RpaTaskPollerService : BackgroundService
    {
        private readonly ILogger<RpaTaskPollerService> _logger;
        private const int DbCommandTimeoutSeconds = 60;
        private static DateTime _lastQueueAlertUtc = DateTime.MinValue;
        private int _pollIntervalMinutes;
        private readonly string? _connectionString;

        public RpaTaskPollerService(ILogger<RpaTaskPollerService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _pollIntervalMinutes = GetPollIntervalMinutes();
            _connectionString = configuration["CHAT_CONNECTION_STRING"] ?? configuration.GetConnectionString("Chat");
            if (string.IsNullOrWhiteSpace(_connectionString))
                LogWarn("CHAT_CONNECTION_STRING was not provided via configuration or ConnectionStrings:Chat");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            LogInfo("ChatService2 starting");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync();
                }
                catch (Exception ex)
                {
                LogError(ex, "Tick failed");
                }

                await Task.Delay(TimeSpan.FromMinutes(_pollIntervalMinutes), stoppingToken);
            }

            LogInfo("ChatService2 stopping");
        }

        private static int GetPollIntervalMinutes()
        {
            var s = Environment.GetEnvironmentVariable("PollIntervalMinutes");
            if (int.TryParse(s, out var minutes) && minutes > 0 && minutes <= 1440)
                return minutes;
            return 1;
        }

        // connection string is injected via IConfiguration into the constructor

        private async Task TickAsync()
        {
            LogInfo("Tick started");

            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                LogError("CHAT_CONNECTION_STRING is empty.");
                return;
            }

            try
            {
                EnsureRpaTasksTable();
            }
            catch (Exception ex)
            {
                LogError(ex, "EnsureRpaTasksTable failed");
            }

            var count = GetPendingTasksCount();
            LogInfo("Pending RPA tasks: {Count}", count);
            if (count <= 0)
            {
                LogInfo("No pending tasks; skipping.");
                return;
            }

            var task = TryGetNextTask();
            if (task == null)
            {
                LogInfo("No task found to process");
                return;
            }

            if (string.IsNullOrWhiteSpace(task.AdsPowerId))
            {
                LogError("Task has empty ads_power_id; skipping");
                return;
            }

            try
            {
                LogInfo("Processing RPA task: {AdsPowerId} {ScriptName} {Id}", task.AdsPowerId, task.ScriptName, task.Id);
                string wsEndpoint = string.IsNullOrWhiteSpace(task.Puppeteer) ? task.Value : task.Puppeteer;
                var scriptNorm = (task.ScriptName ?? string.Empty).Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
                var processed = false;

                if (scriptNorm.Contains("telegram") && scriptNorm.Contains("start") && scriptNorm.Contains("login")
                    || scriptNorm == "telegramstartlogin" || scriptNorm == "startlogintelegram")
                {
                    processed = true;
                    try
                    {
                        await RunTelegramStartLoginPuppeteerAsync(wsEndpoint, task.AdsPowerId, task.Value);
                    }
                    catch (Exception ex)
                    {
                        LogError(ex, "TelegramStartLogin script failed");
                    }
                }
                else if (scriptNorm.Contains("whatsapp") && scriptNorm.Contains("start") && scriptNorm.Contains("login")
                         || scriptNorm == "whatsappstartlogin" || scriptNorm == "startloginwhatsapp")
                {
                    processed = true;
                    try
                    {
                        await RunWhatsappStartLoginPuppeteerAsync(wsEndpoint, task.AdsPowerId, task.Value);
                    }
                    catch (Exception ex)
                    {
                        LogError(ex, "WhatsappStartLogin script failed");
                    }
                }
                else if (scriptNorm.Contains("max") && scriptNorm.Contains("start") && scriptNorm.Contains("login")
                         || scriptNorm == "maxstartlogin" || scriptNorm == "startloginmax")
                {
                    processed = true;
                    try
                    {
                        await RunMaxStartLoginPuppeteerAsync(wsEndpoint, task.AdsPowerId);
                    }
                    catch (Exception ex)
                    {
                        LogError(ex, "MaxStartLogin script failed");
                    }
                }
                else
                {
                    LogError("Unrecognized script_name: {ScriptName}", task.ScriptName);
                }

                if (processed)
                {
                    var deleted = TryDeleteTask(task.Id);
                    LogInfo(deleted ? "Task deleted" : "Task NOT deleted");
                }
                else
                {
                    LogInfo("Task NOT deleted because it was not processed");
                }
            }
            catch (Exception ex)
            {
                LogError(ex, "Task processing failed");
            }

            LogInfo("Tick finished");
        }

        private sealed class RpaTask
        {
            public int Id { get; set; }
            public string? AdsPowerId { get; set; }
            public string? ScriptName { get; set; }
            public string? Value { get; set; }
            public string? Puppeteer { get; set; }
        }

        private RpaTask? TryGetNextTask()
        {
            try
            {
                using var cn = new SqlConnection(_connectionString!);
                using var cmd = cn.CreateCommand();
                cmd.CommandTimeout = DbCommandTimeoutSeconds;
                cmd.CommandText = "SELECT TOP (1) id, ads_power_id, script_name, value, puppeteer FROM dbo.rpa_tasks ORDER BY id";
                cn.Open();
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                    return null;
                var task = new RpaTask
                {
                    Id = r[0] == DBNull.Value ? 0 : Convert.ToInt32(r[0]),
                    AdsPowerId = r[1] == DBNull.Value ? null : Convert.ToString(r[1]),
                    ScriptName = r[2] == DBNull.Value ? null : Convert.ToString(r[2]),
                    Value = r[3] == DBNull.Value ? null : Convert.ToString(r[3]),
                    Puppeteer = r.FieldCount > 4 && r[4] != DBNull.Value ? Convert.ToString(r[4]) : null
                };
                LogInfo("Fetched task from DB: {Id} {AdsPowerId} Value={Value} PuppeteerPresent={PuppeteerPresent}",
                    task.Id, task.AdsPowerId, task.Value, string.IsNullOrWhiteSpace(task.Puppeteer) ? "no" : "yes");
                return task;
            }
            catch (Exception ex)
            {
                LogError(ex, "Failed to read next rpa_task");
                return null;
            }
        }

        private bool TryDeleteTask(int id)
        {
            try
            {
                using var cn = new SqlConnection(_connectionString!);
                using var cmd = cn.CreateCommand();
                cmd.CommandTimeout = DbCommandTimeoutSeconds;
                cmd.CommandText = "DELETE FROM dbo.rpa_tasks WHERE id = @id";
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
                cn.Open();
                return cmd.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                LogError(ex, "Failed to delete rpa_task");
                return false;
            }
        }

        private int GetPendingTasksCount()
        {
            using var cn = new SqlConnection(_connectionString!);
            using var cmd = cn.CreateCommand();
            cmd.CommandTimeout = DbCommandTimeoutSeconds;
            cmd.CommandText = "SELECT COUNT(1) FROM dbo.rpa_tasks";
            cn.Open();
            var obj = cmd.ExecuteScalar();
            return obj == null || obj == DBNull.Value ? 0 : Convert.ToInt32(obj);
        }

        private void EnsureRpaTasksTable()
        {
            using var cn = new SqlConnection(_connectionString!);
            using var cmd = cn.CreateCommand();
            cmd.CommandTimeout = DbCommandTimeoutSeconds;
            cmd.CommandText = @"
IF OBJECT_ID('dbo.rpa_tasks', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.rpa_tasks(
        id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ads_power_id NVARCHAR(128) NOT NULL,
        script_name NVARCHAR(128) NOT NULL,
        value NVARCHAR(128) NULL,
        puppeteer NVARCHAR(2048) NULL,
        created DATETIME NOT NULL DEFAULT(GETDATE())
    );
    CREATE INDEX IX_rpa_tasks_ads_power_id ON dbo.rpa_tasks(ads_power_id);
    CREATE INDEX IX_rpa_tasks_created ON dbo.rpa_tasks(created);
END
ELSE IF COL_LENGTH('dbo.rpa_tasks','puppeteer') IS NULL
BEGIN
    ALTER TABLE dbo.rpa_tasks ADD puppeteer NVARCHAR(2048) NULL;
END";
            cn.Open();
            cmd.ExecuteNonQuery();
        }

        private async Task RunTelegramStartLoginPuppeteerAsync(string? wsEndpoint, string accId, string? userName)
        {
            LogInfo("[Puppeteer] Start Telegram login for accId={AccId}, userName={UserName}", accId, userName);
            try
            {
                if (string.IsNullOrWhiteSpace(wsEndpoint))
                {
                    LogError("[Puppeteer] wsEndpoint is empty for accId={AccId}", accId);
                    return;
                }

                var browser = await Puppeteer.ConnectAsync(new ConnectOptions { BrowserWSEndpoint = wsEndpoint });
                Page? page = null;
                try
                {
                    // Fix for CS0266: Add explicit cast from IPage to Page
                    var pages = await browser.PagesAsync();
                    if (pages != null && pages.Length > 0)
                        page = (Page)pages[0];
                }
                catch (TimeoutException tex)
                {
                    LogWarn(tex, "[Puppeteer] browser.PagesAsync() timed out, will try NewPageAsync");
                }
                catch (Exception ex)
                {
                    LogWarn(ex, "[Puppeteer] browser.PagesAsync() failed, will try NewPageAsync");
                }

                if (page == null)
                {
                    try
                    {
                        page = (Page)await browser.NewPageAsync();
                    }
                    catch (Exception ex)
                    {
                        LogError(ex, "[Puppeteer] Failed to create a new page for accId={AccId}", accId);
                        return;
                    }
                }

                await page.GoToAsync("https://web.telegram.org/a/");
                LogInfo("[Puppeteer] Navigated to Telegram web");
                await page.WaitForSelectorAsync("div.qr-container image");
                LogInfo("[Puppeteer] QR container found");
                var screenshotDir = Path.Combine("C:\\", ".ADSPOWER_GLOBAL", "RPA", "screenshot");
                try
                {
                    Directory.CreateDirectory(screenshotDir);
                }
                catch (Exception ex)
                {
                    LogWarn(ex, "[Puppeteer] Failed to create screenshot directory {Dir}", screenshotDir);
                }
                var screenshotPath = Path.Combine(screenshotDir, accId + ".png");
                await page.ScreenshotAsync(screenshotPath);
                LogInfo("[Puppeteer] Screenshot saved: {Path}", screenshotPath);
                await page.ClickAsync("button[class*=auth-button]");
                LogInfo("[Puppeteer] Auth button clicked");
                await page.WaitForSelectorAsync("input#sign-in-phone-number");
                // Clear the input using DOM (avoid relying on Keyboard API) and type phone with leading '+'
                try
                {
                    await page.EvaluateFunctionAsync(@"selector => {
                        var el = document.querySelector(selector);
                        if (el) {
                            el.focus();
                            el.value = '';
                            var ev = new Event('input', { bubbles: true });
                            el.dispatchEvent(ev);
                        }
                    }", "input#sign-in-phone-number");
                }
                catch (Exception ex)
                {
                    LogWarn(ex, "[Puppeteer] Failed to clear phone input for accId={AccId}", accId);
                }

                var phoneToType = string.Empty;
                if (!string.IsNullOrWhiteSpace(userName))
                    phoneToType = userName.StartsWith("+") ? userName : "+" + userName;

                if (!string.IsNullOrEmpty(phoneToType))
                {
                    await page.TypeAsync("input#sign-in-phone-number", phoneToType);
                }
                LogInfo("[Puppeteer] Phone entered");
                await page.ClickAsync("button[type=submit]");
                LogInfo("[Puppeteer] Submit button clicked");
            }
            catch (Exception ex)
            {
                LogError(ex, "[Puppeteer] Telegram login failed for accId={AccId}", accId);
            }
        }

        private async Task RunWhatsappStartLoginPuppeteerAsync(string? wsEndpoint, string accId, string? userName)
        {
            LogInfo("[Puppeteer] Start WhatsApp login for accId={AccId}, userName={UserName}", accId, userName);
            try
            {
                if (string.IsNullOrWhiteSpace(wsEndpoint))
                {
                    LogError("[Puppeteer] wsEndpoint is empty for accId={AccId}", accId);
                    return;
                }

                var browser = await Puppeteer.ConnectAsync(new ConnectOptions { BrowserWSEndpoint = wsEndpoint });
                Page? page = null;
                try
                {
                    // Fix for CS0266: Add explicit cast from IPage to Page
                    var pages = await browser.PagesAsync();
                    if (pages != null && pages.Length > 0)
                        page = (Page)pages[0];
                }
                catch (TimeoutException tex)
                {
                    LogWarn(tex, "[Puppeteer] browser.PagesAsync() timed out, will try NewPageAsync");
                }
                catch (Exception ex)
                {
                    LogWarn(ex, "[Puppeteer] browser.PagesAsync() failed, will try NewPageAsync");
                }

                if (page == null)
                {
                    try
                    {
                        page = (Page)await browser.NewPageAsync();
                    }   
                    catch (Exception ex)
                    {
                        LogError(ex, "[Puppeteer] Failed to create a new page for accId={AccId}", accId);
                        return;
                    }
                }

                await page.GoToAsync("https://web.whatsapp.com/");
                LogInfo("[Puppeteer] Navigated to WhatsApp web");
                await page.WaitForSelectorAsync("div[class*=qr]");
                LogInfo("[Puppeteer] QR found");
                var screenshotDir = Path.Combine("C:\\", ".ADSPOWER_GLOBAL", "RPA", "screenshot");
                try
                {
                    Directory.CreateDirectory(screenshotDir);
                }
                catch (Exception ex)
                {
                    LogWarn(ex, "[Puppeteer] Failed to create screenshot directory {Dir}", screenshotDir);
                }
                var screenshotPath = Path.Combine(screenshotDir, accId + ".png");
                await page.ScreenshotAsync(screenshotPath);
                LogInfo("[Puppeteer] Screenshot saved: {Path}", screenshotPath);
                await page.ClickAsync("div:text('Log in with phone number')");
                LogInfo("[Puppeteer] Phone login button clicked");
                await page.WaitForSelectorAsync("input[aria-label]");
                // clear field by backspaces then type phone with leading '+'
                try
                {
                    await page.EvaluateFunctionAsync(@"selector => {
                        var el = document.querySelector(selector);
                        if (el) {
                            el.focus();
                            el.value = '';
                            var ev = new Event('input', { bubbles: true });
                            el.dispatchEvent(ev);
                        }
                    }", "input[aria-label]");
                }
                catch (Exception ex)
                {
                    LogWarn(ex, "[Puppeteer] Failed to clear WhatsApp phone input for accId={AccId}", accId);
                }

                var waPhone = string.Empty;
                if (!string.IsNullOrWhiteSpace(userName))
                    waPhone = userName.StartsWith("+") ? userName : "+" + userName;
                if (!string.IsNullOrEmpty(waPhone))
                    await page.TypeAsync("input[aria-label]", waPhone);
                LogInfo("[Puppeteer] Phone entered");
                await page.ClickAsync("div:text('Next')");
                LogInfo("[Puppeteer] Next button clicked");
            }
            catch (Exception ex)
            {
                LogError(ex, "[Puppeteer] WhatsApp login failed for accId={AccId}", accId);
            }
        }

        private async Task RunMaxStartLoginPuppeteerAsync(string? wsEndpoint, string accId)
        {
            LogInfo("[Puppeteer] Start Max login for accId={AccId}", accId);
            try
            {
                if (string.IsNullOrWhiteSpace(wsEndpoint))
                {
                    LogError("[Puppeteer] wsEndpoint is empty for accId={AccId}", accId);
                    return;
                }

                var browser = await Puppeteer.ConnectAsync(new ConnectOptions { BrowserWSEndpoint = wsEndpoint });
                Page? page = null;
                try
                {
                    // Fix for CS0266: Add explicit cast from IPage to Page
                    var pages = await browser.PagesAsync();
                    if (pages != null && pages.Length > 0)
                        page = (Page)pages[0];
                }
                catch (TimeoutException tex)
                {
                    LogWarn(tex, "[Puppeteer] browser.PagesAsync() timed out, will try NewPageAsync");
                }
                catch (Exception ex)
                {
                    LogWarn(ex, "[Puppeteer] browser.PagesAsync() failed, will try NewPageAsync");
                }

                if (page == null)
                {
                    try
                    {
                        page = (Page)await browser.NewPageAsync();
                    }
                    catch (Exception ex)
                    {
                        LogError(ex, "[Puppeteer] Failed to create a new page for accId={AccId}", accId);
                        return;
                    }
                }

                await page.GoToAsync("https://web.whatsapp.com/");
                LogInfo("[Puppeteer] Navigated to WhatsApp web");
                await page.WaitForSelectorAsync("div[class*=qr]");
                LogInfo("[Puppeteer] QR found");
                var screenshotDir = Path.Combine("C:\\", ".ADSPOWER_GLOBAL", "RPA", "screenshot");
                try
                {
                    Directory.CreateDirectory(screenshotDir);
                }
                catch (Exception ex)
                {
                    LogWarn(ex, "[Puppeteer] Failed to create screenshot directory {Dir}", screenshotDir);
                }
                var screenshotPath = Path.Combine(screenshotDir, accId + ".png");
                await page.ScreenshotAsync(screenshotPath);
                LogInfo("[Puppeteer] Screenshot saved: {Path}", screenshotPath);
            }
            catch (Exception ex)
            {
                LogError(ex, "[Puppeteer] Max login failed for accId={AccId}", accId);
            }
        }

        // Helper logging methods: produce a single-line message prefixed with local time
        private string FormatLine(string message, object[] args)
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.Append(' ');
            sb.Append(message);
            if (args != null && args.Length > 0)
            {
                sb.Append(' ');
                sb.Append(string.Join(' ', args.Select(a => a == null ? "null" : a.ToString())));
            }
            return sb.ToString();
        }

        private void LogInfo(string message, params object[] args) => _logger.LogInformation(FormatLine(message, args));
        private void LogWarn(string message, params object[] args) => _logger.LogWarning(FormatLine(message, args));
        private void LogWarn(Exception ex, string message, params object[] args) => _logger.LogWarning(FormatLine(message + " Error: " + (ex?.Message ?? string.Empty), args));
        private void LogError(string message, params object[] args) => _logger.LogError(FormatLine(message, args));
        private void LogError(Exception ex, string message, params object[] args) => _logger.LogError(FormatLine(message + " Error: " + (ex?.Message ?? string.Empty), args));
    }
}
