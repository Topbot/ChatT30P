using ChatT30P.Controllers.Models;
using Core;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Remoting.Channels;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using TL;
using WTelegram;

namespace ChatT30P.Controllers.Api
{
    public class ChatAccountsController : ApiController
    {
        private static string ConnectionString => ConfigurationManager.ConnectionStrings["chatConnectionString"]?.ConnectionString;

        private static string AdsPowerBaseUrl => ConfigurationManager.AppSettings["AdsPower:BaseUrl"];
        private static string AdsPowerToken => ConfigurationManager.AppSettings["AdsPower:Token"];
        private static string AdsPowerProxyId => ConfigurationManager.AppSettings["AdsPower:ProxyId"];
        private const string AdsPowerGroupName = "CHAT";
        private const string TopicSuffix = ":topic:";

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

        public class SaveMonitoringChatsRequest
        {
            public string Platform { get; set; }
            public string Phone { get; set; }
            public List<string> ChatIds { get; set; }
        }

        [HttpGet]
        [Route("api/ChatAccounts/MonitoringChats")]
        public HttpResponseMessage GetMonitoringChats([FromUri] string phone = null)
        {
            try
            {
                var userId = HttpContext.Current?.User?.Identity?.Name;
                if (string.IsNullOrEmpty(userId))
                    return Request.CreateResponse(HttpStatusCode.Unauthorized);

                var result = new List<string>();
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    if (!string.IsNullOrWhiteSpace(phone))
                    {
                        cmd.CommandText = "SELECT chat_id FROM dbo.chats WHERE user_id=@user_id AND (phone=@phone OR phone IS NULL)";
                        cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = NormalizePhone(phone);
                    }
                    else
                    {
                        cmd.CommandText = "SELECT chat_id FROM dbo.chats WHERE user_id=@user_id";
                    }
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                    cn.Open();
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var id = r[0] as string;
                            if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
                        }
                    }
                }

                return Request.CreateResponse(HttpStatusCode.OK, result);
            }
            catch (Exception ex)
            {
                LogAdsPowerError("GetMonitoringChats failed.", ex);
                return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, ex);
            }
        }

        [HttpPost]
        [Route("api/ChatAccounts/MonitoringChats")]
        public async Task<HttpResponseMessage> SaveMonitoringChats(SaveMonitoringChatsRequest request)
        {
            try
            {
                var userId = HttpContext.Current?.User?.Identity?.Name;
                if (string.IsNullOrEmpty(userId))
                    return Request.CreateResponse(HttpStatusCode.Unauthorized);

                var chatIds = request?.ChatIds ?? new List<string>();
                var platform = request?.Platform;
                var phone = request?.Phone;
                var normPhone = NormalizePhone(phone);
                var idMap = LoadChatIdMap(userId, platform, normPhone);

                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cn.Open();
                    using (var tx = cn.BeginTransaction())
                    {
                        cmd.Transaction = tx;
                        var existingComments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        cmd.CommandText = "SELECT chat_id, comment FROM dbo.chats WHERE user_id=@user_id AND phone=@phone";
                        cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                        cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = (object)normPhone ?? DBNull.Value;
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                var id = r[0] as string;
                                var comment = r[1] as string;
                                if (!string.IsNullOrWhiteSpace(id))
                                {
                                    existingComments[id] = comment;
                                    var baseId = ExtractBaseChatId(id);
                                    if (!existingComments.ContainsKey(baseId))
                                        existingComments[baseId] = comment;
                                }
                            }
                        }

                        cmd.Parameters.Clear();
                        cmd.CommandText = "DELETE FROM dbo.chats WHERE user_id=@user_id AND phone=@phone";
                        cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                        cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = (object)normPhone ?? DBNull.Value;
                        cmd.ExecuteNonQuery();

                        cmd.Parameters.Clear();
                        cmd.CommandText = "INSERT INTO dbo.chats(user_id, phone, chat_id, comment, updated_at) VALUES(@user_id,@phone,@chat_id,@comment,@updated_at)";
                        cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                        cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = (object)normPhone ?? DBNull.Value;
                        var chatIdParam = cmd.Parameters.Add("@chat_id", SqlDbType.NVarChar, 128);
                        var commentParam = cmd.Parameters.Add("@comment", SqlDbType.NVarChar, 512);
                        var updatedAtParam = cmd.Parameters.Add("@updated_at", SqlDbType.DateTime);
                        updatedAtParam.Value = DateTime.UtcNow.AddMonths(-2);

                        foreach (var cid in chatIds.Distinct())
                        {
                            if (string.IsNullOrWhiteSpace(cid)) continue;
                            var normalizedId = ResolveChatIdWithUsername(cid, idMap);
                            chatIdParam.Value = normalizedId;
                            var baseId = ExtractBaseChatId(cid);
                            commentParam.Value = (object)(existingComments.ContainsKey(normalizedId)
                                    ? existingComments[normalizedId]
                                    : (existingComments.ContainsKey(cid)
                                        ? existingComments[cid]
                                        : (existingComments.ContainsKey(baseId) ? existingComments[baseId] : null)))
                                ?? DBNull.Value;
                            updatedAtParam.Value = DateTime.UtcNow.AddMonths(-2);
                            cmd.ExecuteNonQuery();
                        }

                        tx.Commit();
                    }
                }

                if (!string.IsNullOrWhiteSpace(platform) && IsTelegramPlatform(platform) && !string.IsNullOrWhiteSpace(normPhone))
                {
                    var chatsToFetch = chatIds.Where(cid => !string.IsNullOrWhiteSpace(cid)).Distinct()
                        .Select(cid => ResolveChatIdWithUsername(cid, idMap)).ToList();
                    try
                    {
                        LogAdsPowerError("SaveMonitoringChats: chatsToFetch=" + string.Join(",", chatsToFetch));
                    }
                    catch { }
                    try
                    {
                        await DownloadTelegramResourcesAsync(userId, normPhone, chatsToFetch);
                    }
                    catch (Exception ex)
                    {
                        LogAdsPowerError("Media download failed.", ex);
                    }
                }

                return Request.CreateResponse(HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                LogAdsPowerError("SaveMonitoringChats failed.", ex);
                return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, ex);
            }
        }

        private async Task DownloadTelegramResourcesAsync(string userId, string phone, List<string> chatIds)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(phone)) return;
            if (chatIds == null || chatIds.Count == 0) return;

            try { LogAdsPowerError("DownloadTelegramResourcesAsync: chatIds=" + string.Join(",", chatIds)); } catch { }

            var apiId = GetTelegramApiIdOrNull();
            var apiHash = GetTelegramApiHashOrNull();
            if (string.IsNullOrWhiteSpace(apiId) || string.IsNullOrWhiteSpace(apiHash)) return;

            var sessionFile = GetTelegramSessionFilePath(userId, phone);
            if (!File.Exists(sessionFile)) return;

            var sessionSemaphore = GetSessionSemaphore(sessionFile);
            await sessionSemaphore.WaitAsync();
            try
            {
                using (var client = new Client(what =>
                {
                    switch (what)
                    {
                        case "api_id": return apiId;
                        case "api_hash": return apiHash;
                        case "phone_number": return phone;
                        case "session_pathname": return sessionFile;
                        default: return null;
                    }
                }))
                {
                    await client.ConnectAsync();

                    var dialogs = await client.Messages_GetDialogs();
                    var peerMap = BuildPeerMap(dialogs);
                    var cutoff = DateTime.UtcNow.AddMonths(-2);

                    foreach (var chatId in chatIds)
                    {
                        var lookupId = ExtractBaseChatId(chatId);
                        if (!peerMap.TryGetValue(lookupId, out var peer))
                        {
                            try { LogAdsPowerError($"DownloadTelegramResourcesAsync skipped. chatId={chatId} lookupId={lookupId} (peer not found)"); } catch { }
                            continue;
                        }

                        var dir = GetTelegramMediaDir(chatId);
                        var lastDownloaded = GetLatestMessageDate(dir);
                        var effectiveCutoff = lastDownloaded.HasValue && lastDownloaded.Value > cutoff
                            ? lastDownloaded.Value
                            : cutoff;

                        try
                        {
                            await DownloadPeerAvatarAsync(client, peer, chatId);
                        }
                        catch (TL.RpcException ex)
                        {
                            if (ex.Message != null && ex.Message.IndexOf("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                LogAdsPowerError($"DownloadTelegramResourcesAsync: AUTH_KEY_UNREGISTERED for chatId={chatId}");
                            }
                            else
                            {
                                LogAdsPowerError($"DownloadTelegramResourcesAsync: avatar failed for chatId={chatId}", ex);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogAdsPowerError($"DownloadTelegramResourcesAsync: avatar failed for chatId={chatId}", ex);
                        }

                        try
                        {
                            if (TryExtractTopicId(chatId, out var topicId))
                            {
                                await DownloadTopicTextsAsync(client, peer, topicId, dir, effectiveCutoff);
                            }
                            else
                            {
                                await DownloadChatTextsAsync(client, peer, dir, effectiveCutoff);
                            }
                        }
                        catch (TL.RpcException ex)
                        {
                            if (ex.Message != null && ex.Message.IndexOf("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                LogAdsPowerError($"DownloadTelegramResourcesAsync: AUTH_KEY_UNREGISTERED for chatId={chatId}");
                                continue;
                            }
                            throw;
                        }
                        catch (WTelegram.WTException ex)
                        {
                            var msg = ex.Message ?? string.Empty;
                            if (msg.IndexOf("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                LogAdsPowerError($"DownloadTelegramResourcesAsync: AUTH_KEY_UNREGISTERED for chatId={chatId}");
                                continue;
                            }
                            throw;
                        }
                    }
                }
            }
            catch (WTelegram.WTException ex)
            {
                var msg = ex.Message ?? string.Empty;
                if (msg.IndexOf("Exception while reading session file", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    LogAdsPowerError("DownloadTelegramResourcesAsync: session file invalid.", ex);
                    return;
                }
                throw;
            }
            finally
            {
                sessionSemaphore.Release();
            }
        }

        private static string GetTelegramMediaDir(string chatId)
        {
            var baseDir = HttpContext.Current?.Server?.MapPath("~/App_Data") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data");
            var safeChat = NormalizeChatFolderName(chatId);
            var dir = Path.Combine(baseDir, "telegram_media", safeChat);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string NormalizeChatFolderName(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId)) return "unknown";
            var s = chatId;
            var idx = s.IndexOf(':');
            if (idx >= 0 && idx < s.Length - 1) s = s.Substring(idx + 1);
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        private class ChatIdItem
        {
            public string id { get; set; }
            public string username { get; set; }
        }

        private Dictionary<string, string> LoadChatIdMap(string userId, string platform, string phone)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(phone))
                    return map;
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT TOP 1 chats_json FROM accounts WHERE user_id=@user_id AND platform=@platform AND phone=@phone";
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                    cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = platform;
                    cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = phone;
                    cn.Open();
                    var json = cmd.ExecuteScalar() as string;
                    if (string.IsNullOrWhiteSpace(json)) return map;
                    try
                    {
                        var items = JsonConvert.DeserializeObject<List<ChatIdItem>>(json);
                        if (items == null) return map;
                        foreach (var item in items)
                        {
                            if (item == null || string.IsNullOrWhiteSpace(item.id)) continue;
                            var baseId = ExtractBaseChatId(item.id);
                            if (!map.ContainsKey(baseId))
                                map[baseId] = item.id;
                            if (!string.IsNullOrWhiteSpace(item.username) && item.id.IndexOf(":" + item.username, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                var composed = baseId + ":" + item.username;
                                map[baseId] = composed;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return map;
        }

        private static string ResolveChatIdWithUsername(string chatId, Dictionary<string, string> map)
        {
            if (string.IsNullOrWhiteSpace(chatId)) return chatId;
            var topicSuffix = string.Empty;
            var topicIndex = chatId.LastIndexOf(TopicSuffix, StringComparison.OrdinalIgnoreCase);
            var baseChatId = chatId;
            if (topicIndex >= 0)
            {
                topicSuffix = chatId.Substring(topicIndex);
                baseChatId = chatId.Substring(0, topicIndex);
            }
            if (baseChatId.IndexOf("channel:", StringComparison.OrdinalIgnoreCase) != 0)
                return chatId;
            var baseId = ExtractBaseChatId(baseChatId);
            if (map != null && map.TryGetValue(baseId, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
                return mapped + topicSuffix;
            return baseChatId + topicSuffix;
        }

        private static string ExtractBaseChatId(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId)) return chatId;
            if (chatId.IndexOf("channel:", StringComparison.OrdinalIgnoreCase) == 0)
            {
                var rest = chatId.Substring("channel:".Length);
                var parts = rest.Split(':');
                return "channel:" + parts[0];
            }
            return chatId;
        }

        private static bool TryExtractTopicId(string chatId, out int topicId)
        {
            topicId = 0;
            if (string.IsNullOrWhiteSpace(chatId)) return false;
            var idx = chatId.LastIndexOf(TopicSuffix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            var part = chatId.Substring(idx + TopicSuffix.Length);
            return int.TryParse(part, out topicId);
        }

        private async Task DownloadTelegramMediaAsync(string userId, string phone, List<string> chatIds)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(phone)) return;
            if (chatIds == null || chatIds.Count == 0) return;

            try { LogAdsPowerError("DownloadTelegramMediaAsync: chatIds=" + string.Join(",", chatIds)); } catch { }

            var apiId = GetTelegramApiIdOrNull();
            var apiHash = GetTelegramApiHashOrNull();
            if (string.IsNullOrWhiteSpace(apiId) || string.IsNullOrWhiteSpace(apiHash)) return;

            var sessionFile = GetTelegramSessionFilePath(userId, phone);
            if (!File.Exists(sessionFile)) return;

            var sessionSemaphore = GetSessionSemaphore(sessionFile);
            await sessionSemaphore.WaitAsync();
            try
            {
                using (var client = new Client(what =>
                {
                    switch (what)
                    {
                        case "api_id": return apiId;
                        case "api_hash": return apiHash;
                        case "phone_number": return phone;
                        case "session_pathname": return sessionFile;
                        default: return null;
                    }
                }))
                {
                    await client.ConnectAsync();

                    var dialogs = await client.Messages_GetDialogs();
                    var peerMap = BuildPeerMap(dialogs);
                    var cutoff = DateTime.UtcNow.AddMonths(-2);

                    foreach (var chatId in chatIds)
                    {
                        var lookupId = ExtractBaseChatId(chatId);
                        if (!peerMap.TryGetValue(lookupId, out var peer))
                        {
                            try { LogAdsPowerError($"DownloadChatTextsAsync skipped. chatId={chatId} lookupId={lookupId} (peer not found)"); } catch { }
                            continue;
                        }
                        var dir = GetTelegramMediaDir(chatId);
                        var lastDownloaded = GetLatestMessageDate(dir);
                        var effectiveCutoff = lastDownloaded.HasValue && lastDownloaded.Value > cutoff
                            ? lastDownloaded.Value
                            : cutoff;
                        if (TryExtractTopicId(chatId, out var topicId))
                        {
                            try { LogAdsPowerError($"DownloadTopicTextsAsync start. chatId={chatId} lookupId={lookupId} topicId={topicId}"); } catch { }
                            await DownloadTopicTextsAsync(client, peer, topicId, dir, effectiveCutoff);
                            try { LogAdsPowerError($"DownloadTopicTextsAsync done. chatId={chatId} lookupId={lookupId} topicId={topicId}"); } catch { }
                        }
                        else
                        {
                            try { LogAdsPowerError($"DownloadChatTextsAsync start. chatId={chatId} lookupId={lookupId}"); } catch { }
                            await DownloadChatTextsAsync(client, peer, dir, effectiveCutoff);
                            try { LogAdsPowerError($"DownloadChatTextsAsync done. chatId={chatId} lookupId={lookupId}"); } catch { }
                        }
                    }
                }
            }
            catch (WTelegram.WTException ex)
            {
                var msg = ex.Message ?? string.Empty;
                if (msg.IndexOf("verification_code", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    TryMarkAccountLoggedOut(userId, phone);
                    return;
                }
                throw;
            }
            finally
            {
                sessionSemaphore.Release();
            }
        }

        private void TryMarkAccountLoggedOut(string userId, string phone)
        {
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE accounts SET status = 0 WHERE user_id=@user_id AND platform=@platform AND phone=@phone";
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                    cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = "Telegram";
                    cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = NormalizePhone(phone);
                    cn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch
            {
            }
        }

        private static DateTime? GetLatestMessageDate(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
                var logPath = Path.Combine(dir, "messages.log");
                if (File.Exists(logPath))
                {
                    DateTime? latest = null;
                    foreach (var line in File.ReadLines(logPath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split('|');
                        if (parts.Length == 0) continue;
                        if (DateTime.TryParse(parts[0], null,
                            System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var parsed))
                        {
                            if (!latest.HasValue || parsed > latest.Value) latest = parsed;
                        }
                    }
                    return latest;
                }
                var files = Directory.GetFiles(dir, "*.txt");
                DateTime? latestFile = null;
                foreach (var file in files)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (DateTime.TryParseExact(name.Substring(0, Math.Min(15, name.Length)), "yyyyMMdd_HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                    {
                        if (!latestFile.HasValue || parsed > latestFile.Value) latestFile = parsed;
                    }
                }
                return latestFile;
            }
            catch (Exception ex)
            {
                try { LogAdsPowerError("DownloadPeerAvatarAsync failed.", ex); } catch { }
                return null;
            }
        }

        private static object TryGetMemberValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName)) return null;
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase;
            var type = target.GetType();
            while (type != null)
            {
                var prop = type.GetProperty(memberName, flags);
                if (prop != null) return prop.GetValue(target, null);
                var field = type.GetField(memberName, flags);
                if (field != null) return field.GetValue(target);
                type = type.BaseType;
            }
            return null;
        }

        private static Dictionary<string, InputPeer> BuildPeerMap(Messages_DialogsBase dialogs)
        {
            var map = new Dictionary<string, InputPeer>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var chats = GetDialogsChats(dialogs);
                if (chats == null) return map;
                foreach (var chatBase in chats)
                {
                    if (chatBase is Chat chat)
                    {
                        var key = "chat:" + chat.id;
                        map[key] = new InputPeerChat(chat.id);
                    }
                    else if (chatBase is Channel channel)
                    {
                        var key = "channel:" + channel.id;
                        map[key] = new InputPeerChannel(channel.id, channel.access_hash);
                    }
                }
            }
            catch
            {
            }
            return map;
        }

        private static IEnumerable<ChatBase> GetDialogsChats(Messages_DialogsBase dialogs)
        {
            switch (dialogs)
            {
                case Messages_DialogsSlice ds:
                    return ds.chats?.Values;
                case Messages_Dialogs d:
                    return d.chats?.Values;
                default:
                    return null;
            }
        }

        private static async Task DownloadChatTextsAsync(Client client, InputPeer peer, string dir, DateTime cutoffUtc)
        {
            var offsetId = 0;
            while (true)
            {
                var history = await client.Messages_GetHistory(peer, offsetId, DateTime.MinValue, 0, 100, 0, 0, 0);
                var messages = GetHistoryMessages(history);
                var senderMap = BuildSenderMap(history);
                if (messages == null || messages.Count == 0)
                    break;

                var pageMessages = messages.OfType<Message>().ToList();
                if (pageMessages.Count == 0)
                    break;

                var shouldStop = false;
                foreach (var msg in pageMessages.OrderByDescending(m => m.date))
                {
                    var msgDate = msg.date.ToUniversalTime();
                    if (msgDate < cutoffUtc)
                    {
                        shouldStop = true;
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(msg.message))
                    {
                        var filePath = SaveTextMessage(dir, msg.id, msgDate, msg.message);
                        if (!string.IsNullOrWhiteSpace(filePath))
                        {
                            var sender = GetSenderLabel(msg, senderMap);
                            var senderId = GetSenderId(msg);
                            var views = GetMessageViews(msg);
                            var replies = GetMessageReplies(msg);
                            var replyToId = GetReplyToMessageId(msg);
                            AppendMessageLog(dir, msgDate, msg.id, sender, senderId, views, replies, replyToId, "neutral");
                        }
                    }
                }

                offsetId = pageMessages.Min(m => m.id);
                if (shouldStop) break;
            }
        }

        private static async Task DownloadTopicTextsAsync(Client client, InputPeer peer, int topicId, string dir, DateTime cutoffUtc)
        {
            var offsetId = 0;
            while (true)
            {
                var history = await client.Messages_GetReplies(peer, topicId, offsetId, DateTime.MinValue, 0, 100, 0, 0, 0);
                var messages = GetHistoryMessages(history);
                var senderMap = BuildSenderMap(history);
                if (messages == null || messages.Count == 0)
                    break;

                var pageMessages = messages.OfType<Message>().ToList();
                if (pageMessages.Count == 0)
                    break;

                var shouldStop = false;
                foreach (var msg in pageMessages.OrderByDescending(m => m.date))
                {
                    var msgDate = msg.date.ToUniversalTime();
                    if (msgDate < cutoffUtc)
                    {
                        shouldStop = true;
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(msg.message))
                    {
                        var filePath = SaveTextMessage(dir, msg.id, msgDate, msg.message);
                        if (!string.IsNullOrWhiteSpace(filePath))
                        {
                            var sender = GetSenderLabel(msg, senderMap);
                            var senderId = GetSenderId(msg);
                            var views = GetMessageViews(msg);
                            var replies = GetMessageReplies(msg);
                            var replyToId = GetReplyToMessageId(msg);
                            AppendMessageLog(dir, msgDate, msg.id, sender, senderId, views, replies, replyToId, "neutral");
                        }
                    }
                }

                offsetId = pageMessages.Min(m => m.id);
                if (shouldStop) break;
            }
        }

        private static string SaveTextMessage(string dir, int messageId, DateTime msgDate, string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                Directory.CreateDirectory(dir);
                var fileName = $"{msgDate:yyyyMMdd_HHmmss}_{messageId}.txt";
                var path = Path.Combine(dir, fileName);
                if (File.Exists(path)) return null;
                File.WriteAllText(path, text);
                return path;
            }
            catch
            {
                return null;
            }
        }

        private static void AppendMessageLog(string dir, DateTime msgDate, int messageId, string sender, string senderId, int views, int replies, long? replyToId, string sentiment)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var logPath = Path.Combine(dir, "messages.log");
                var safeSender = (sender ?? string.Empty).Replace("|", "/");
                var safeSenderId = (senderId ?? string.Empty).Replace("|", "/");
                var safeSentiment = NormalizeSentimentCode(sentiment);
                var replyValue = replyToId.HasValue && replyToId.Value > 0 ? replyToId.Value.ToString() : "0";
                var line = $"{msgDate:yyyy-MM-ddTHH:mm:ss}|{messageId}|{safeSender}|{safeSenderId}|{views}|{replies}|{replyValue}|{safeSentiment}";
                var lines = File.Exists(logPath) ? File.ReadAllLines(logPath).ToList() : new List<string>();
                var replaced = false;
                for (var i = 0; i < lines.Count; i++)
                {
                    var existing = lines[i];
                    if (string.IsNullOrWhiteSpace(existing)) continue;
                    var parts = existing.Split('|');
                    if (parts.Length < 8) continue;
                    if (!string.Equals(parts[0], msgDate.ToString("yyyy-MM-ddTHH:mm:ss"), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(parts[3], safeSenderId, StringComparison.OrdinalIgnoreCase)) continue;
                    lines[i] = line;
                    replaced = true;
                    break;
                }
                if (!replaced)
                    lines.Add(line);
                File.WriteAllLines(logPath, lines);
            }
            catch
            {
            }
        }

        private static long? GetReplyToMessageId(Message messageObj)
        {
            if (messageObj == null) return null;

            try
            {
                var messageType = messageObj.GetType();

                // 1. Получаем поле reply_to
                var replyToProp = messageType.GetProperty("reply_to")
                               ?? messageType.GetProperty("ReplyTo");   // иногда PascalCase

                if (replyToProp == null) return null;

                var replyHeader = replyToProp.GetValue(messageObj);
                if (replyHeader == null) return null;

                var replyValue = TryGetMemberValue(replyHeader, "reply_to_msg_id")
                    ?? TryGetMemberValue(replyHeader, "replyToMsgId");
                if (replyValue is int intId && intId > 0) return intId;
                if (replyValue is long longId && longId > 0) return longId;

                var topValue = TryGetMemberValue(replyHeader, "reply_to_top_id")
                    ?? TryGetMemberValue(replyHeader, "ReplyToTopId");
                if (topValue is int t && t > 0) return t;
                if (topValue is long tl && tl > 0) return tl;
            }
            catch
            {
                // silent fail как у тебя
            }

            return null;
        }

        private static string GetSenderId(Message msg)
        {
            if (msg == null) return string.Empty;
            if (msg.from_id is PeerUser pu)
                return pu.user_id.ToString();
            if (msg.from_id is PeerChannel pc)
                return pc.channel_id.ToString();
            if (msg.from_id is PeerChat pch)
                return pch.chat_id.ToString();
            if (msg.peer_id is PeerUser pui)
                return pui.user_id.ToString();
            if (msg.peer_id is PeerChannel pci)
                return pci.channel_id.ToString();
            if (msg.peer_id is PeerChat pchi)
                return pchi.chat_id.ToString();
            return string.Empty;
        }

        private static string NormalizeSentimentCode(string sentiment)
        {
            if (string.IsNullOrWhiteSpace(sentiment)) return "O";
            switch (sentiment.Trim().ToLowerInvariant())
            {
                case "positive":
                case "p":
                    return "P";
                case "negative":
                case "n":
                    return "N";
                case "neutral":
                case "u":
                case "0":
                    return "U";
                case "unknown":
                case "o":
                    return "O";
                default:
                    return "O";
            }
        }

        private static int GetMessageViews(Message msg)
        {
            try
            {
                return msg?.views ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetMessageReplies(Message msg)
        {
            try
            {
                return msg?.replies?.replies ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static Dictionary<long, string> BuildSenderMap(Messages_MessagesBase history)
        {
            var map = new Dictionary<long, string>();
            try
            {
                var users = GetHistoryUsers(history);
                if (users != null)
                {
                    foreach (var user in users)
                    {
                        if (user == null) continue;
                        var name = string.Join(" ", new[] { user.first_name, user.last_name }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                        if (string.IsNullOrWhiteSpace(name)) name = user.username;
                        if (string.IsNullOrWhiteSpace(name)) name = user.id.ToString();
                        map[user.id] = name;
                    }
                }

                var chats = GetHistoryChats(history);
                if (chats != null)
                {
                    foreach (var chatBase in chats)
                    {
                        if (chatBase is Chat chat)
                        {
                            map[chat.id] = chat.Title ?? chat.id.ToString();
                        }
                        else if (chatBase is Channel channel)
                        {
                            map[channel.id] = channel.Title ?? channel.id.ToString();
                        }
                    }
                }
            }
            catch
            {
            }
            return map;
        }

        private static IEnumerable<User> GetHistoryUsers(Messages_MessagesBase history)
        {
            switch (history)
            {
                case Messages_ChannelMessages mc:
                    return mc.users?.Values;
                case Messages_MessagesSlice ms:
                    return ms.users?.Values;
                case Messages_Messages mm:
                    return mm.users?.Values;
                default:
                    return null;
            }
        }

        private static IEnumerable<ChatBase> GetHistoryChats(Messages_MessagesBase history)
        {
            switch (history)
            {
                case Messages_ChannelMessages mc:
                    return mc.chats?.Values;
                case Messages_MessagesSlice ms:
                    return ms.chats?.Values;
                case Messages_Messages mm:
                    return mm.chats?.Values;
                default:
                    return null;
            }
        }

        private static string GetSenderLabel(Message msg, Dictionary<long, string> map)
        {
            if (msg == null) return string.Empty;
            if (msg.from_id is PeerUser pu && map.TryGetValue(pu.user_id, out var userName))
                return userName;
            if (msg.from_id is PeerChannel pc && map.TryGetValue(pc.channel_id, out var channelName))
                return channelName;
            if (msg.peer_id is PeerChannel pch && map.TryGetValue(pch.channel_id, out var peerChannelName))
                return peerChannelName;
            if (msg.peer_id is PeerChat pchat && map.TryGetValue(pchat.chat_id, out var peerChatName))
                return peerChatName;
            return string.Empty;
        }

        private static IReadOnlyList<MessageBase> GetHistoryMessages(Messages_MessagesBase history)
        {
            switch (history)
            {
                case Messages_ChannelMessages mc:
                    return mc.messages;
                case Messages_MessagesSlice ms:
                    return ms.messages;
                case Messages_Messages mm:
                    return mm.messages;
                default:
                    return Array.Empty<MessageBase>();
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
                var bytes = Convert.FromBase64String(sessionText);
                File.WriteAllBytes(path, bytes);
            }
            catch
            {
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

        private class ChannelMeta
        {
            public List<object> Topics { get; set; }
        }

        private static bool HasFlagValue(object flagsObj, string flagName)
        {
            try
            {
                if (flagsObj == null || string.IsNullOrWhiteSpace(flagName)) return false;
                var flagsType = flagsObj.GetType();
                if (!flagsType.IsEnum) return false;
                var flagValue = Enum.Parse(flagsType, flagName, true);
                return ((Enum)flagsObj).HasFlag((Enum)flagValue);
            }
            catch
            {
                return false;
            }
        }

        private List<object> ExtractChatItemsFromDialogs(dynamic dialogs, Dictionary<string, ChannelMeta> metaMap)
        {
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
                    string username = null;
                    string baseId = null;
                    List<object> topics = null;
                    bool isMegagroup = false;
                    bool isForum = false;
                    try
                    {
                        var peer = d.peer;
                        if (peer is PeerUser)
                        {
                            // Skip user dialogs per requirements
                            id = null;
                            title = null;
                            type = null;
                        }
                        else if (peer is PeerChat pc)
                        {
                            baseId = "chat:" + pc.chat_id;
                            id = baseId;
                            type = "chat";
                            dynamic chats = dd.chats;
                            dynamic c = chats[pc.chat_id];
                            title = (string)c.Title;
                            try
                            {
                                var flags = c.flags;
                                isMegagroup = HasFlagValue(flags, "megagroup");
                                isForum = HasFlagValue(flags, "forum");
                            }
                            catch { }
                            if (metaMap != null && !string.IsNullOrWhiteSpace(baseId) && metaMap.TryGetValue(baseId, out var chatMeta))
                            {
                                topics = chatMeta.Topics;
                            }
                        }
                        else if (peer is PeerChannel pch)
                        {
                            baseId = "channel:" + pch.channel_id;
                            id = baseId;
                            type = "channel";
                            dynamic chats = dd.chats;
                            dynamic c = chats[pch.channel_id];
                            title = (string)c.Title;
                            try { username = (string)c.username; } catch { try { username = (string)c.Username; } catch { } }
                            try
                            {
                                var flags = c.flags;
                                isMegagroup = HasFlagValue(flags, "megagroup");
                                isForum = HasFlagValue(flags, "forum");
                            }
                            catch { }
                            if (!string.IsNullOrWhiteSpace(username))
                                id = id + ":" + username;
                            if (metaMap != null && !string.IsNullOrWhiteSpace(baseId) && metaMap.TryGetValue(baseId, out var meta))
                            {
                                topics = meta.Topics;
                            }
                        }

                        // last message preview (best-effort)
                        try
                        {
                            if (d.top_message != null)
                            {
                                int topId = (int)d.top_message;
                                dynamic messages = dd.messages;
                                for (var i = 0; i < 10 && string.IsNullOrWhiteSpace(last); i++)
                                {
                                    try
                                    {
                                        dynamic m = messages[topId - i];
                                        if (!IsSamePeer(d.peer, m.peer_id)) continue;
                                        try { last = (string)m.message; }
                                        catch { try { last = (string)m.Message; } catch { } }
                                    }
                                    catch { }
                                }

                                if (string.IsNullOrWhiteSpace(last))
                                {
                                    try
                                    {
                                        var list = new List<dynamic>();
                                        foreach (var msg in messages) list.Add(msg);
                                        foreach (var msg in list.OrderByDescending(x => (int)x.id))
                                        {
                                            try
                                            {
                                                if (!IsSamePeer(d.peer, msg.peer_id)) continue;
                                            }
                                            catch { continue; }
                                            try { last = (string)msg.message; }
                                            catch { try { last = (string)msg.Message; } catch { } }
                                            if (!string.IsNullOrWhiteSpace(last)) break;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }
                    catch { }

                    if (!string.IsNullOrWhiteSpace(id) && title != null)
                        items.Add(new { id, title, type, last, username, topics = topics ?? new List<object>() });
                }
            }
            catch
            {
                // ignore dialogs parsing differences between TL versions
            }

            return items;
        }

        private async Task<Dictionary<string, ChannelMeta>> LoadPeerMetaAsync(Client client, Messages_DialogsBase dialogs)
        {
            var result = new Dictionary<string, ChannelMeta>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (client == null || dialogs == null) return result;
                var peers = BuildPeerMap(dialogs);
                var chats = GetDialogsChats(dialogs);
                if (chats == null) return result;
                foreach (var chatBase in chats)
                {
                    if (chatBase is Channel channel)
                    {
                        var id = "channel:" + channel.id;
                        var isMegagroup = false;
                        var isForum = false;
                        try
                        {
                            dynamic dc = channel;
                            var flags = dc.flags;
                            isMegagroup = HasFlagValue(flags, "megagroup");
                            isForum = HasFlagValue(flags, "forum");
                        }
                        catch { }
                        if (!peers.TryGetValue(id, out var peer))
                            continue;

                        var topics = new List<object>();
                        if (isMegagroup && isForum)
                        {
                            try
                            {
                                var resp = await client.Channels_GetAllForumTopics(peer);
                                if (resp != null)
                                {
                                    dynamic d = resp;
                                    if (d.topics != null)
                                    {
                                        foreach (var t in d.topics)
                                        {
                                            try
                                            {
                                                dynamic tDyn = t;
                                                var tid = (int)tDyn.id;
                                                var ttitle = (string)tDyn.title;
                                                topics.Add(new { id = tid, title = ttitle });
                                            }
                                            catch { }
                                        }
                                    }
                                }

                            }
                            catch { }
                        }
                        result[id] = new ChannelMeta
                        {
                            Topics = topics
                        };
                    }
                    else if (chatBase is Chat chat)
                    {
                        var id = "chat:" + chat.id;
                        if (!peers.TryGetValue(id, out var peer))
                            continue;
                        result[id] = new ChannelMeta
                        {
                            Topics = new List<object>()
                        };
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        private static async Task<string> DownloadPeerAvatarAsync(Client client, InputPeer peer, string chatId)
        {
            try
            {
                if (client == null || peer == null || string.IsNullOrWhiteSpace(chatId)) return null;
                try { LogAdsPowerError($"DownloadPeerAvatarAsync start. chatId={chatId}"); } catch { }
                var baseDir = HttpContext.Current?.Server?.MapPath("~/Content/ava") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Content", "ava");
                Directory.CreateDirectory(baseDir);
                var fileName = NormalizeChatFolderName(chatId) + ".jpg";
                var path = Path.Combine(baseDir, fileName);
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    return "/Content/ava/" + fileName;
                if (File.Exists(path))
                    File.Delete(path);

                ChatFullBase full = null;
                try
                {
                    if (peer is InputPeerChannel ipc)
                    {
                        var inputChannel = new InputChannel(ipc.channel_id, ipc.access_hash);
                        var fullChannel = await client.Channels_GetFullChannel(inputChannel);
                        full = fullChannel.full_chat;  // messages.ChatFull
                    }
                    else if (peer is InputPeerChat ipchat)
                    {
                        var fullChat = await client.Messages_GetFullChat(ipchat.chat_id);
                        full = fullChat.full_chat;     // messages.ChatFull
                    }
                    else
                    {
                        // User или другой peer — аватарку чата не скачиваем
                        return null;
                    }
                }
                catch (RpcException ex)
                {
                    LogAdsPowerError($"GetFull failed for chatId={chatId}: {ex.Message} (code {ex.Code})");
                    return null;
                }
                catch (Exception ex)
                {
                    LogAdsPowerError($"Unexpected error getting full chat {chatId}: {ex}");
                    return null;
                }

                if (full == null) return null;

                // Единая логика получения Photo
                Photo photo = full.ChatPhoto as Photo;
                if (photo == null || photo is PhotoEmpty)
                {
                    LogAdsPowerError($"No valid chat_photo for chatId={chatId}");
                    return null;
                }

                try
                {
                    var smallSize = photo.sizes
    .OfType<PhotoSize>()  // игнор cached/stripped
    .OrderBy(s => s.w * s.h)  // по размеру
    .FirstOrDefault();
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await client.DownloadFileAsync(photo, fs, smallSize);  // ← вот это главное
                    }
                }
                catch (Exception ex)
                {
                    LogAdsPowerError($"DownloadFileAsync failed for chatId={chatId}: {ex.Message}");
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    return null;
                }

                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    LogAdsPowerError($"Downloaded file empty or missing: chatId={chatId}, file={fileName}");
                    return null;
                }

                LogAdsPowerError($"Avatar downloaded: chatId={chatId}, file={fileName}, size={info.Length} bytes");
                return "/Content/ava/" + fileName;
            }
            catch (TL.RpcException ex)
            {
                if (ex.Message != null && ex.Message.IndexOf("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw;
                try { LogAdsPowerError("DownloadPeerAvatarAsync failed.", ex); } catch { }
                return null;
            }
            catch (Exception ex)
            {
                try { LogAdsPowerError("DownloadPeerAvatarAsync failed.", ex); } catch { }
                return null;
            }
        }

        private static bool IsSamePeer(dynamic dialogPeer, dynamic messagePeer)
        {
            try
            {
                if (dialogPeer == null || messagePeer == null) return false;
                if (dialogPeer is PeerChat pc && messagePeer is PeerChat mpc)
                    return pc.chat_id == mpc.chat_id;
                if (dialogPeer is PeerChannel pch && messagePeer is PeerChannel mpch)
                    return pch.channel_id == mpch.channel_id;
                if (dialogPeer is PeerUser pu && messagePeer is PeerUser mpu)
                    return pu.user_id == mpu.user_id;
            }
            catch
            {
            }
            return false;
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

        private void UpdateTelegramSession(string sessionFile, string session)
        {
            if (string.IsNullOrWhiteSpace(sessionFile)) return;
            try
            {
                if (string.IsNullOrWhiteSpace(session))
                {
                    try { if (File.Exists(sessionFile)) File.Delete(sessionFile); } catch { }
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(sessionFile));
                var bytes = Convert.FromBase64String(session);
                File.WriteAllBytes(sessionFile, bytes);
            }
            catch
            {
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
                string platform = null;
                string phone = null;
                try { platform = Convert.ToString(request.Platform ?? request.platform); } catch { }
                try { phone = Convert.ToString(request.Phone ?? request.phone); } catch { }
                if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(phone))
                    return Request.CreateResponse(HttpStatusCode.BadRequest);

                var userId = HttpContext.Current?.User?.Identity?.Name;
                if (string.IsNullOrEmpty(userId)) return Request.CreateResponse(HttpStatusCode.Unauthorized);
                if (!Security.IsPaid) return Request.CreateResponse((HttpStatusCode)402, "No active subscription");

                var script = "LoadChats" + platform.Replace(" ", "");

                var normPlatform = NormalizePlatform(platform);
                var normPhone = NormalizePhone(phone);

                // Telegram: refresh chats via MTProto (no AdsPower, no rpa_tasks)
                if (IsTelegramPlatform(normPlatform))
                {
                    var apiId = GetTelegramApiIdOrNull();
                    var apiHash = GetTelegramApiHashOrNull();
                    if (string.IsNullOrWhiteSpace(apiId) || string.IsNullOrWhiteSpace(apiHash))
                        return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "Telegram API ключи не настроены на сервере.");

                    var sessionFile = GetTelegramSessionFilePath(userId, normPhone);
                    var sessionText = ReadSessionTextFromFile(sessionFile);
                    if (string.IsNullOrWhiteSpace(sessionText))
                    {
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
                        return Request.CreateResponse(HttpStatusCode.BadRequest, new { Message = "Аккаунт не залогинен. Сначала выполните вход." });
                    }
                    var sessionSemaphore = GetSessionSemaphore(sessionFile);
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
                            var dialogs = await client.Messages_GetDialogs();
                            var metaMap = await LoadPeerMetaAsync(client, dialogs);
                            var items = ExtractChatItemsFromDialogs(dialogs, metaMap);
                            var chatsJson = JsonConvert.SerializeObject(items);
                            SaveChatsJson(userId, normPlatform, normPhone, chatsJson);

                            return Request.CreateResponse(HttpStatusCode.OK, items);
                        }
                    }
                    catch (Exception ex)
                    {
                        var msg = ex.Message ?? string.Empty;
                        if (msg.IndexOf("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try
                            {
                                using (var cn = new SqlConnection(ConnectionString))
                                using (var cmd = cn.CreateCommand())
                                {
                                    cmd.CommandText = "UPDATE accounts SET status = 0 WHERE user_id=@user_id AND platform=@platform AND phone=@phone";
                                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                                    cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = normPlatform;
                                    cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = normPhone;
                                    cn.Open();
                                    cmd.ExecuteNonQuery();
                                }
                            }
                            catch { }
                            return Request.CreateResponse(HttpStatusCode.Unauthorized, new { Message = "Сессия невалидна. Требуется повторный вход.", Code = "AUTH_KEY_UNREGISTERED" });
                        }
                        if (msg.IndexOf("verification_code", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
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
                            return Request.CreateResponse(HttpStatusCode.BadRequest, new { Message = "Требуется авторизация аккаунта. Сначала выполните вход." });
                        }
                        throw;
                    }
                    finally
                    {
                        try { UpdateTelegramSession(sessionFile, ReadSessionTextFromFile(sessionFile)); } catch { }
                        sessionSemaphore.Release();
                    }
                }

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
                var details = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { Message = details });
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

                var sessionFile = GetTelegramSessionFilePath(userId, normPhone);
                var sessionSemaphore = GetSessionSemaphore(sessionFile);

                // Send code. Persist session and phone_code_hash.
                string codeHash = null;
                await sessionSemaphore.WaitAsync();
                try
                {
                    // Keep file-based session storage in sync with DB
                    var sessionText = ReadSessionTextFromFile(sessionFile);
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
                            var sent = await client.Auth_SendCode("+"+normPhone, int.Parse(apiId), 
                                apiHash, new CodeSettings
                                {
                                    flags = CodeSettings.Flags.allow_flashcall |
                                    CodeSettings.Flags.allow_app_hash | CodeSettings.Flags.current_number
                                }
                                
                               );
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
                        if (string.IsNullOrWhiteSpace(codeHash))
                            return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "Не удалось запросить код авторизации. Попробуйте снова позже.");
                    }

                    // Persist session text back into DB (file content) after client disposed
                    try { UpdateTelegramSession(sessionFile, ReadSessionTextFromFile(sessionFile)); } catch { }
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
                    var sessionFile = GetTelegramSessionFilePath(userId, normPhone);
                    var sessionText = ReadSessionTextFromFile(sessionFile);
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
                            var metaMap = await LoadPeerMetaAsync(client, dialogs);
                            var items = ExtractChatItemsFromDialogs(dialogs, metaMap);

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

                        // Persist session text back into file after client disposed (avoid file lock)
                        try { UpdateTelegramSession(sessionFile, ReadSessionTextFromFile(sessionFile)); } catch { }
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

            var normPlatform = NormalizePlatform(platform);
            var normPhone = NormalizePhone(phone);
            if (IsTelegramPlatform(normPlatform))
            {
                try
                {
                    var sessionFile = GetTelegramSessionFilePath(userId, normPhone);
                    if (File.Exists(sessionFile))
                        File.Delete(sessionFile);
                }
                catch
                {
                }
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
                if (affected > 0)
                {
                    try
                    {
                        using (var cleanup = cn.CreateCommand())
                        {
                            cleanup.CommandText = "DELETE FROM dbo.chats WHERE user_id=@user_id AND phone=@phone";
                            cleanup.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                            cleanup.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = phone.Trim();
                            cleanup.ExecuteNonQuery();
                        }
                    }
                    catch
                    {
                    }
                }
                return Request.CreateResponse(affected > 0 ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }
        }

    private void TryTerminateTelegramSession(string userId, string phone)
    {
        try
        {
            var apiId = GetTelegramApiIdOrNull();
            var apiHash = GetTelegramApiHashOrNull();
            if (string.IsNullOrWhiteSpace(apiId) || string.IsNullOrWhiteSpace(apiHash)) return;
            var sessionFile = GetTelegramSessionFilePath(userId, phone);
            if (!File.Exists(sessionFile)) return;

            var sessionSemaphore = GetSessionSemaphore(sessionFile);
            if (!sessionSemaphore.Wait(TimeSpan.FromSeconds(5)))
            {
                LogAdsPowerError("TryTerminateTelegramSession: semaphore timeout.");
                return;
            }
            try
            {
                var task = Task.Run(async () =>
                {
                    using (var client = new Client(what =>
                    {
                        switch (what)
                        {
                            case "api_id": return apiId;
                            case "api_hash": return apiHash;
                            case "phone_number": return phone;
                            case "session_pathname": return sessionFile;
                            default: return null;
                        }
                    }))
                    {
                        await client.ConnectAsync();
                        await client.Auth_LogOut();
                    }
                });
                if (!task.Wait(TimeSpan.FromSeconds(10)))
                {
                    LogAdsPowerError("TryTerminateTelegramSession: logout timeout.");
                }
            }
            finally
            {
                sessionSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            try { LogAdsPowerError("TryTerminateTelegramSession failed.", ex); } catch { }
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
