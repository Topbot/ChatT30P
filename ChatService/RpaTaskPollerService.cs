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
                WriteInfo("No pending tasks; skipping AdsPower focus.");
                WriteInfo("Tick finished");
                return;
            }

            // Pre-check: if SunBrowser is open, do not proceed unless queue is too large.
            var sunBrowserOpen = WindowFocusService.HasOpenSunBrowserWindow();
            if (sunBrowserOpen)
            {
                if (count >= 100)
                {
                    WriteError($"Queue is too large: {count}. Closing SunBrowser window...");
                    var closed = WindowFocusService.CloseSunBrowserWindows();
                    WriteInfo($"SunBrowser windows close requested: {closed}");
                    TrySendQueueAlertOncePerDay(count);
                }
                else
                {
                    WriteInfo($"SunBrowser is open and queue size is {count} (<100). Skipping tick.");
                    return;
                }
            }

            try
            {
                WriteInfo("Focusing AdsPower window...");
                WindowFocusService.FocusAdsPower();
                WriteInfo("AdsPower focus/maximize/click completed");

                // Best-effort: ensure UIA can see the window
                AdsPowerAutomation.FocusMainWindow();
            }
            catch (Exception ex)
            {
                WriteError("Window focus action failed", ex);
            }

            var task = TryGetNextTask();
            if (task != null && !string.IsNullOrWhiteSpace(task.AdsPowerId))
            {
                try
                {
                    WriteInfo($"Filling AdsPower filter with ads_power_id='{task.AdsPowerId}', script_name='{task.ScriptName}', task_id={task.Id}");
                    // Legacy coordinate-based flow (filter + RPA)
                    WindowFocusService.SelectProfileIdAndFill(task.AdsPowerId, task.ScriptName);

                    // UIAutomation assist: if RPA dialog is open, try to fill + OK using UIA
                    try
                    {
                        AdsPowerAutomation.TryFillScriptNameAndOk(task.ScriptName);
                    }
                    catch
                    {
                    }
                    WriteInfo("UI automation completed; waiting 1 minute before confirmation...");
                    Thread.Sleep(TimeSpan.FromMinutes(1));

                    var hasSunBrowser = WindowFocusService.HasOpenSunBrowserWindow();
                    if (hasSunBrowser)
                    {
                        WriteInfo("Detected '* - SunBrowser' window; deleting task from DB...");
                        var deleted = TryDeleteTask(task.Id);
                        WriteInfo(deleted
                            ? $"Task deleted from DB (id={task.Id})"
                            : $"Task was NOT deleted (id={task.Id})");
                    }
                    else
                    {
                        WriteInfo("No '* - SunBrowser' window detected after 1 minute; clicking Cancel and skipping DB deletion.");
                        try
                        {
                            WindowFocusService.ClickRpaDialogCancel();
                        }
                        catch (Exception ex)
                        {
                            WriteError("Failed to click Cancel in RPA dialog", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteError("UI automation failed", ex);
                }
            }
            else
            {
                WriteInfo("No ads_power_id found to fill");
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
        }

        private RpaTask TryGetNextTask()
        {
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandTimeout = DbCommandTimeoutSeconds;
                    cmd.CommandText = @"SELECT TOP (1) id, ads_power_id, script_name FROM dbo.rpa_tasks ORDER BY id";
                    cn.Open();
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read())
                            return null;

                        return new RpaTask
                        {
                            Id = r[0] == DBNull.Value ? 0 : Convert.ToInt32(r[0]),
                            AdsPowerId = r[1] == DBNull.Value ? null : Convert.ToString(r[1]),
                            ScriptName = r[2] == DBNull.Value ? null : Convert.ToString(r[2])
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
    }
}
