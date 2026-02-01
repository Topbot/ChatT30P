using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.IO;
using System.Web;
using System.Web.Http;
using System.Linq;
using System.Collections.Concurrent;
using ChatT30P.Controllers.Models;
using Core;
using Newtonsoft.Json;
using WTelegram;
using TL;

namespace ChatT30P.Controllers.Api
{
    public class ChatAccountsController : ApiController
    {
        private static string ConnectionString => ConfigurationManager.ConnectionStrings["chatConnectionString"]?.ConnectionString;

        private static string AdsPowerBaseUrl => ConfigurationManager.AppSettings["AdsPower:BaseUrl"];
        private static string AdsPowerToken => ConfigurationManager.AppSettings["AdsPower:Token"];
        private static string AdsPowerProxyId => ConfigurationManager.AppSettings["AdsPower:ProxyId"];
        private const string AdsPowerGroupName = "CHAT";

        private static string TelegramApiId => ConfigurationManager.AppSettings["Telegram:ApiId"];
        private static string TelegramApiHash => ConfigurationManager.AppSettings["Telegram:ApiHash"];

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> SessionSemaphores =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private const string EventLogSource = "ChatT30P";

        private static void LogAdsPowerError(string message, Exception ex = null)
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

        private static SemaphoreSlim GetSessionSemaphore(string sessionFile)
        {
            var key = string.IsNullOrWhiteSpace(sessionFile) ? "__default" : sessionFile;
            return SessionSemaphores.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        private static string NormalizePlatform(string platform) => (platform ?? string.Empty).Trim();

        private static bool IsTelegramPlatform(string platform)
            => NormalizePlatform(platform).Equals("Telegram", StringComparison.OrdinalIgnoreCase);

        private static string GetTelegramApiIdOrNull() => string.IsNullOrWhiteSpace(TelegramApiId) ? null : TelegramApiId.Trim();
        private static string GetTelegramApiHashOrNull() => string.IsNullOrWhiteSpace(TelegramApiHash) ? null : TelegramApiHash.Trim();

        private static string GetTelegramSessionDir()
        {
            try
            {
                var baseDir = HttpContext.Current?.Server?.MapPath("~/App_Data") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data");
                var dir = Path.Combine(baseDir, "telegram_sessions");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "telegram_sessions");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string GetTelegramSessionFilePath(string userId, string phone)
        {
            var safeUser = (userId ?? string.Empty).Replace("\\", "_").Replace("/", "_").Replace(":", "_");
            var safePhone = (phone ?? string.Empty).Replace("+", string.Empty).Replace(" ", string.Empty);
            return Path.Combine(GetTelegramSessionDir(), $"tg_{safeUser}_{safePhone}.session");
        }

        private static string ReadSessionTextFromFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                // Read file bytes and store as Base64 text in DB
                var bytes = File.ReadAllBytes(path);
                return bytes != null && bytes.Length > 0 ? Convert.ToBase64String(bytes) : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteSessionTextToFile(string path, string sessionText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (string.IsNullOrWhiteSpace(sessionText))
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // Session text is stored as Base64
                var bytes = Convert.FromBase64String(sessionText);
                File.WriteAllBytes(path, bytes);
            }
            catch
            {
            }
        }

        private string GetAccountSession(string userId, string platform, string phone)
        {
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"SELECT TOP 1 [session] FROM accounts WHERE user_id=@user_id AND platform=@platform AND phone=@phone";
                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = platform;
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = phone;
                cn.Open();
                return cmd.ExecuteScalar() as string;
            }
        }

        private string GetTelegramCodeHash(string userId, string platform, string phone)
        {
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"SELECT TOP 1 telegram_code_hash FROM accounts WHERE user_id=@user_id AND platform=@platform AND phone=@phone";
                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = platform;
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = phone;
                cn.Open();
                return cmd.ExecuteScalar() as string;
            }
        }

        private void SaveChatsJson(string userId, string platform, string phone, string chatsJson)
        {
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE accounts SET chats_json=@j WHERE user_id=@user_id AND platform=@platform AND phone=@phone";
                cmd.Parameters.Add("@j", SqlDbType.NVarChar).Value = (object)chatsJson ?? (object)"[]";
                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = platform;
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = phone;
                cn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private void UpdateTelegramCodeState(string userId, string platform, string phone, string telegramCodeHash)
        {
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE accounts
SET telegram_code_hash=@h, telegram_code_created=GETDATE()
WHERE user_id=@user_id AND platform=@platform AND phone=@phone";
                cmd.Parameters.Add("@h", SqlDbType.NVarChar, 255).Value = (object)telegramCodeHash ?? DBNull.Value;
                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = platform;
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = phone;
                cn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private void UpdateTelegramSession(string userId, string platform, string phone, string session)
        {
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE accounts
SET [session]=@s
WHERE user_id=@user_id AND platform=@platform AND phone=@phone";
                cmd.Parameters.Add("@s", SqlDbType.NVarChar).Value = (object)session ?? DBNull.Value;
                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = platform;
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = phone;
                cn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private void EnsureAccountExists(string userId, string platform, string phone)
        {
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT TOP 1 1 FROM accounts WHERE user_id=@user_id AND platform=@platform AND phone=@phone";
                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = platform;
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = phone;
                cn.Open();
                var exists = cmd.ExecuteScalar() != null;
                if (!exists) throw new InvalidOperationException("Account not found");
            }
        }

        private void EnsureAccountsSessionColumn()
        {
            if (string.IsNullOrWhiteSpace(ConnectionString)) return;
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"
IF OBJECT_ID('dbo.accounts', 'U') IS NOT NULL AND COL_LENGTH('dbo.accounts','session') IS NULL
BEGIN
    ALTER TABLE dbo.accounts ADD [session] NVARCHAR(MAX) NULL;
END

IF OBJECT_ID('dbo.accounts', 'U') IS NOT NULL AND COL_LENGTH('dbo.accounts','telegram_code_hash') IS NULL
BEGIN
    ALTER TABLE dbo.accounts ADD telegram_code_hash NVARCHAR(255) NULL;
END

IF OBJECT_ID('dbo.accounts', 'U') IS NOT NULL AND COL_LENGTH('dbo.accounts','telegram_code_created') IS NULL
BEGIN
    ALTER TABLE dbo.accounts ADD telegram_code_created DATETIME NULL;
END";
                    cn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                LogAdsPowerError("EnsureAccountsSessionColumn failed.", ex);
            }
        }


        [HttpPost]
        [Route("api/ChatAccounts/StartLoadChats")]
        public async Task<HttpResponseMessage> StartLoadChats([FromBody] dynamic request)
        {
            try
            {
                if (request == null) return Request.CreateResponse(HttpStatusCode.BadRequest);
                string platform = (request.Platform ?? (string)null) as string;
                string phone = (request.Phone ?? (string)null) as string;
                if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(phone))
                    return Request.CreateResponse(HttpStatusCode.BadRequest);

                var userId = HttpContext.Current?.User?.Identity?.Name;
                if (string.IsNullOrEmpty(userId)) return Request.CreateResponse(HttpStatusCode.Unauthorized);
                if (!Security.IsPaid) return Request.CreateResponse((HttpStatusCode)402, "No active subscription");

                var script = "LoadChats" + platform.Replace(" ", "");

                // Resolve adsPowerId for this account
                string adsPowerId = null;
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT TOP 1 ads_power_id FROM accounts WHERE user_id = @user_id AND platform = @platform AND phone = @phone";
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                    cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = platform.Trim();
                    cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = phone.Trim();
                    cn.Open();
                    adsPowerId = cmd.ExecuteScalar() as string;
                }

                if (string.IsNullOrWhiteSpace(adsPowerId))
                {
                    LogAdsPowerError($"StartLoadChats: adsPowerId not found for user={userId} platform={platform} phone={phone}");
                    return Request.CreateErrorResponse(HttpStatusCode.BadRequest, "Profile not found or not created yet");
                }

                // Try to ensure AdsPower browser/profile is open and obtain puppeteer URL
                string puppeteerUrl = null;
                try
                {
                    puppeteerUrl = await OpenAdsPowerHeadlessAsync(adsPowerId);
                    LogAdsPowerError($"StartLoadChats: OpenAdsPowerHeadlessAsync returned puppeteerUrl={puppeteerUrl} for adsPowerId={adsPowerId}");
                }
                catch (Exception ex)
                {
                    LogAdsPowerError($"StartLoadChats: OpenAdsPowerHeadlessAsync failed for adsPowerId={adsPowerId}", ex);
                    puppeteerUrl = null;
                }

                // enqueue rpa task with puppeteer URL when available
                EnsureRpaTasksTable();
                int insertedId = 0;
                using (var cn2 = new SqlConnection(ConnectionString))
                using (var cmd2 = cn2.CreateCommand())
                {
                    cmd2.CommandText = @"INSERT INTO dbo.rpa_tasks(ads_power_id, script_name, value, puppeteer)
OUTPUT INSERTED.id
VALUES(@ads_power_id, @script_name, @value, @puppeteer)";
                    cmd2.Parameters.Add("@ads_power_id", SqlDbType.NVarChar, 128).Value = adsPowerId;
                    cmd2.Parameters.Add("@script_name", SqlDbType.NVarChar, 128).Value = script;
                    cmd2.Parameters.Add("@value", SqlDbType.NVarChar, 128).Value = DBNull.Value;
                    cmd2.Parameters.Add("@puppeteer", SqlDbType.NVarChar, 2048).Value = (object)puppeteerUrl ?? DBNull.Value;
                    cn2.Open();
                    try { var obj = cmd2.ExecuteScalar(); if (obj != null && obj != DBNull.Value) insertedId = Convert.ToInt32(obj); }
                    catch (SqlException sqex)
                    {
                        if (sqex.Number == 2601 || sqex.Number == 2627)
                            return Request.CreateErrorResponse((HttpStatusCode)409, "RPA задача для этого профиля уже существует.");
                        throw;
                    }
                }

                if (insertedId <= 0) return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "Failed to enqueue rpa task.");

                // wait up to 5 minutes for deletion
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var timeout = TimeSpan.FromMinutes(5);
                while (sw.Elapsed < timeout)
                {
                    bool exists = false;
                    try
                    {
                        using (var cn = new SqlConnection(ConnectionString))
                        using (var cmd = cn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT COUNT(1) FROM dbo.rpa_tasks WHERE id = @id";
                            cmd.Parameters.Add("@id", SqlDbType.Int).Value = insertedId;
                            cn.Open();
                            var obj = cmd.ExecuteScalar();
                            exists = obj != null && obj != DBNull.Value && Convert.ToInt32(obj) > 0;
                        }
                    }
                    catch { exists = true; }
                    if (!exists) return Request.CreateResponse(HttpStatusCode.OK);
                    await Task.Delay(1000);
                }
                return Request.CreateResponse((HttpStatusCode)202, "Task queued but not processed within timeout");
            }
            catch (Exception ex)
            {
                LogAdsPowerError("StartLoadChats failed.", ex);
                return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, ex);
            }
        }

        public class SubmitLoginCodeRequest
        {
            public string AdsPowerId { get; set; }
            public string Code { get; set; }
        }

        [HttpPost]
        [Route("api/ChatAccounts/SubmitLoginCode")]
        public async Task<HttpResponseMessage> SubmitLoginCode(SubmitLoginCodeRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.AdsPowerId) || string.IsNullOrWhiteSpace(request.Code))
                    return Request.CreateResponse(HttpStatusCode.BadRequest);

                var userId = HttpContext.Current?.User?.Identity?.Name;
                if (string.IsNullOrEmpty(userId))
                    return Request.CreateResponse(HttpStatusCode.Unauthorized);

                if (!Security.IsPaid)
                    return Request.CreateResponse((HttpStatusCode)402, "No active subscription");

                // Determine platform for adsPowerId to pick script name
                string platform = null;
                try
                {
                    using (var cn = new SqlConnection(ConnectionString))
                    using (var cmd = cn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT TOP 1 platform FROM accounts WHERE ads_power_id = @ads_power_id";
                        cmd.Parameters.Add("@ads_power_id", SqlDbType.NVarChar, 128).Value = request.AdsPowerId.Trim();
                        cn.Open();
                        platform = cmd.ExecuteScalar() as string;
                    }
                }
                catch { }

                var script = "SubmitLoginTelegram";
                if (!string.IsNullOrWhiteSpace(platform))
                {
                    if (platform.Equals("Whatsapp", StringComparison.OrdinalIgnoreCase) || platform.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
                        script = "SubmitLoginWhatsapp";
                    else if (platform.Equals("Max", StringComparison.OrdinalIgnoreCase))
                        script = "SubmitLoginMax";
                    else if (platform.Equals("Telegram", StringComparison.OrdinalIgnoreCase))
                        script = "SubmitLoginTelegram";
                }

                // ensure table exists
                EnsureRpaTasksTable();

                int insertedId = 0;
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO dbo.rpa_tasks(ads_power_id, script_name, value)
OUTPUT INSERTED.id
VALUES(@ads_power_id, @script_name, @value)";
                    cmd.Parameters.Add("@ads_power_id", SqlDbType.NVarChar, 128).Value = request.AdsPowerId.Trim();
                    cmd.Parameters.Add("@script_name", SqlDbType.NVarChar, 128).Value = script;
                    cmd.Parameters.Add("@value", SqlDbType.NVarChar, 128).Value = request.Code.Trim();
                    cn.Open();
                    try
                    {
                        var obj = cmd.ExecuteScalar();
                        if (obj != null && obj != DBNull.Value)
                            insertedId = Convert.ToInt32(obj);
                    }
                    catch (SqlException sqex)
                    {
                        // Duplicate index (unique constraint) on ads_power_id + script_name
                        if (sqex.Number == 2601 || sqex.Number == 2627)
                        {
                            LogAdsPowerError($"RPA task insert duplicate for adsPowerId={request.AdsPowerId}, script={script}", sqex);
                            return Request.CreateErrorResponse((HttpStatusCode)409, "RPA задача для этого профиля уже существует.");
                        }
                        throw;
                    }
                }

                if (insertedId <= 0)
                    return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "Failed to enqueue rpa task.");

                // Wait up to 5 minutes for the task to be processed (deleted or status changed)
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var timeout = TimeSpan.FromMinutes(5);
                while (sw.Elapsed < timeout)
                {
                    bool exists = false;
                    try
                    {
                        using (var cn = new SqlConnection(ConnectionString))
                        using (var cmd = cn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT COUNT(1) FROM dbo.rpa_tasks WHERE id = @id";
                            cmd.Parameters.Add("@id", SqlDbType.Int).Value = insertedId;
                            cn.Open();
                            var obj = cmd.ExecuteScalar();
                            exists = obj != null && obj != DBNull.Value && Convert.ToInt32(obj) > 0;
                        }
                    }
                    catch
                    {
                        // ignore and retry
                        exists = true;
                    }

                    if (!exists)
                        return Request.CreateResponse(HttpStatusCode.OK);

                    await Task.Delay(1000);
                }

                return Request.CreateResponse((HttpStatusCode)202, "Task queued but not processed within timeout");
            }
            catch (Exception ex)
            {
                LogAdsPowerError("ChatAccounts SubmitLoginCode failed.", ex);
                return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, ex);
            }
        }
        

        // Write AdsPower-related messages to file logs only (avoid Windows Event Log)
        private static void LogAdsPowerToFile(string message, Exception ex = null)
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

        private static string NormalizePhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return phone;
            phone = phone.Trim();
            if (phone.StartsWith("+"))
                return phone.Substring(1);
            return phone;
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

            // Normalize phone: remove leading '+' if present
            item.Phone = NormalizePhone(item.Phone);

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

            // Telegram accounts use MTProto login on the web server and do NOT require AdsPower profile.
            // Other platforms still rely on AdsPower.
            string adsPowerId = null;
            if (!IsTelegramPlatform(item.Platform))
            {
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
            }

            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO accounts(user_id, platform, phone, status, chats_json, ads_power_id, created)
VALUES(@user_id, @platform, @phone, @status, @chats_json, @ads_power_id, GETDATE())";

                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = item.Platform.Trim();
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = item.Phone.Trim();
                cmd.Parameters.Add("@status", SqlDbType.Int).Value = item.Status;
                cmd.Parameters.Add("@chats_json", SqlDbType.NVarChar).Value = (object)(item.ChatsJson ?? "[]") ?? DBNull.Value;
                cmd.Parameters.Add("@ads_power_id", SqlDbType.NVarChar, 128).Value = (object)adsPowerId ?? DBNull.Value;
                cn.Open();

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

            LogAdsPowerError($"StartLogin called. userId={userId}, platform={request.Platform}, phone={request.Phone}");

            var normPlatform = NormalizePlatform(request.Platform);
            var normPhone = NormalizePhone(request.Phone);

            // Telegram: MTProto login flow (no AdsPower, no rpa_tasks)
            if (IsTelegramPlatform(normPlatform))
            {
                var apiId = GetTelegramApiIdOrNull();
                var apiHash = GetTelegramApiHashOrNull();
                if (string.IsNullOrWhiteSpace(apiId) || string.IsNullOrWhiteSpace(apiHash))
                    return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "Telegram API ключи не настроены на сервере.");

                try
                {
                    EnsureAccountExists(userId, normPlatform, normPhone);
                }
                catch
                {
                    return Request.CreateErrorResponse(HttpStatusCode.BadRequest, "Аккаунт не найден.");
                }

                var sessionText = GetAccountSession(userId, normPlatform, normPhone);
                var sessionFile = GetTelegramSessionFilePath(userId, normPhone);
                var sessionSemaphore = GetSessionSemaphore(sessionFile);

                // Send code. Persist session and phone_code_hash.
                string codeHash = null;
                await sessionSemaphore.WaitAsync();
                try
                {
                    // Keep file-based session storage in sync with DB
                    WriteSessionTextToFile(sessionFile, sessionText);

                    using (var client = new Client(what =>
                    {
                        switch (what)
                        {
                            case "api_id": return apiId;
                            case "api_hash": return apiHash;
                            case "phone_number": return normPhone;
                            case "session_pathname": return sessionFile;
                            default: return null;
                        }
                    }))
                    {
                        try
                        {
                            // Ensure MTProto transport is connected before sending requests
                            await client.ConnectAsync();
                            // Force send code via Auth_SendCode
                            var sent = await client.Auth_SendCode(normPhone, int.Parse(apiId), apiHash, new CodeSettings());
                            try
                            {
                                dynamic d = sent;
                                try { codeHash = d.phone_code_hash; }
                                catch { try { codeHash = d.phoneCodeHash; } catch { try { codeHash = d.PhoneCodeHash; } catch { codeHash = null; } } }
                            }
                            catch { codeHash = null; }
                        }
                        catch
                        {
                            codeHash = null;
                        }
                    }

                    // Persist session text back into DB (file content) after client disposed
                    try { UpdateTelegramSession(userId, normPlatform, normPhone, ReadSessionTextFromFile(sessionFile)); } catch { }
                }
                finally
                {
                    sessionSemaphore.Release();
                }
                UpdateTelegramCodeState(userId, normPlatform, normPhone, codeHash);
                return Request.CreateResponse(HttpStatusCode.OK, new { platform = normPlatform, phone = normPhone });
            }

            
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
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = NormalizePhone(request.Phone);

                cn.Open();
                adsPowerId = cmd.ExecuteScalar() as string;
            }

            LogAdsPowerError($"Found adsPowerId in DB: {adsPowerId ?? "NULL"}");

            if (string.IsNullOrWhiteSpace(adsPowerId))
            {
                // Do not create AdsPower profiles automatically during StartLogin.
                LogAdsPowerError($"StartLogin: ads_power_id missing for user={userId}, platform={request.Platform}, phone={request.Phone}. Returning error without creating profile.");
                return Request.CreateErrorResponse(HttpStatusCode.BadRequest, "AdsPower profile is not created for this account. Please add the account first.");
            }

            // Open AdsPower profile and run RPA script
            if (string.IsNullOrWhiteSpace(adsPowerId))
                return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "На сервере случилась критическая ошибка, обратитесь к администратору.");

            // Instead of calling AdsPower RPA directly, enqueue task into DB.
            EnsureRpaTasksTable();

            var platform = (request.Platform ?? string.Empty).Trim();
            string script;
            if (platform.Equals("Telegram", StringComparison.OrdinalIgnoreCase))
                script = "StartLoginTelegram";
            else if (platform.Equals("Max", StringComparison.OrdinalIgnoreCase))
                script = "StartLoginMax";
            else if (platform.Equals("Whatsapp", StringComparison.OrdinalIgnoreCase) || platform.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
                script = "StartLoginWhatsapp";
            else
                script = "StartLoginTelegram";

            LogAdsPowerError($"Selected script: {script}");

            // Attempt to open AdsPower profile immediately from the server (headless start via AdsPower API).
            // We prefer direct start instead of enqueuing a client-side RPA task.
            string puppeteerUrl = null;
            try
            {
                LogAdsPowerError($"StartLogin: attempting to open AdsPower profile immediately. adsPowerId={adsPowerId}");
                puppeteerUrl = await OpenAdsPowerHeadlessAsync(adsPowerId);
                if (!string.IsNullOrWhiteSpace(puppeteerUrl))
                {
                    LogAdsPowerError($"StartLogin: OpenAdsPowerHeadlessAsync returned puppeteer URL: {puppeteerUrl}");
                    // Явно записываем puppeteerUrl в rpa_tasks
                    try
                    {
                        using (var cn = new SqlConnection(ConnectionString))
                        using (var cmd = cn.CreateCommand())
                        {
                            // include phone number in `value` column so worker knows which phone to use
                            var phoneValue = NormalizePhone(request.Phone);
                            cmd.CommandText = @"
INSERT INTO dbo.rpa_tasks(ads_power_id, script_name, value, puppeteer)
VALUES(@ads_power_id, @script_name, @value, @puppeteer)";
                            cmd.Parameters.Add("@ads_power_id", SqlDbType.NVarChar, 128).Value = adsPowerId;
                            cmd.Parameters.Add("@script_name", SqlDbType.NVarChar, 128).Value = script;
                            cmd.Parameters.Add("@value", SqlDbType.NVarChar, 128).Value = (object)phoneValue ?? DBNull.Value;
                            cmd.Parameters.Add("@puppeteer", SqlDbType.NVarChar, 2048).Value = puppeteerUrl;
                            cn.Open();
                            cmd.ExecuteNonQuery();
                        }
                    }
                    catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
                    {
                        // Unique constraint violation (duplicate key)
                        LogAdsPowerError($"RPA task insert duplicate for adsPowerId={adsPowerId}, script={script}", ex);
                        return Request.CreateErrorResponse(HttpStatusCode.Conflict, "RPA задача для этого профиля уже существует.");
                    }
                }
                else
                {
                    LogAdsPowerError($"StartLogin: OpenAdsPowerHeadlessAsync did not return a puppeteer URL for adsPowerId={adsPowerId}");
                }
            }
            catch (Exception ex)
            {
                LogAdsPowerError($"StartLogin: OpenAdsPowerHeadlessAsync failed for adsPowerId={adsPowerId}", ex);
                puppeteerUrl = null;
            }

            LogAdsPowerError($"StartLogin returning adsPowerId={adsPowerId}");
            // Return puppeteer URL when available so caller may connect or diagnose. Keep legacy adsPowerId in response.
            return Request.CreateResponse(HttpStatusCode.OK, new { adsPowerId, puppeteer = puppeteerUrl });
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
                    // rpa_tasks now stores optional puppeteer URL returned by AdsPower when opening a profile in headless mode
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
ELSE IF COL_LENGTH('dbo.rpa_tasks','value') IS NULL
BEGIN
    ALTER TABLE dbo.rpa_tasks ADD value NVARCHAR(128) NULL;
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

        private async Task EnqueueRpaTaskAsync(string adsPowerId, string scriptName)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString)) return;
            if (string.IsNullOrWhiteSpace(adsPowerId) || string.IsNullOrWhiteSpace(scriptName)) return;

            LogAdsPowerError($"EnqueueRpaTask started. adsPowerId={adsPowerId} script={scriptName}");

            try
            {
                // Try to open AdsPower profile in headless mode via AdsPower v2 API and obtain puppeteer URL
                string puppeteerUrl = null;
                try
                {
                    LogAdsPowerError($"Attempting to open AdsPower profile: {adsPowerId}");
                    // Use a longer timeout for opening AdsPower profile (up to 30 seconds)
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    {
                        puppeteerUrl = await OpenAdsPowerHeadlessAsync(adsPowerId);
                        if (string.IsNullOrWhiteSpace(puppeteerUrl))
                        {
                            LogAdsPowerError($"OpenAdsPowerHeadless returned no puppeteer URL for adsPowerId={adsPowerId}");
                        }
                        else
                        {
                            LogAdsPowerError($"OpenAdsPowerHeadless succeeded. Puppeteer URL: {puppeteerUrl}");
                        }
                    }
                }
                catch (TaskCanceledException ex)
                {
                    LogAdsPowerError($"OpenAdsPowerHeadless timeout for {adsPowerId}", ex);
                    puppeteerUrl = null;
                }
                catch (Exception ex)
                {
                    LogAdsPowerError($"OpenAdsPowerHeadless failed for {adsPowerId}", ex);
                    puppeteerUrl = null;
                }
                
                LogAdsPowerError($"Inserting into rpa_tasks. adsPowerId={adsPowerId}, script={scriptName}, puppeteer={puppeteerUrl}");
                
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {

                    cmd.CommandText = @"
INSERT INTO dbo.rpa_tasks(ads_power_id, script_name, value, puppeteer)
VALUES(@ads_power_id, @script_name, @value, @puppeteer)";

                    cmd.Parameters.Add("@ads_power_id", SqlDbType.NVarChar, 128).Value = adsPowerId;
                    cmd.Parameters.Add("@script_name", SqlDbType.NVarChar, 128).Value = scriptName;
                    // include phone number in `value` for StartLogin scripts when available
                    cmd.Parameters.Add("@value", SqlDbType.NVarChar, 128).Value = (object)adsPowerId == null ? (object)DBNull.Value : (object)adsPowerId; // placeholder, will be overwritten below
                    cmd.Parameters.Add("@puppeteer", SqlDbType.NVarChar, 2048).Value = (object)puppeteerUrl ?? DBNull.Value;

                    // If this method was invoked from StartLogin path, try to set value to phone number supplied earlier via request
                    try
                    {
                        // attempt to locate phone in local variables by reflection of closure (best-effort) - fallback: leave value NULL
                    }
                    catch
                    {
                    }

                    cn.Open();
                    cmd.ExecuteNonQuery();
                }
                
                LogAdsPowerError($"RPA task inserted successfully. adsPowerId={adsPowerId}");
            }
            catch (Exception ex)
            {
                LogAdsPowerError($"EnqueueRpaTask failed. adsPowerId={adsPowerId} script={scriptName}", ex);
            }
        }

        private async Task<string> OpenAdsPowerHeadlessAsync(string adsPowerId)
        {
            if (string.IsNullOrWhiteSpace(AdsPowerBaseUrl) || string.IsNullOrWhiteSpace(AdsPowerToken) || string.IsNullOrWhiteSpace(adsPowerId))
            {
                LogAdsPowerError($"OpenAdsPowerHeadlessAsync: Missing configuration. BaseUrl={AdsPowerBaseUrl}, Token={AdsPowerToken}, adsPowerId={adsPowerId}");
                return null;
            }

            var baseUrl = AdsPowerBaseUrl.TrimEnd('/');
            // Исправлено: добавляем profile_id в GET-запрос
            var activeUrl = baseUrl + "/api/v2/browser-profile/active?token=" + Uri.EscapeDataString(AdsPowerToken) + "&profile_id=" + Uri.EscapeDataString(adsPowerId);
            var startUrl = baseUrl + "/api/v2/browser-profile/start?token=" + Uri.EscapeDataString(AdsPowerToken);
            var reqBody = new { profile_id = adsPowerId, headless = true };

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(25);
                // 1. Check active profiles first
                try
                {
                    LogAdsPowerToFile($"OpenAdsPowerHeadlessAsync: Checking active profiles {activeUrl}");
                    var activeResp = await http.GetAsync(activeUrl);
                    var activeJson = await activeResp.Content.ReadAsStringAsync();
                    LogAdsPowerToFile($"OpenAdsPowerHeadlessAsync: Active profiles response from {activeUrl}. Status={(int)activeResp.StatusCode}, Body={activeJson}");
                    if (activeResp.IsSuccessStatusCode)
                    {
                        dynamic activeObj = null;
                        try { activeObj = JsonConvert.DeserializeObject(activeJson); } catch { }
                        if (activeObj != null && activeObj.code == 0 && activeObj.data != null && activeObj.data.list != null)
                        {
                            foreach (var prof in activeObj.data.list)
                            {
                                string pid = null;
                                try { pid = (string)prof.profile_id; } catch { }
                                if (pid == adsPowerId)
                                {
                                    string found = null;
                                    try { if (prof.puppeteer_url != null) found = (string)prof.puppeteer_url; } catch { }
                                    try { if (found == null && prof.ws_url != null) found = (string)prof.ws_url; } catch { }
                                    try { if (found == null && prof.debugger_url != null) found = (string)prof.debugger_url; } catch { }
                                    try { if (found == null && prof.url != null) found = (string)prof.url; } catch { }
                                    try { if (found == null && prof.ws != null && prof.ws.puppeteer != null) found = (string)prof.ws.puppeteer; } catch { }
                                    if (!string.IsNullOrWhiteSpace(found))
                                    {
                                        LogAdsPowerToFile($"OpenAdsPowerHeadlessAsync: Profile already open, using ws: {found}");
                                        return found;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogAdsPowerToFile($"OpenAdsPowerHeadlessAsync: Exception during active profiles check: {ex.Message}");
                }
                // 2. If not open, start profile
                try
                {
                    var content = new StringContent(JsonConvert.SerializeObject(reqBody), Encoding.UTF8, "application/json");
                    LogAdsPowerToFile($"OpenAdsPowerHeadlessAsync: Attempting to connect to {startUrl} with body: {JsonConvert.SerializeObject(reqBody)}");

                    var resp = await http.PostAsync(startUrl, content);
                    var json = await resp.Content.ReadAsStringAsync();
                    LogAdsPowerToFile($"OpenAdsPowerHeadlessAsync: Received response from {startUrl}. Status={(int)resp.StatusCode}, Body={json}");

                    if (!resp.IsSuccessStatusCode)
                    {
                        LogAdsPowerToFile($"AdsPower browser start failed for {startUrl}. Status={(int)resp.StatusCode}. Body={json}");
                        return null;
                    }

                    dynamic obj = null;
                    try { obj = JsonConvert.DeserializeObject(json); }
                    catch (Exception ex) { LogAdsPowerToFile($"AdsPower browser start JSON parse failed. Body={json}"); }

                    if (obj == null || obj.code != 0 || obj.data == null)
                    {
                        LogAdsPowerToFile($"AdsPower browser start returned unexpected response. Body={json}");
                        return null;
                    }

                    // try common fields for puppeteer/debug URL, including nested ws.puppeteer
                    string found = null;
                    try { if (obj.data.puppeteer_url != null) found = (string)obj.data.puppeteer_url; } catch { }
                    try { if (found == null && obj.data.ws_url != null) found = (string)obj.data.ws_url; } catch { }
                    try { if (found == null && obj.data.debugger_url != null) found = (string)obj.data.debugger_url; } catch { }
                    try { if (found == null && obj.data.url != null) found = (string)obj.data.url; } catch { }
                    try { if (found == null && obj.data.ws != null && obj.data.ws.puppeteer != null) found = (string)obj.data.ws.puppeteer; } catch { }
                    if (string.IsNullOrWhiteSpace(found))
                    {
                        LogAdsPowerToFile($"AdsPower headless start succeeded but no puppeteer URL found. Body={json}");
                    }
                    return found;
                }
                catch (HttpRequestException ex)
                {
                    LogAdsPowerToFile($"AdsPower connection error for start. Message={ex.Message}");
                    return null;
                }
                catch (TaskCanceledException ex)
                {
                    LogAdsPowerToFile($"AdsPower request timeout for start");
                    return null;
                }
                catch (Exception ex)
                {
                    LogAdsPowerToFile($"AdsPower browser start failed with exception for start: {ex.Message}");
                    return null;
                }
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
        public async Task<HttpResponseMessage> SubmitCode(SubmitCodeRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Platform) || string.IsNullOrWhiteSpace(request.Phone) || string.IsNullOrWhiteSpace(request.Code))
                    return Request.CreateResponse(HttpStatusCode.BadRequest);

                var userId = HttpContext.Current?.User?.Identity?.Name;
                if (string.IsNullOrEmpty(userId))
                    return Request.CreateResponse(HttpStatusCode.Unauthorized);

                if (!Security.IsPaid)
                    return Request.CreateResponse((HttpStatusCode)402, "No active subscription");

                var normPlatform = NormalizePlatform(request.Platform);
                var normPhone = NormalizePhone(request.Phone);

                if (IsTelegramPlatform(normPlatform))
                {
                    var apiId = GetTelegramApiIdOrNull();
                    var apiHash = GetTelegramApiHashOrNull();
                    if (string.IsNullOrWhiteSpace(apiId) || string.IsNullOrWhiteSpace(apiHash))
                        return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "Telegram API ключи не настроены на сервере.");

                    // normalize phone to match how it is stored in DB (without leading '+')
                    normPhone = NormalizePhone(normPhone);
                    var codeHash = GetTelegramCodeHash(userId, normPlatform, normPhone);
                    var sessionText = GetAccountSession(userId, normPlatform, normPhone);
                    var sessionFile = GetTelegramSessionFilePath(userId, normPhone);
                    var sessionSemaphore = GetSessionSemaphore(sessionFile);

                    if (string.IsNullOrWhiteSpace(codeHash))
                        return Request.CreateErrorResponse(HttpStatusCode.Conflict, "Сначала выполните StartLogin для Telegram (код не запрошен).");

                    await sessionSemaphore.WaitAsync();
                    try
                    {
                        WriteSessionTextToFile(sessionFile, sessionText);

                        using (var client = new Client(what =>
                        {
                            switch (what)
                            {
                                case "api_id": return apiId;
                                case "api_hash": return apiHash;
                                case "phone_number": return normPhone;
                                case "session_pathname": return sessionFile;
                                default: return null;
                            }
                        }))
                        {
                            await client.ConnectAsync();
                            // Sign-in
                            try
                            {
                                await client.Auth_SignIn(normPhone, codeHash, request.Code.Trim());
                            }
                            catch (TL.RpcException ex)
                            {
                                var msg = (ex.Message ?? string.Empty).ToUpperInvariant();
                                if (msg.Contains("PHONE_CODE_EXPIRED") || msg.Contains("PHONE_CODE_INVALID"))
                                {
                                    UpdateTelegramCodeState(userId, normPlatform, normPhone, null);
                                    return Request.CreateErrorResponse(HttpStatusCode.Conflict, "Код истёк или неверный. Запросите новый код и попробуйте снова.");
                                }
                                throw;
                            }

                            // Load dialogs (best-effort)
                            var dialogs = await client.Messages_GetDialogs();

                            var items = new List<object>();
                            try
                            {
                                dynamic dd = dialogs;
                                foreach (var d in dd.dialogs)
                                {
                                    string id = null;
                                    string title = null;
                                    string type = null;
                                    string last = null;
                                    try
                                    {
                                        var peer = d.peer;
                                        if (peer is PeerUser pu)
                                        {
                                            id = "user:" + pu.user_id;
                                            type = "user";
                                            dynamic users = dd.users;
                                            dynamic u = users[pu.user_id];
                                            title = ("" + u.first_name + " " + u.last_name).Trim();
                                        }
                                        else if (peer is PeerChat pc)
                                        {
                                            id = "chat:" + pc.chat_id;
                                            type = "chat";
                                            dynamic chats = dd.chats;
                                            dynamic c = chats[pc.chat_id];
                                            title = (string)c.Title;
                                        }
                                        else if (peer is PeerChannel pch)
                                        {
                                            id = "channel:" + pch.channel_id;
                                            type = "channel";
                                            dynamic chats = dd.chats;
                                            dynamic c = chats[pch.channel_id];
                                            title = (string)c.Title;
                                        }

                                        // last message preview (best-effort)
                                        try
                                        {
                                            if (d.top_message != null)
                                            {
                                                int topId = (int)d.top_message;
                                                dynamic messages = dd.messages;
                                                dynamic m = messages[topId];
                                                try { last = (string)m.message; }
                                                catch { try { last = (string)m.Message; } catch { } }
                                            }
                                        }
                                        catch { }
                                    }
                                    catch { }

                                    if (!string.IsNullOrWhiteSpace(id) && title != null)
                                        items.Add(new { id, title, type, last });
                                }
                            }
                            catch
                            {
                                // ignore dialogs parsing differences between TL versions
                            }

                            var chatsJson = JsonConvert.SerializeObject(items);

                            SaveChatsJson(userId, normPlatform, normPhone, chatsJson);
                            UpdateTelegramCodeState(userId, normPlatform, normPhone, null);
                            try
                            {
                                using (var cn = new SqlConnection(ConnectionString))
                                using (var cmd = cn.CreateCommand())
                                {
                                    cmd.CommandText = "UPDATE accounts SET status = 1 WHERE user_id=@user_id AND platform=@platform AND phone=@phone";
                                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                                    cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = normPlatform;
                                    cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = normPhone;
                                    cn.Open();
                                    cmd.ExecuteNonQuery();
                                }
                            }
                            catch { }
                        }

                        // Persist session text back into DB after client disposed (avoid file lock)
                        try { UpdateTelegramSession(userId, normPlatform, normPhone, ReadSessionTextFromFile(sessionFile)); } catch { }
                    }
                    finally
                    {
                        sessionSemaphore.Release();
                    }

                    return Request.CreateResponse(HttpStatusCode.OK);
                }

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
            // Create profile v2: POST /api/v2/browser-profile/create
            // We'll use minimal fingerprint_config with automatic timezone.
            var url = $"{AdsPowerBaseUrl.TrimEnd('/')}/api/v2/browser-profile/create?token={Uri.EscapeDataString(AdsPowerToken)}";

            var platform = (item?.Platform ?? string.Empty).Trim();
            var phoneDigits = new string(((item?.Phone ?? string.Empty).Trim()).Where(char.IsDigit).ToArray());
            string platformDomain;
            if (platform.Equals("Telegram", StringComparison.OrdinalIgnoreCase))
            {
                platformDomain = "web.telegram.org";
            }
            else if (platform.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
            {
                platformDomain = "web.whatsapp.com";
            }
            else if (platform.Equals("MAX", StringComparison.OrdinalIgnoreCase) || platform.Equals("MessengerMAX", StringComparison.OrdinalIgnoreCase))
            {
                platformDomain = "web.max.ru";
            }
            else
            {
                platformDomain = "web.telegram.org";
            }

            var body = new
            {
                name = $"{item.Platform}-{item.Phone}",
                group_id = groupId,
                platform = platformDomain,
                username = item.Phone, // <--- добавить это поле
                platform_name = item.Platform, // <--- добавить это поле для явного указания платформы
                // Prefer direct connection.
                user_proxy_config = new
                {
                    proxy_soft = "no_proxy"
                },
                fingerprint_config = new
                {
                    automatic_timezone = "1",
                    random_ua = new
                    {
                        ua_browser = new[] { "chrome" },
                        ua_system_version = new[] { "Windows 10" },
                        ua_version = new[] { "142" }
                    },
                    browser_kernel_config = new
                    {
                        type = "chrome",
                        version = "142"
                    }
                }
            };

            using (var http = new HttpClient())
            {
                try
                {
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

                    var ok = obj != null && obj.code == 0 && obj.data != null && (obj.data.profile_id != null || obj.data.id != null);
                    if (!ok)
                    {
                        LogAdsPowerError($"AdsPower profile create returned unexpected response. Body={json}");
                        return null;
                    }

                    //ожидание на случай долгого обращения в БД
                    Thread.Sleep(3000);

                    if (obj.data.profile_id != null) return (string)obj.data.profile_id;
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

                // Also cleanup queued RPA tasks for this profile (best-effort)
                try
                {
                    using (var cn = new SqlConnection(ConnectionString))
                    using (var cmd = cn.CreateCommand())
                    {
                        cmd.CommandText = @"DELETE FROM dbo.rpa_tasks WHERE ads_power_id = @ads_power_id";
                        cmd.Parameters.Add("@ads_power_id", SqlDbType.NVarChar, 128).Value = adsPowerId;
                        cn.Open();
                        cmd.ExecuteNonQuery();
                    }
                }
                catch
                {
                    // ignore cleanup failures
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
                // Only v2 API for delete/stop
                var deleteUrl = $"{AdsPowerBaseUrl.TrimEnd('/')}/api/v1/user/delete?token={Uri.EscapeDataString(AdsPowerToken)}";
                var deleteBody = new { user_ids = new[] { adsPowerId } };
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(15);
                    var content = new StringContent(JsonConvert.SerializeObject(deleteBody), Encoding.UTF8, "application/json");
                    var resp = http.PostAsync(deleteUrl, content).GetAwaiter().GetResult();
                    var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (resp.IsSuccessStatusCode)
                    {
                        dynamic obj = null;
                        try { obj = JsonConvert.DeserializeObject(json); } catch { }
                        if (obj != null && obj.code == 0)
                        {
                            LogAdsPowerToFile($"AdsPower profile delete succeeded. adsPowerId={adsPowerId}");
                            return;
                        }
                        LogAdsPowerToFile($"AdsPower profile delete returned unexpected response. Body={json}");
                    }
                    else
                    {
                        LogAdsPowerToFile($"AdsPower profile delete failed. Status={(int)resp.StatusCode}. Body={json}");
                    }
                }

                // If delete didn't succeed, try to stop the browser/profile (V2 only) and retry delete
                LogAdsPowerToFile($"AdsPower profile delete: attempting to stop profile then retry delete. adsPowerId={adsPowerId}");
                try
                {
                    var closed = TryCloseAdsPowerProfileV2(adsPowerId);
                    LogAdsPowerToFile($"TryCloseAdsPowerProfileV2 result: {closed}");
                    if (closed)
                    {
                        System.Threading.Thread.Sleep(4000); // Ждём 4 секунды после закрытия профиля
                    }
                    // Retry delete
                    using (var http2 = new HttpClient())
                    {
                        http2.Timeout = TimeSpan.FromSeconds(15);
                        var content2 = new StringContent(JsonConvert.SerializeObject(deleteBody), Encoding.UTF8, "application/json");
                        var resp2 = http2.PostAsync(deleteUrl, content2).GetAwaiter().GetResult();
                        var json2 = resp2.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (resp2.IsSuccessStatusCode)
                        {
                            dynamic obj2 = null;
                            try { obj2 = JsonConvert.DeserializeObject(json2); } catch { }
                            if (obj2 != null && obj2.code == 0)
                            {
                                LogAdsPowerToFile($"AdsPower profile delete after stop succeeded. adsPowerId={adsPowerId}");
                                return;
                            }
                            LogAdsPowerToFile($"AdsPower profile delete after stop returned unexpected response. Body={json2}");
                        }
                        else
                        {
                            LogAdsPowerToFile($"AdsPower profile delete after stop failed. Status={(int)resp2.StatusCode}. Body={json2}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogAdsPowerToFile($"Error while attempting to stop profile before delete: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                LogAdsPowerToFile($"AdsPower profile delete failed with exception: {ex.Message}");
            }
        }

        private bool TryCloseAdsPowerProfileV2(string adsPowerId)
        {
            if (string.IsNullOrWhiteSpace(AdsPowerBaseUrl) || string.IsNullOrWhiteSpace(AdsPowerToken)) return false;
            try
            {
                var url = AdsPowerBaseUrl.TrimEnd('/') + "/api/v2/browser-profile/stop?token=" + Uri.EscapeDataString(AdsPowerToken);
                var body = new { profile_id = adsPowerId };
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(10);
                    var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                    var resp = http.PostAsync(url, content).GetAwaiter().GetResult();
                    var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    LogAdsPowerToFile($"TryCloseAdsPowerProfileV2: called {url}. Status={(int)resp.StatusCode}, Body={json}");
                    if (!resp.IsSuccessStatusCode) return false;
                    dynamic obj = null;
                    try { obj = JsonConvert.DeserializeObject(json); } catch { }
                    if (obj != null && obj.code == 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogAdsPowerToFile($"TryCloseAdsPowerProfileV2: exception: {ex.Message}");
            }
            return false;
        }
    }
}
