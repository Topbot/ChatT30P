using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;

namespace ChatService
{
    public sealed class RpaTaskPollerService : ServiceBase
    {
        private const string EventLogSource = "ChatService";

        private Timer _timer;
        private int _isRunning;

        public RpaTaskPollerService()
        {
            ServiceName = "ChatService";
            CanStop = true;
            AutoLog = true;

            TryEnsureEventSource();
        }

        protected override void OnStart(string[] args)
        {
            WriteInfo("Service starting...");
            var minutes = GetPollIntervalMinutes();
            _timer = new Timer(_ => TickSafe(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(minutes));
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
            }
        }

        private void Tick()
        {
            WriteInfo("Tick started");

            try
            {
                WriteInfo("Focusing AdsPower window...");
                WindowFocusService.FocusAdsPower();
                WriteInfo("AdsPower focus/maximize/click completed");
            }
            catch (Exception ex)
            {
                WriteError("Window focus action failed", ex);
            }

            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                WriteError("chatConnectionString is empty.");
                return;
            }

            EnsureRpaTasksTable();

            var count = GetPendingTasksCount();
            WriteInfo($"Pending RPA tasks: {count}");

            var adsPowerId = TryGetNextAdsPowerId();
            if (!string.IsNullOrWhiteSpace(adsPowerId))
            {
                try
                {
                    WriteInfo($"Filling AdsPower filter with ads_power_id='{adsPowerId}'");
                    WindowFocusService.SelectProfileIdAndFill(adsPowerId);
                    WriteInfo("UI automation completed");
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

        private string TryGetNextAdsPowerId()
        {
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT TOP (1) ads_power_id FROM dbo.rpa_tasks ORDER BY id";
                    cn.Open();
                    var obj = cmd.ExecuteScalar();
                    return obj == null || obj == DBNull.Value ? null : Convert.ToString(obj);
                }
            }
            catch (Exception ex)
            {
                WriteError("Failed to read ads_power_id", ex);
                return null;
            }
        }

        private int GetPendingTasksCount()
        {
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
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

        private static void TryEnsureEventSource()
        {
            try
            {
                if (!EventLog.SourceExists(EventLogSource))
                    EventLog.CreateEventSource(EventLogSource, "Application");
            }
            catch
            {
            }
        }

        private static void WriteInfo(string message)
        {
            try
            {
                EventLog.WriteEntry(EventLogSource, message, EventLogEntryType.Information);
            }
            catch
            {
            }

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
                var text = ex == null ? message : (message + Environment.NewLine + ex);
                EventLog.WriteEntry(EventLogSource, text, EventLogEntryType.Error);
            }
            catch
            {
            }

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
