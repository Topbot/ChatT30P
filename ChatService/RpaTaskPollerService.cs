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
            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                WriteError("chatConnectionString is empty.");
                return;
            }

            EnsureRpaTasksTable();

            var count = GetPendingTasksCount();
            WriteInfo($"Pending RPA tasks: {count}");
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
        }
    }
}
