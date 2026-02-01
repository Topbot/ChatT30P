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
using System.Text.Json;
using System.Collections.Concurrent;

namespace ChatService2
{
    public sealed class RpaTaskPollerService : BackgroundService
    {
        private readonly ILogger<RpaTaskPollerService> _logger;
        private const int DbCommandTimeoutSeconds = 60;
        private static DateTime _lastQueueAlertUtc = DateTime.MinValue;
        private int _pollIntervalMinutes;
        private readonly string? _connectionString;
        private const int MaxConcurrency = 3;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(MaxConcurrency);
        private readonly ConcurrentBag<Task> _workerTasks = new ConcurrentBag<Task>();

        public RpaTaskPollerService(ILogger<RpaTaskPollerService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _pollIntervalMinutes = GetPollIntervalMinutes();
            _connectionString = configuration["CHAT_CONNECTION_STRING"] ?? configuration.GetConnectionString("Chat");
            if (string.IsNullOrWhiteSpace(_connectionString))
                LogWarn("CHAT_CONNECTION_STRING was not provided via configuration or ConnectionStrings:Chat");
        }

        private async Task RunTelegramLoadChatsPuppeteerAsync(string? wsEndpoint, string accId)
        {
            LogInfo("[Puppeteer] Load Telegram chats for accId={AccId}", accId);
            try
            {
                if (string.IsNullOrWhiteSpace(wsEndpoint))
                {
                    LogWarn("[Puppeteer] wsEndpoint is empty for accId={AccId}", accId);
                    return;
                }

                var browser = await Puppeteer.ConnectAsync(new ConnectOptions { BrowserWSEndpoint = wsEndpoint });
                Page? page = null;
                try
                {
                    var pages = await browser.PagesAsync();
                    if (pages != null && pages.Length > 0)
                        page = (Page)pages[0];
                }
                catch (Exception ex)
                {
                    LogWarn(ex, "[Puppeteer] browser.PagesAsync() failed, will try NewPageAsync");
                }

                if (page == null)
                {
                    try { page = (Page)await browser.NewPageAsync(); }
                    catch (Exception ex) { LogError(ex, "[Puppeteer] Failed to create new page for accId={AccId}", accId); return; }
                }

                await page.GoToAsync("https://web.telegram.org/a/", new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } });
                LogInfo("[Puppeteer] Navigated to Telegram web for accId={AccId}", accId);

                // Wait for the left navigation/chat list
                var chatListSelector = "div[role='navigation']";
                await page.WaitForSelectorAsync(chatListSelector);

                // Execute JS in page to collect chat titles and previews by scrolling
                var result = await page.EvaluateFunctionAsync<string>(@"async (chatListSelector) => {
    const collected = {};
    const el = document.querySelector(chatListSelector);
    if (!el) return JSON.stringify(collected);
    const itemsSelector = chatListSelector + ' [role=""listitem""]';
    const sleep = (ms) => new Promise(r => setTimeout(r, ms));
    for (let i = 0; i < 40; i++) {
        const chats = Array.from(document.querySelectorAll(itemsSelector));
        for (const chat of chats) {
            try {
                const titleEl = chat.querySelector('div[dir=""auto""]');
                const title = titleEl ? titleEl.innerText.trim() : null;
                if (!title) continue;
                const previewEl = chat.querySelector('span');
                const preview = previewEl ? previewEl.innerText.trim() : '';
                if (!(title in collected)) collected[title] = preview;
            } catch(e) {}
        }
        el.scrollBy(0, 300);
        await sleep(400 + Math.random()*300);
    }
    return JSON.stringify(collected);
}", chatListSelector);

                if (string.IsNullOrWhiteSpace(result))
                {
                    LogWarn("[Puppeteer] No chats collected for accId={AccId}", accId);
                    return;
                }

                // Save collected JSON into accounts.chats_json for this ads_power_id
                try
                {
                    using var cn = new SqlConnection(_connectionString!);
                    using var cmd = cn.CreateCommand();
                    cmd.CommandTimeout = DbCommandTimeoutSeconds;
                    cmd.CommandText = @"UPDATE accounts SET chats_json = @chats WHERE ads_power_id = @ads_power_id";
                    cmd.Parameters.Add("@chats", SqlDbType.NVarChar).Value = result;
                    cmd.Parameters.Add("@ads_power_id", SqlDbType.NVarChar, 128).Value = accId;
                    cn.Open();
                    var affected = cmd.ExecuteNonQuery();
                    LogInfo("[Puppeteer] Saved chats for accId={AccId}, rowsAffected={Count}", accId, affected);
                }
                catch (Exception ex)
                {
                    LogError(ex, "[Puppeteer] Failed to save chats for accId={AccId}", accId);
                }
            }
            catch (Exception ex)
            {
                LogError(ex, "[Puppeteer] Load chats failed for accId={AccId}", accId);
            }
        }

        private async Task RunWhatsappSubmitCodePuppeteerAsync(string? wsEndpoint, string accId, string? code)
        {
            LogInfo("[Puppeteer] Submit WhatsApp login code for accId={AccId}", accId);
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
                    var pages = await browser.PagesAsync();
                    if (pages != null && pages.Length > 0)
                        page = (Page)pages[0];
                }
                catch (Exception ex)
                {
                    LogWarn(ex, "[Puppeteer] browser.PagesAsync() failed, will try NewPageAsync");
                }

                if (page == null)
                {
                    try { page = (Page)await browser.NewPageAsync(); }
                    catch (Exception ex) { LogError(ex, "[Puppeteer] Failed to create new page for accId={AccId}", accId); return; }
                }

                try
                {
                    // WhatsApp code input selector may vary; commonly it's input[aria-label] or input[type=tel] when entering phone,
                    // for code entry we look for input[id='sign-in-code'] or input[autocomplete='one-time-code']
                    await page.WaitForSelectorAsync("input[id='sign-in-code'], input[autocomplete='one-time-code']", new WaitForSelectorOptions { Timeout = 10000 });
                    // clear and type
                    await page.EvaluateFunctionAsync(@"selector => {
                        var el = document.querySelector(selector);
                        if (el) { el.focus(); el.value=''; el.dispatchEvent(new Event('input',{bubbles:true})); }
                    }", "input[id='sign-in-code'], input[autocomplete='one-time-code']");
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        // type into the first matching element
                        await page.TypeAsync("input[id='sign-in-code'], input[autocomplete='one-time-code']", code);
                    }
                    await page.ClickAsync("button[type='submit']");
                    LogInfo("[Puppeteer] WhatsApp code submitted for accId={AccId}", accId);
                }
                catch (Exception ex)
                {
                    LogError(ex, "[Puppeteer] Failed to submit WhatsApp code for accId={AccId}", accId);
                }
            }
            catch (Exception ex)
            {
                LogError(ex, "[Puppeteer] WhatsApp submit code failed for accId={AccId}", accId);
            }
        }

        private async Task RunTelegramSubmitCodePuppeteerAsync(string? wsEndpoint, string accId, string? code)
        {
            LogInfo("[Puppeteer] Submit Telegram login code for accId={AccId}", accId);
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
                    var pages = await browser.PagesAsync();
                    if (pages != null && pages.Length > 0)
                        page = (Page)pages[0];
                }
                catch (Exception ex)
                {
                    LogWarn(ex, "[Puppeteer] browser.PagesAsync() failed, will try NewPageAsync");
                }

                if (page == null)
                {
                    try { page = (Page)await browser.NewPageAsync(); }
                    catch (Exception ex) { LogError(ex, "[Puppeteer] Failed to create new page for accId={AccId}", accId); return; }
                }

                // Wait for code input and submit
                try
                {
                    await page.WaitForSelectorAsync("input[id='sign-in-code']", new WaitForSelectorOptions { Timeout = 10000 });
                    // clear via DOM and type code
                    await page.EvaluateFunctionAsync(@"selector => {
                        var el = document.querySelector(selector);
                        if (el) { el.focus(); el.value=''; el.dispatchEvent(new Event('input',{bubbles:true})); }
                    }", "input[id='sign-in-code']");
                    if (!string.IsNullOrWhiteSpace(code))
                        await page.TypeAsync("input[id='sign-in-code']", code);
                    await page.ClickAsync("button[type='submit']");
                    LogInfo("[Puppeteer] Code submitted for accId={AccId}", accId);
                }
                catch (Exception ex)
                {
                    LogError(ex, "[Puppeteer] Failed to submit code for accId={AccId}", accId);
                }
            }
            catch (Exception ex)
            {
                LogError(ex, "[Puppeteer] Telegram submit code failed for accId={AccId}", accId);
            }
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

            try
            {
                var cleaned = DeleteStalePendingTasks();
                if (cleaned > 0)
                    LogInfo("Cleaned stale pending rpa_tasks: {Count}", cleaned);
            }
            catch (Exception ex)
            {
                LogWarn(ex, "DeleteStalePendingTasks failed");
            }

            var count = GetPendingTasksCount();
            LogInfo("Pending RPA tasks: {Count}", count);
            if (count <= 0)
            {
                LogInfo("No pending tasks; skipping.");
                return;
            }

                var task = TryClaimNextTask();
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

                // Start background worker to process task without blocking tick.
                var worker = Task.Run(async () =>
                {
                    await _semaphore.WaitAsync();
                    try
                    {
                        await ProcessTaskAsync(task);
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                });
                _workerTasks.Add(worker);
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

        // Atomically claim next pending task by setting status = 'processing' and returning the row
        private RpaTask? TryClaimNextTask()
        {
            try
            {
                using var cn = new SqlConnection(_connectionString!);
                using var cmd = cn.CreateCommand();
                cmd.CommandTimeout = DbCommandTimeoutSeconds;

                // Use MERGE/OUTPUT or update+select pattern. Simpler: update top(1) with OUTPUT
                cmd.CommandText = @"
DECLARE @id INT;
;WITH cte AS (
    SELECT TOP (1) id FROM dbo.rpa_tasks WHERE ISNULL(status,'pending') = 'pending' ORDER BY id
)
UPDATE dbo.rpa_tasks
SET status = 'processing'
OUTPUT inserted.id, inserted.ads_power_id, inserted.script_name, inserted.value, inserted.puppeteer
WHERE id IN (SELECT id FROM cte);
";

                cn.Open();
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                var task = new RpaTask
                {
                    Id = r[0] == DBNull.Value ? 0 : Convert.ToInt32(r[0]),
                    AdsPowerId = r[1] == DBNull.Value ? null : Convert.ToString(r[1]),
                    ScriptName = r[2] == DBNull.Value ? null : Convert.ToString(r[2]),
                    Value = r[3] == DBNull.Value ? null : Convert.ToString(r[3]),
                    Puppeteer = r.FieldCount > 4 && r[4] != DBNull.Value ? Convert.ToString(r[4]) : null
                };
                LogInfo("Claimed task {Id} {AdsPowerId}", task.Id, task.AdsPowerId);
                return task;
            }
            catch (Exception ex)
            {
                LogError(ex, "Failed to claim next rpa_task");
                return null;
            }
        }

        private async Task ProcessTaskAsync(RpaTask task)
        {
            try
            {
                var wsEndpoint = string.IsNullOrWhiteSpace(task.Puppeteer) ? task.Value : task.Puppeteer;
                var scriptNorm = (task.ScriptName ?? string.Empty).Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

                // Load chats for Telegram
                if (scriptNorm.Contains("loadchats") && scriptNorm.Contains("telegram") || scriptNorm == "loadchatstelegram")
                {
                    try { await RunTelegramLoadChatsPuppeteerAsync(wsEndpoint, task.AdsPowerId); }
                    catch (Exception ex) { LogError(ex, "LoadChatsTelegram script failed for task {Id}", task.Id); }
                }

                // Submit code task (e.g. SubmitLoginTelegram)
                if ((scriptNorm.Contains("telegram") && (scriptNorm.Contains("submit") || scriptNorm.Contains("code")))
                    || scriptNorm == "submitlogintelegram" || scriptNorm == "telegramsumbitcode" || scriptNorm == "telegramsubmitcode")
                {
                    try { await RunTelegramSubmitCodePuppeteerAsync(wsEndpoint, task.AdsPowerId, task.Value); }
                    catch (Exception ex) { LogError(ex, "TelegramSubmitCode script failed for task {Id}", task.Id); }
                }
                else if ((scriptNorm.Contains("whatsapp") && (scriptNorm.Contains("submit") || scriptNorm.Contains("code")))
                         || scriptNorm == "submitloginwhatsapp" || scriptNorm == "whatsappsubmitcode" || scriptNorm == "whatsappsubmitcode")
                {
                    try { await RunWhatsappSubmitCodePuppeteerAsync(wsEndpoint, task.AdsPowerId, task.Value); }
                    catch (Exception ex) { LogError(ex, "WhatsappSubmitCode script failed for task {Id}", task.Id); }
                }
                else if (scriptNorm.Contains("telegram") && scriptNorm.Contains("start") && scriptNorm.Contains("login")
                    || scriptNorm == "telegramstartlogin" || scriptNorm == "startlogintelegram")
                {
                    try { await RunTelegramStartLoginPuppeteerAsync(wsEndpoint, task.AdsPowerId, task.Value); }
                    catch (Exception ex) { LogError(ex, "TelegramStartLogin script failed for task {Id}", task.Id); }
                }
                else if (scriptNorm.Contains("whatsapp") && scriptNorm.Contains("start") && scriptNorm.Contains("login")
                         || scriptNorm == "whatsappstartlogin" || scriptNorm == "startloginwhatsapp")
                {
                    try { await RunWhatsappStartLoginPuppeteerAsync(wsEndpoint, task.AdsPowerId, task.Value); }
                    catch (Exception ex) { LogError(ex, "WhatsappStartLogin script failed for task {Id}", task.Id); }
                }
                else if (scriptNorm.Contains("max") && scriptNorm.Contains("start") && scriptNorm.Contains("login")
                         || scriptNorm == "maxstartlogin" || scriptNorm == "startloginmax")
                {
                    try { await RunMaxStartLoginPuppeteerAsync(wsEndpoint, task.AdsPowerId); }
                    catch (Exception ex) { LogError(ex, "MaxStartLogin script failed for task {Id}", task.Id); }
                }
                else
                {
                    LogError("Unrecognized script_name: {ScriptName}", task.ScriptName);
                }

                // On success remove the task
                var deleted = TryDeleteTask(task.Id);
                LogInfo(deleted ? "Task deleted" : "Task NOT deleted");
            }
            catch (Exception ex)
            {
                LogError(ex, "Processing task failed id={Id}", task.Id);
                try
                {
                    // mark task as failed so it can be inspected
                    using var cn = new SqlConnection(_connectionString!);
                    using var cmd = cn.CreateCommand();
                    cmd.CommandTimeout = DbCommandTimeoutSeconds;
                    cmd.CommandText = "UPDATE dbo.rpa_tasks SET status = 'failed' WHERE id = @id";
                    cmd.Parameters.Add("@id", SqlDbType.Int).Value = task.Id;
                    cn.Open();
                    cmd.ExecuteNonQuery();
                }
                catch (Exception inner)
                {
                    LogWarn(inner, "Failed to mark task failed id={Id}", task.Id);
                }
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

        private int DeleteStalePendingTasks()
        {
            // remove tasks with status pending (or NULL) older than 1 hour
            using var cn = new SqlConnection(_connectionString!);
            using var cmd = cn.CreateCommand();
            cmd.CommandTimeout = DbCommandTimeoutSeconds;
            cmd.CommandText = @"
IF COL_LENGTH('dbo.rpa_tasks','status') IS NULL
BEGIN
    -- No status column: delete tasks older than 1 hour
    DELETE FROM dbo.rpa_tasks
    WHERE created < DATEADD(hour, -1, GETUTCDATE());
END
ELSE
BEGIN
    -- Remove tasks that are stuck in pending OR processing for more than 1 hour
    DELETE FROM dbo.rpa_tasks
    WHERE ISNULL(status,'pending') IN ('pending','processing')
      AND created < DATEADD(hour, -1, GETUTCDATE());
END";
            cn.Open();
            return cmd.ExecuteNonQuery();
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
END
ELSE IF COL_LENGTH('dbo.rpa_tasks','status') IS NULL
BEGIN
    ALTER TABLE dbo.rpa_tasks ADD status NVARCHAR(32) NULL DEFAULT('pending');
END

-- Ensure uniqueness: one pending/processing rpa task per ads_power_id + script_name
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name = 'IX_rpa_tasks_ads_power_script' AND object_id = OBJECT_ID('dbo.rpa_tasks'))
BEGIN
    CREATE UNIQUE INDEX IX_rpa_tasks_ads_power_script ON dbo.rpa_tasks(ads_power_id, script_name);
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
