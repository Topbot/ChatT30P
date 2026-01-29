using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Net.Mail;
using System.ServiceProcess;
using System.Threading;

namespace ChatService
{
    public sealed class RpaTaskPollerService : ServiceBase
    {
        private const int DbCommandTimeoutSeconds = 60;
        private static DateTime _lastQueueAlertUtc = DateTime.MinValue;

        private Timer _timer;
        private int _isRunning;
        private int _pollIntervalMinutes;

        public RpaTaskPollerService()
        {
            ServiceName = "ChatService";
            CanStop = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            WriteInfo("Service starting...");
            _pollIntervalMinutes = GetPollIntervalMinutes();
            // One-shot timer: re-scheduled after each completed tick.
            _timer = new Timer(_ => TickSafe(), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }

        protected override void OnStop()
        {
            WriteInfo("Service stopping...");
            _timer?.Dispose();
            _timer = null;
        }

        internal void DebugStart() => OnStart(new string[0]);
        internal void DebugStop() => OnStop();

        private static int GetPollIntervalMinutes()
        {
            var s = ConfigurationManager.AppSettings["PollIntervalMinutes"];
            if (int.TryParse(s, out var minutes) && minutes > 0 && minutes <= 1440)
                return minutes;
            return 10;
        }

        private static string ConnectionString => ConfigurationManager.ConnectionStrings["chatConnectionString"]?.ConnectionString;

        private void TickSafe()
        {
            if (Interlocked.Exchange(ref _isRunning, 1) == 1)
                return;

            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                WriteError("Tick failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _isRunning, 0);

                try
                {
                    _timer?.Change(TimeSpan.FromMinutes(_pollIntervalMinutes), Timeout.InfiniteTimeSpan);
                }
                catch
                {
                }
            }
        }

        private void Tick()
        {
            WriteInfo("Tick started");

            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                WriteError("chatConnectionString is empty.");
                return;
            }

            EnsureRpaTasksTable();
            var count = GetPendingTasksCount();
            WriteInfo($"Pending RPA tasks: {count}");

            if (count <= 0)
            {
                WriteInfo("No pending tasks; skipping.");
                WriteInfo("Tick finished");
                return;
            }

            var task = TryGetNextTask();
            if (task != null && !string.IsNullOrWhiteSpace(task.AdsPowerId))
            {
                try
                {
                    WriteInfo($"Processing RPA task: ads_power_id='{task.AdsPowerId}', script_name='{task.ScriptName}', task_id={task.Id}");

                    if (string.Equals(task.ScriptName, "TelegramStartLogin", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            RunTelegramStartLogin(task.AdsPowerId, task.Value); // userName = Value, accId = AdsPowerId
                        }
                        catch (Exception ex)
                        {
                            WriteError("TelegramStartLogin script failed", ex);
                        }
                    }
                    else if (string.Equals(task.ScriptName, "WhatsappStartLogin", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            RunWhatsappStartLogin(task.AdsPowerId, task.Value); // userName = Value, accId = AdsPowerId
                        }
                        catch (Exception ex)
                        {
                            WriteError("WhatsappStartLogin script failed", ex);
                        }
                    }
                    else if (string.Equals(task.ScriptName, "MaxStartLogin", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            RunMaxStartLogin(task.AdsPowerId); // accId = AdsPowerId, no username needed
                        }
                        catch (Exception ex)
                        {
                            WriteError("MaxStartLogin script failed", ex);
                        }
                    }
                    // Здесь может быть не-UI логика обработки других задач

                    var deleted = TryDeleteTask(task.Id);
                    WriteInfo(deleted
                        ? $"Task deleted from DB (id={task.Id})"
                        : $"Task was NOT deleted (id={task.Id})");
                }
                catch (Exception ex)
                {
                    WriteError("Task processing failed", ex);
                }
            }
            else
            {
                WriteInfo("No ads_power_id found to process");
            }

            WriteInfo("Tick finished");
        }

        private static void TrySendQueueAlertOncePerDay(int pending)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                if (_lastQueueAlertUtc != DateTime.MinValue && (nowUtc - _lastQueueAlertUtc) < TimeSpan.FromDays(1))
                    return;

                _lastQueueAlertUtc = nowUtc;

                using (var msg = new MailMessage())
                {
                    msg.To.Add("topbot@yandex.ru");
                    msg.Subject = "ChatService: очередь RPA задач слишком большая";
                    msg.Body = "Очередь задач rpa_tasks >= 100 (текущее значение: " + pending + "). Проверьте сервер.";

                    using (var client = new SmtpClient("localhost"))
                    {
                        client.DeliveryMethod = SmtpDeliveryMethod.Network;
                        client.Send(msg);
                    }
                }

                WriteInfo("Daily queue alert email sent.");
            }
            catch (Exception ex)
            {
                WriteError("Failed to send queue alert email", ex);
            }
        }

        private sealed class RpaTask
        {
            public int Id { get; set; }
            public string AdsPowerId { get; set; }
            public string ScriptName { get; set; }
            public string Value { get; set; } // параметр для скрипта (телефон или код)
        }

        private RpaTask TryGetNextTask()
        {
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandTimeout = DbCommandTimeoutSeconds;
                    cmd.CommandText = @"SELECT TOP (1) id, ads_power_id, script_name, value FROM dbo.rpa_tasks ORDER BY id";
                    cn.Open();
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read())
                            return null;

                        return new RpaTask
                        {
                            Id = r[0] == DBNull.Value ? 0 : Convert.ToInt32(r[0]),
                            AdsPowerId = r[1] == DBNull.Value ? null : Convert.ToString(r[1]),
                            ScriptName = r[2] == DBNull.Value ? null : Convert.ToString(r[2]),
                            Value = r[3] == DBNull.Value ? null : Convert.ToString(r[3])
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError("Failed to read next rpa_task", ex);
                return null;
            }
        }

        private bool TryDeleteTask(int id)
        {
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandTimeout = DbCommandTimeoutSeconds;
                    cmd.CommandText = @"DELETE FROM dbo.rpa_tasks WHERE id = @id";
                    cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
                    cn.Open();
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception ex)
            {
                WriteError($"Failed to delete rpa_task id={id}", ex);
                return false;
            }
        }

        private int GetPendingTasksCount()
        {
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandTimeout = DbCommandTimeoutSeconds;
                cmd.CommandText = "SELECT COUNT(1) FROM dbo.rpa_tasks";
                cn.Open();
                var obj = cmd.ExecuteScalar();
                return obj == null || obj == DBNull.Value ? 0 : Convert.ToInt32(obj);
            }
        }

        private void EnsureRpaTasksTable()
        {
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandTimeout = DbCommandTimeoutSeconds;
                cmd.CommandText = @"
IF OBJECT_ID('dbo.rpa_tasks', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.rpa_tasks(
        id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ads_power_id NVARCHAR(128) NOT NULL,
        script_name NVARCHAR(128) NOT NULL,
        value NVARCHAR(128) NULL,
        created DATETIME NOT NULL DEFAULT(GETDATE())
    );
    CREATE INDEX IX_rpa_tasks_ads_power_id ON dbo.rpa_tasks(ads_power_id);
    CREATE INDEX IX_rpa_tasks_created ON dbo.rpa_tasks(created);
END";
                cn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private static void WriteInfo(string message)
        {
            try
            {
                if (Environment.UserInteractive)
                {
                    Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + message);
                    Trace.WriteLine(message);
                }
            }
            catch
            {
            }
        }

        private static void WriteError(string message, Exception ex = null)
        {
            try
            {
                if (Environment.UserInteractive)
                {
                    var text = ex == null ? message : (message + Environment.NewLine + ex);
                    Console.Error.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + text);
                    Trace.WriteLine(text);
                }
            }
            catch
            {
            }
        }

#if !NO_SELENIUM
        private void RunTelegramStartLogin(string accId, string userName)
        {
            // Requires Selenium.WebDriver, Selenium.WebDriver.ChromeDriver, Selenium.Support
            var options = new OpenQA.Selenium.Chrome.ChromeOptions();
            options.AddArgument("--headless");
            options.AddArgument("--disable-gpu");
            options.AddArgument("--window-size=1280,1024");
            using (var driver = new OpenQA.Selenium.Chrome.ChromeDriver(options))
            {
                driver.Navigate().GoToUrl("https://web.telegram.org/a/");
                driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);

                var wait = new OpenQA.Selenium.Support.UI.WebDriverWait(driver, TimeSpan.FromSeconds(5));
                wait.Until(OpenQA.Selenium.Support.UI.ExpectedConditions.ElementIsVisible(OpenQA.Selenium.By.CssSelector("div[class=\"qr-container\"] image")));

                var screenshot = ((OpenQA.Selenium.ITakesScreenshot)driver).GetScreenshot();
                string screenshotPath = $"{accId}.png";
                screenshot.SaveAsFile(screenshotPath, OpenQA.Selenium.ScreenshotImageFormat.Png);

                var authButton = driver.FindElement(OpenQA.Selenium.By.CssSelector("button[class*=\"auth-button\"]"));
                authButton.Click();

                wait = new OpenQA.Selenium.Support.UI.WebDriverWait(driver, TimeSpan.FromSeconds(30));
                wait.Until(OpenQA.Selenium.Support.UI.ExpectedConditions.ElementIsVisible(OpenQA.Selenium.By.CssSelector("input[id=\"sign-in-phone-number\"]")));

                var phoneInput = driver.FindElement(OpenQA.Selenium.By.CssSelector("input[id=\"sign-in-phone-number\"]"));
                phoneInput.SendKeys(userName);

                System.Threading.Thread.Sleep(100);

                var submitButton = driver.FindElement(OpenQA.Selenium.By.CssSelector("button[type=\"submit\"]"));
                submitButton.Click();

                System.Threading.Thread.Sleep(240000); // 4 минуты
            }
        }

        private void RunWhatsappStartLogin(string accId, string userName)
        {
            var options = new OpenQA.Selenium.Chrome.ChromeOptions();
            options.AddArgument("--headless");
            options.AddArgument("--disable-gpu");
            options.AddArgument("--window-size=1280,1024");
            using (var driver = new OpenQA.Selenium.Chrome.ChromeDriver(options))
            {
                driver.Navigate().GoToUrl("https://web.whatsapp.com/");
                driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);

                var wait = new OpenQA.Selenium.Support.UI.WebDriverWait(driver, TimeSpan.FromSeconds(5));
                wait.Until(OpenQA.Selenium.Support.UI.ExpectedConditions.ElementIsVisible(OpenQA.Selenium.By.XPath("//div[text()=\"Log in with phone number\"]")));

                var screenshot = ((OpenQA.Selenium.ITakesScreenshot)driver).GetScreenshot();
                string screenshotPath = $"{accId}.png";
                screenshot.SaveAsFile(screenshotPath, OpenQA.Selenium.ScreenshotImageFormat.Png);

                var phoneLoginButton = driver.FindElement(OpenQA.Selenium.By.XPath("//div[text()=\"Log in with phone number\"]"));
                phoneLoginButton.Click();

                wait = new OpenQA.Selenium.Support.UI.WebDriverWait(driver, TimeSpan.FromSeconds(30));
                wait.Until(OpenQA.Selenium.Support.UI.ExpectedConditions.ElementIsVisible(OpenQA.Selenium.By.CssSelector("input[aria-label]")));

                var phoneInput = driver.FindElement(OpenQA.Selenium.By.CssSelector("input[aria-label]"));
                phoneInput.SendKeys("+" + userName);

                System.Threading.Thread.Sleep(100);

                var nextButton = driver.FindElement(OpenQA.Selenium.By.XPath("//div[text()=\"Next\"]"));
                nextButton.Click();

                System.Threading.Thread.Sleep(240000); // 4 минуты
            }
        }

        private void RunMaxStartLogin(string accId)
        {
            var options = new OpenQA.Selenium.Chrome.ChromeOptions();
            options.AddArgument("--headless");
            options.AddArgument("--disable-gpu");
            options.AddArgument("--window-size=1280,1024");
            using (var driver = new OpenQA.Selenium.Chrome.ChromeDriver(options))
            {
                driver.Navigate().GoToUrl("https://web.whatsapp.com/");
                driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);

                var wait = new OpenQA.Selenium.Support.UI.WebDriverWait(driver, TimeSpan.FromSeconds(5));
                wait.Until(OpenQA.Selenium.Support.UI.ExpectedConditions.ElementIsVisible(OpenQA.Selenium.By.CssSelector("div[class*=\"qr\"]")));

                var screenshot = ((OpenQA.Selenium.ITakesScreenshot)driver).GetScreenshot();
                string screenshotPath = $"{accId}.png";
                screenshot.SaveAsFile(screenshotPath, OpenQA.Selenium.ScreenshotImageFormat.Png);

                System.Threading.Thread.Sleep(240000); // 4 минуты
            }
        }
#endif
    }
}
