using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.IO;
using System.Web;
using System.Web.Http;
using System.Linq;
using ChatT30P.Controllers.Models;
using Core;
using Newtonsoft.Json;

namespace ChatT30P.Controllers.Api
{
    public class ChatAccountsController : ApiController
    {
        private static string ConnectionString => ConfigurationManager.ConnectionStrings["chatConnectionString"]?.ConnectionString;

        private static string AdsPowerBaseUrl => ConfigurationManager.AppSettings["AdsPower:BaseUrl"];
        private static string AdsPowerToken => ConfigurationManager.AppSettings["AdsPower:Token"];
        private static string AdsPowerProxyId => ConfigurationManager.AppSettings["AdsPower:ProxyId"];
        private const string AdsPowerGroupName = "CHAT";

        private const string EventLogSource = "ChatT30P";

        private static void LogAdsPowerError(string message, Exception ex = null)
        {
            try
            {
                if (!EventLog.SourceExists(EventLogSource))
                    EventLog.CreateEventSource(EventLogSource, "Application");

                var text = ex == null ? message : (message + Environment.NewLine + ex);
                EventLog.WriteEntry(EventLogSource, text, EventLogEntryType.Error);
            }
            catch
            {
                try
                {
                    var baseDir = HttpContext.Current?.Server?.MapPath("~/App_Data/logs") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "logs");
                    Directory.CreateDirectory(baseDir);
                    var path = Path.Combine(baseDir, "chataccounts-errors.log");
                    var text = ex == null ? message : (message + Environment.NewLine + ex);
                    File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + text + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // ignore logging failures
                }
            }
        }

        [HttpGet]
        [Route("api/ChatAccounts")]
        public IEnumerable<ChatAccountItem> Get()
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                throw new HttpResponseException(HttpStatusCode.InternalServerError);
            }

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
            {
                throw new HttpResponseException(HttpStatusCode.Unauthorized);
            }

            var result = new List<ChatAccountItem>();

            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                // `created` column is optional; if it doesn't exist the query will fail.
                // If your schema doesn't have it, remove it from the SELECT and ORDER BY.
                cmd.CommandText = @"
SELECT user_id, platform, phone, status, chats_json,
       ads_power_id,
       TRY_CONVERT(datetime, [created]) AS created
FROM accounts
WHERE user_id = @user_id
ORDER BY TRY_CONVERT(datetime, [created]) DESC";

                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;

                cn.Open();
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        result.Add(new ChatAccountItem
                        {
                            UserId = r[0] as string,
                            Platform = r[1] as string,
                            Phone = r[2] as string,
                            Status = r[3] == DBNull.Value ? 0 : Convert.ToInt32(r[3]),
                            ChatsJson = r[4] as string,
                            AdsPowerId = r[5] as string,
                            Created = r[6] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r[6])
                        });
                    }
                }
            }

            return result;
        }

        [HttpPost]
        [Route("api/ChatAccounts")]
        public async Task<HttpResponseMessage> Post(ChatAccountItem item)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ConnectionString))
                {
                    LogAdsPowerError("ChatAccounts POST failed: chatConnectionString is missing.");
                    return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "На сервере случилась критическая ошибка, обратитесь к администратору.");
                }

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
            {
                return Request.CreateResponse(HttpStatusCode.Unauthorized);
            }

            if (!Security.IsPaid)
            {
                return Request.CreateResponse((HttpStatusCode)402, "No active subscription");
            }

            if (item == null || string.IsNullOrWhiteSpace(item.Platform) || string.IsNullOrWhiteSpace(item.Phone))
            {
                return Request.CreateResponse(HttpStatusCode.BadRequest);
            }

            // Prevent AdsPower calls if the account already exists
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT TOP 1 1
FROM accounts
WHERE user_id = @user_id AND platform = @platform AND phone = @phone";

                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = item.Platform.Trim();
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = item.Phone.Trim();

                cn.Open();
                var exists = cmd.ExecuteScalar() != null;
                if (exists)
                    return Request.CreateErrorResponse(HttpStatusCode.Conflict, "Указанный номер телефона уже в базе");
            }

            // Create AdsPower profile first. If AdsPower returns an error, do not write to DB.
            string adsPowerId;
            if (!string.IsNullOrWhiteSpace(AdsPowerBaseUrl) && !string.IsNullOrWhiteSpace(AdsPowerToken))
            {
                var groupId = await EnsureAdsPowerGroupAsync(AdsPowerGroupName);
                adsPowerId = await CreateAdsPowerProfileAsync(groupId, item);
                if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(adsPowerId))
                    return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "На сервере случилась критическая ошибка, обратитесь к администратору.");
            }
            else
            {
                return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "На сервере случилась критическая ошибка, обратитесь к администратору.");
            }

            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                // Cleanup: remove stale rows without AdsPower profile ID (older than 1 hour)
                cmd.CommandText = @"
DELETE FROM accounts
WHERE ads_power_id IS NULL
  AND TRY_CONVERT(datetime, [created]) < DATEADD(hour, -1, GETDATE())";

                cn.Open();
                cmd.ExecuteNonQuery();

                cmd.CommandText = @"
INSERT INTO accounts(user_id, platform, phone, status, chats_json, ads_power_id, created)
VALUES(@user_id, @platform, @phone, @status, @chats_json, @ads_power_id, GETDATE())";

                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = item.Platform.Trim();
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = item.Phone.Trim();
                cmd.Parameters.Add("@status", SqlDbType.Int).Value = item.Status;
                cmd.Parameters.Add("@chats_json", SqlDbType.NVarChar).Value = (object)(item.ChatsJson ?? "[]") ?? DBNull.Value;
                cmd.Parameters.Add("@ads_power_id", SqlDbType.NVarChar, 128).Value = (object)adsPowerId ?? DBNull.Value;

                try
                {
                    cmd.ExecuteNonQuery();
                }
                catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
                {
                    // Unique constraint violation (duplicate key)
                    return Request.CreateErrorResponse(HttpStatusCode.Conflict, "Указанный номер телефона уже в базе");
                }
            }

                return Request.CreateResponse(HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                LogAdsPowerError("ChatAccounts POST failed with exception.", ex);
                return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "На сервере случилась критическая ошибка, обратитесь к администратору.\n\n" + ex);
            }
        }

        public class StartLoginRequest
        {
            public string Platform { get; set; }
            public string Phone { get; set; }
        }

        [HttpPost]
        [Route("api/ChatAccounts/StartLogin")]
        public async Task<HttpResponseMessage> StartLogin(StartLoginRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ConnectionString))
                {
                    LogAdsPowerError("ChatAccounts StartLogin failed: chatConnectionString is missing.");
                    return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "На сервере случилась критическая ошибка, обратитесь к администратору.");
                }

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            if (!Security.IsPaid)
                return Request.CreateResponse((HttpStatusCode)402, "No active subscription");

            if (request == null || string.IsNullOrWhiteSpace(request.Platform) || string.IsNullOrWhiteSpace(request.Phone))
                return Request.CreateResponse(HttpStatusCode.BadRequest);

            // Read current row
            string adsPowerId = null;
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT TOP 1 ads_power_id
FROM accounts
WHERE user_id = @user_id AND platform = @platform AND phone = @phone";

                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = request.Platform.Trim();
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = request.Phone.Trim();

                cn.Open();
                adsPowerId = cmd.ExecuteScalar() as string;
            }

            if (string.IsNullOrWhiteSpace(adsPowerId))
            {
                if (string.IsNullOrWhiteSpace(AdsPowerBaseUrl) || string.IsNullOrWhiteSpace(AdsPowerToken))
                {
                    LogAdsPowerError("AdsPower StartLogin failed: AdsPower is not configured.");
                    return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "На сервере случилась критическая ошибка, обратитесь к администратору.");
                }

                var groupId = await EnsureAdsPowerGroupAsync(AdsPowerGroupName);
                adsPowerId = await CreateAdsPowerProfileAsync(groupId, new ChatAccountItem { Platform = request.Platform, Phone = request.Phone });
                if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(adsPowerId))
                    return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "На сервере случилась критическая ошибка, обратитесь к администратору.");

                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE accounts
SET ads_power_id = @ads_power_id
WHERE user_id = @user_id AND platform = @platform AND phone = @phone AND ads_power_id IS NULL";

                    cmd.Parameters.Add("@ads_power_id", SqlDbType.NVarChar, 128).Value = adsPowerId;
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                    cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = request.Platform.Trim();
                    cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = request.Phone.Trim();

                    cn.Open();
                    cmd.ExecuteNonQuery();
                }
            }

            // Open AdsPower profile and run RPA script
            if (string.IsNullOrWhiteSpace(adsPowerId))
                return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "На сервере случилась критическая ошибка, обратитесь к администратору.");

            // Instead of calling AdsPower RPA directly, enqueue task into DB.
            EnsureRpaTasksTable();
            EnqueueRpaTask(adsPowerId, "StartLoginTelegram");

                return Request.CreateResponse(HttpStatusCode.OK, new { adsPowerId });
            }
            catch (Exception ex)
            {
                LogAdsPowerError("ChatAccounts StartLogin failed with exception.", ex);
                return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "На сервере случилась критическая ошибка, обратитесь к администратору.\n\n" + ex);
            }
        }

        private void EnsureRpaTasksTable()
        {
            if (string.IsNullOrWhiteSpace(ConnectionString)) return;
            try
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
END";
                    cn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                LogAdsPowerError("EnsureRpaTasksTable failed.", ex);
            }
        }

        private void EnqueueRpaTask(string adsPowerId, string scriptName)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString)) return;
            if (string.IsNullOrWhiteSpace(adsPowerId) || string.IsNullOrWhiteSpace(scriptName)) return;

            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO dbo.rpa_tasks(ads_power_id, script_name)
VALUES(@ads_power_id, @script_name)";

                    cmd.Parameters.Add("@ads_power_id", SqlDbType.NVarChar, 128).Value = adsPowerId;
                    cmd.Parameters.Add("@script_name", SqlDbType.NVarChar, 128).Value = scriptName;

                    cn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                LogAdsPowerError($"EnqueueRpaTask failed. adsPowerId={adsPowerId} script={scriptName}", ex);
            }
        }

        public class SubmitCodeRequest
        {
            public string Platform { get; set; }
            public string Phone { get; set; }
            public string Code { get; set; }
        }

        [HttpPost]
        [Route("api/ChatAccounts/SubmitCode")]
        public HttpResponseMessage SubmitCode(SubmitCodeRequest request)
        {
            try
            {
                // Placeholder: actual code submission requires Telegram/AdsPower workflow.
                if (request == null || string.IsNullOrWhiteSpace(request.Platform) || string.IsNullOrWhiteSpace(request.Phone) || string.IsNullOrWhiteSpace(request.Code))
                    return Request.CreateResponse(HttpStatusCode.BadRequest);

                var userId = HttpContext.Current?.User?.Identity?.Name;
                if (string.IsNullOrEmpty(userId))
                    return Request.CreateResponse(HttpStatusCode.Unauthorized);

                if (!Security.IsPaid)
                    return Request.CreateResponse((HttpStatusCode)402, "No active subscription");

                return Request.CreateResponse(HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                LogAdsPowerError("ChatAccounts SubmitCode failed with exception.", ex);
                return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "На сервере случилась критическая ошибка, обратитесь к администратору.\n\n" + ex);
            }
        }

        private async Task<string> EnsureAdsPowerGroupAsync(string groupName)
        {
            // API endpoints are based on AdsPower Local API docs.
            // Query: /api/v1/group/list?group_name=xxx
            // Create: /api/v1/group/create
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(15);

                try
                {
                    var listUrl = $"{AdsPowerBaseUrl.TrimEnd('/')}/api/v1/group/list?group_name={Uri.EscapeDataString(groupName)}&token={Uri.EscapeDataString(AdsPowerToken)}";
                    var listResp = await http.GetAsync(listUrl);
                    var listJson = await listResp.Content.ReadAsStringAsync();

                    if (!listResp.IsSuccessStatusCode)
                    {
                        LogAdsPowerError($"AdsPower group list failed. Status={(int)listResp.StatusCode}. Body={listJson}");
                        return null;
                    }

                    dynamic listObj = null;
                    try { listObj = JsonConvert.DeserializeObject(listJson); }
                    catch (Exception ex) { LogAdsPowerError($"AdsPower group list JSON parse failed. Body={listJson}", ex); }

                    if (listObj != null && listObj.code == 0 && listObj.data != null && listObj.data.list != null)
                    {
                        foreach (var g in listObj.data.list)
                        {
                            if ((string)g.group_name == groupName)
                            {
                                return (string)g.group_id;
                            }
                        }
                    }

                    var createUrl = $"{AdsPowerBaseUrl.TrimEnd('/')}/api/v1/group/create?token={Uri.EscapeDataString(AdsPowerToken)}";
                    var body = new { group_name = groupName };
                    var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                    var createResp = await http.PostAsync(createUrl, content);
                    var createJson = await createResp.Content.ReadAsStringAsync();

                    if (!createResp.IsSuccessStatusCode)
                    {
                        LogAdsPowerError($"AdsPower group create failed. Status={(int)createResp.StatusCode}. Body={createJson}");
                        return null;
                    }

                    dynamic createObj = null;
                    try { createObj = JsonConvert.DeserializeObject(createJson); }
                    catch (Exception ex) { LogAdsPowerError($"AdsPower group create JSON parse failed. Body={createJson}", ex); }

                    if (createObj != null && createObj.code == 0 && createObj.data != null)
                    {
                        // docs vary: sometimes group_id is in data.group_id
                        if (createObj.data.group_id != null) return (string)createObj.data.group_id;
                        if (createObj.data.id != null) return (string)createObj.data.id;
                    }

                    LogAdsPowerError($"AdsPower group create returned unexpected response. Body={createJson}");
                    return null;
                }
                catch (Exception ex)
                {
                    LogAdsPowerError("AdsPower group ensure failed with exception.", ex);
                    return null;
                }
            }
        }

        private async Task<string> CreateAdsPowerProfileAsync(string groupId, ChatAccountItem item)
        {
            // Create profile: /api/v1/user/create (POST), requires group_id and fingerprint_config
            // We'll use minimal fingerprint_config with automatic timezone.
            var url = $"{AdsPowerBaseUrl.TrimEnd('/')}/api/v1/user/create?token={Uri.EscapeDataString(AdsPowerToken)}";

            var platform = (item?.Platform ?? string.Empty).Trim();
            var phoneDigits = new string(((item?.Phone ?? string.Empty).Trim()).Where(char.IsDigit).ToArray());
            string domainName;
            if (platform.Equals("Telegram", StringComparison.OrdinalIgnoreCase))
            {
                domainName = "web.telegram.org";
            }
            else if (platform.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
            {
                domainName = "https://web.whatsapp.com";
            }
            else if (platform.Equals("MAX", StringComparison.OrdinalIgnoreCase) || platform.Equals("MessengerMAX", StringComparison.OrdinalIgnoreCase))
            {
                domainName = "web.max.ru";
            }
            else
            {
                domainName = "web.telegram.org";
            }

            var body = new
            {
                name = $"{item.Platform}-{item.Phone}",
                username = phoneDigits,
                group_id = groupId,
                domain_name = domainName,
                proxyid = AdsPowerProxyId,
                fingerprint_config = new
                {
                    automatic_timezone = "1"
                }
            };

            using (var http = new HttpClient())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(AdsPowerProxyId))
                    {
                        LogAdsPowerError("AdsPower profile create aborted: AdsPower:ProxyId is not configured.");
                        return null;
                    }

                    // AdsPower proxy configuration is passed via proxyid (e.g. numeric id, random, etc.)

                    http.Timeout = TimeSpan.FromSeconds(20);
                    var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                    var resp = await http.PostAsync(url, content);
                    var json = await resp.Content.ReadAsStringAsync();

                    if (!resp.IsSuccessStatusCode)
                    {
                        LogAdsPowerError($"AdsPower profile create failed. Status={(int)resp.StatusCode}. Body={json}");
                        return null;
                    }

                    dynamic obj = null;
                    try { obj = JsonConvert.DeserializeObject(json); }
                    catch (Exception ex) { LogAdsPowerError($"AdsPower profile create JSON parse failed. Body={json}", ex); }

                    var ok = obj != null && obj.code == 0 && obj.data != null && obj.data.id != null;
                    if (!ok)
                    {
                        LogAdsPowerError($"AdsPower profile create returned unexpected response. Body={json}");
                        return null;
                    }

                    return (string)obj.data.id;
                }
                catch (Exception ex)
                {
                    LogAdsPowerError("AdsPower profile create failed with exception.", ex);
                    return null;
                }
            }
        }

        [HttpDelete]
        [Route("api/ChatAccounts")]
        public HttpResponseMessage Delete([FromUri] string platform, [FromUri] string phone)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError);
            }

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
            {
                return Request.CreateResponse(HttpStatusCode.Unauthorized);
            }

            if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(phone))
            {
                return Request.CreateResponse(HttpStatusCode.BadRequest);
            }

            string adsPowerId = null;
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT TOP 1 ads_power_id
FROM accounts
WHERE user_id = @user_id AND platform = @platform AND phone = @phone";

                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = platform.Trim();
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = phone.Trim();

                cn.Open();
                adsPowerId = cmd.ExecuteScalar() as string;
            }

            if (!string.IsNullOrWhiteSpace(adsPowerId))
            {
                try
                {
                    DeleteAdsPowerProfile(adsPowerId);
                }
                catch
                {
                    // best-effort: still delete from DB
                }
            }

            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"
DELETE FROM accounts
WHERE user_id = @user_id AND platform = @platform AND phone = @phone";

                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = platform.Trim();
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = phone.Trim();

                cn.Open();
                var affected = cmd.ExecuteNonQuery();
                return Request.CreateResponse(affected > 0 ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }
        }

        private void DeleteAdsPowerProfile(string adsPowerId)
        {
            if (string.IsNullOrWhiteSpace(AdsPowerBaseUrl) || string.IsNullOrWhiteSpace(AdsPowerToken))
                return;

            try
            {
                // AdsPower API common endpoint: /api/v1/user/delete?token=... (POST)
                var url = $"{AdsPowerBaseUrl.TrimEnd('/')}/api/v1/user/delete?token={Uri.EscapeDataString(AdsPowerToken)}";
                var body = new { user_ids = new[] { adsPowerId } };

                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(15);
                    var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                    var resp = http.PostAsync(url, content).GetAwaiter().GetResult();
                    var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (!resp.IsSuccessStatusCode)
                    {
                        LogAdsPowerError($"AdsPower profile delete failed. Status={(int)resp.StatusCode}. Body={json}");
                        return;
                    }

                    dynamic obj = null;
                    try { obj = JsonConvert.DeserializeObject(json); }
                    catch (Exception ex) { LogAdsPowerError($"AdsPower profile delete JSON parse failed. Body={json}", ex); }

                    if (obj == null || obj.code != 0)
                    {
                        LogAdsPowerError($"AdsPower profile delete returned unexpected response. Body={json}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogAdsPowerError("AdsPower profile delete failed with exception.", ex);
            }
        }
    }
}
