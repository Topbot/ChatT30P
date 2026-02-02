using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web;
using System.Web.Http;
using Newtonsoft.Json;

namespace ChatT30P.Controllers.Api
{
    public class ChatsController : ApiController
    {
        private static string ConnectionString => ConfigurationManager.ConnectionStrings["chatConnectionString"]?.ConnectionString;

        private class ChatRow
        {
            public string ChatId { get; set; }
            public string Title { get; set; }
            public string Phone { get; set; }
            public string Comment { get; set; }
            public int MessageCount { get; set; }
        }

        private class ChatMessageRow
        {
            public int MessageId { get; set; }
            public string FileName { get; set; }
            public string DateText { get; set; }
            public long DateTicks { get; set; }
            public string Sender { get; set; }
            public string Text { get; set; }
            public int Views { get; set; }
            public int Replies { get; set; }
            public string Sentiment { get; set; }
        }

        [HttpGet]
        [Route("api/Chats/Messages")]
        public HttpResponseMessage GetMessages([FromUri] string chatId, [FromUri] DateTime? start = null, [FromUri] DateTime? end = null)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return Request.CreateResponse(HttpStatusCode.InternalServerError);

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            if (string.IsNullOrWhiteSpace(chatId))
                return Request.CreateResponse(HttpStatusCode.BadRequest);

            var range = NormalizeRange(start, end);
            var result = LoadMessages(chatId, range.Start, range.End);
            return Request.CreateResponse(HttpStatusCode.OK, result);
        }

        [HttpGet]
        [Route("api/Chats/MessagesExport")]
        public HttpResponseMessage ExportMessages([FromUri] string chatId, [FromUri] DateTime? start = null, [FromUri] DateTime? end = null)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return Request.CreateResponse(HttpStatusCode.InternalServerError);

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            if (string.IsNullOrWhiteSpace(chatId))
                return Request.CreateResponse(HttpStatusCode.BadRequest);

            var range = NormalizeRange(start, end);
            var rows = LoadMessages(chatId, range.Start, range.End);
            var title = LoadChatTitle(userId, chatId);
            var csv = BuildCsv(rows, title);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(csv, System.Text.Encoding.UTF8, "application/vnd.ms-excel")
            };
            var safe = NormalizeChatFolderName(chatId);
            response.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
            {
                FileName = $"messages_{safe}_{DateTime.UtcNow:yyyyMMddHHmmss}.csv"
            };
            return response;
        }

        [HttpGet]
        [Route("api/Chats/Title")]
        public HttpResponseMessage GetTitle([FromUri] string chatId)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return Request.CreateResponse(HttpStatusCode.InternalServerError);

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            if (string.IsNullOrWhiteSpace(chatId))
                return Request.CreateResponse(HttpStatusCode.BadRequest);

            var title = LoadChatTitle(userId, chatId);
            return Request.CreateResponse(HttpStatusCode.OK, new { Title = title });
        }

        private class ChatTitleItem
        {
            public string id { get; set; }
            public string title { get; set; }
            public string username { get; set; }
        }

        [HttpGet]
        [Route("api/Chats")]
        public HttpResponseMessage Get([FromUri] DateTime? start = null, [FromUri] DateTime? end = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ConnectionString))
                    return Request.CreateResponse(HttpStatusCode.InternalServerError);

                var userId = HttpContext.Current?.User?.Identity?.Name;
                if (string.IsNullOrEmpty(userId))
                    return Request.CreateResponse(HttpStatusCode.Unauthorized);

                EnsureChatsTable();

                var titles = LoadChatTitles(userId);
                var range = NormalizeRange(start, end);
                var cutoff = range.Start;
                var rangeEnd = range.End;
                var result = new List<ChatRow>();

                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT chat_id, phone, comment FROM dbo.chats WHERE user_id=@user_id ORDER BY updated_at DESC";
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                    cn.Open();
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var chatId = r[0] as string;
                            var phone = r[1] as string;
                            var comment = r[2] as string;
                            if (string.IsNullOrWhiteSpace(chatId)) continue;
                            titles.TryGetValue(chatId, out var title);
                            result.Add(new ChatRow
                            {
                                ChatId = chatId,
                                Phone = phone,
                                Comment = comment,
                                Title = string.IsNullOrWhiteSpace(title) ? chatId : title,
                                MessageCount = CountMediaMessages(chatId, cutoff, rangeEnd)
                            });
                        }
                    }
                }

                return Request.CreateResponse(HttpStatusCode.OK, result);
            }
            catch (Exception ex)
            {
                return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, ex);
            }
        }

        [HttpDelete]
        [Route("api/Chats")]
        public HttpResponseMessage Delete([FromUri] string chatId, [FromUri] string phone = null)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return Request.CreateResponse(HttpStatusCode.InternalServerError);

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            if (string.IsNullOrWhiteSpace(chatId))
                return Request.CreateResponse(HttpStatusCode.BadRequest);

            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                if (!string.IsNullOrWhiteSpace(phone))
                {
                    cmd.CommandText = "DELETE FROM dbo.chats WHERE user_id=@user_id AND chat_id=@chat_id AND phone=@phone";
                    cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = NormalizePhone(phone);
                }
                else
                {
                    cmd.CommandText = "DELETE FROM dbo.chats WHERE user_id=@user_id AND chat_id=@chat_id";
                }
                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@chat_id", SqlDbType.NVarChar, 128).Value = chatId;
                cn.Open();
                var affected = cmd.ExecuteNonQuery();
                return Request.CreateResponse(affected > 0 ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }
        }

        private static void EnsureChatsTable()
        {
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"
IF OBJECT_ID('dbo.chats', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.chats(
        id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        user_id NVARCHAR(128) NOT NULL,
        phone NVARCHAR(64) NULL,
        chat_id NVARCHAR(128) NOT NULL,
        comment NVARCHAR(512) NULL,
        updated_at DATETIME NOT NULL DEFAULT(GETDATE())
    );
    CREATE INDEX IX_chats_user_id ON dbo.chats(user_id);
    CREATE UNIQUE INDEX IX_chats_user_chat ON dbo.chats(user_id, chat_id);
END
ELSE IF COL_LENGTH('dbo.chats','phone') IS NULL
BEGIN
    ALTER TABLE dbo.chats ADD phone NVARCHAR(64) NULL;
END
ELSE IF COL_LENGTH('dbo.chats','comment') IS NULL
BEGIN
    ALTER TABLE dbo.chats ADD comment NVARCHAR(512) NULL;
END";
                    cn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch
            {
            }
        }

        private class ChatInfo
        {
            public string Title { get; set; }
            public string Username { get; set; }
        }

        private static Dictionary<string, string> LoadChatTitles(string userId)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT chats_json FROM accounts WHERE user_id=@user_id";
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                    cn.Open();
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var json = r[0] as string;
                            if (string.IsNullOrWhiteSpace(json)) continue;
                            try
                            {
                                var items = JsonConvert.DeserializeObject<List<ChatTitleItem>>(json);
                                if (items == null) continue;
                                foreach (var item in items)
                                {
                                    if (item == null || string.IsNullOrWhiteSpace(item.id)) continue;
                                    if (!map.ContainsKey(item.id))
                                        map[item.id] = item.title;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return map;
        }

        private static ChatInfo LoadChatInfo(string userId, string chatId)
        {
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT chats_json FROM accounts WHERE user_id=@user_id";
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                    cn.Open();
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var json = r[0] as string;
                            if (string.IsNullOrWhiteSpace(json)) continue;
                            try
                            {
                                var items = JsonConvert.DeserializeObject<List<ChatTitleItem>>(json);
                                if (items == null) continue;
                                var match = items.FirstOrDefault(i => i != null && string.Equals(i.id, chatId, StringComparison.OrdinalIgnoreCase));
                                if (match != null)
                                {
                                    return new ChatInfo
                                    {
                                        Title = string.IsNullOrWhiteSpace(match.title) ? chatId : match.title,
                                        Username = match.username
                                    };
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return new ChatInfo { Title = chatId, Username = null };
        }

        private static int CountMediaMessages(string chatId, DateTime startUtc, DateTime endUtc)
        {
            try
            {
                var dir = GetTelegramMediaDir(chatId);
                if (!Directory.Exists(dir)) return 0;
                var logPath = Path.Combine(dir, "messages.log");
                if (!File.Exists(logPath))
                {
                    var files = Directory.GetFiles(dir, "*.jpg");
                    return files.Length;
                }

                var count = 0;
                foreach (var line in File.ReadLines(logPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length == 0) continue;
                    if (DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                    {
                        if (parsed >= startUtc && parsed <= endUtc) count++;
                    }
                }
                return count;
            }
            catch
            {
                return 0;
            }
        }

        private static (DateTime Start, DateTime End) NormalizeRange(DateTime? start, DateTime? end)
        {
            var endUtc = (end ?? DateTime.UtcNow).ToUniversalTime();
            var startUtc = (start ?? endUtc.AddMonths(-1)).ToUniversalTime();
            if (endUtc < startUtc)
            {
                var tmp = endUtc;
                endUtc = startUtc;
                startUtc = tmp;
            }
            return (startUtc, endUtc);
        }

        private static List<ChatMessageRow> LoadMessages(string chatId, DateTime startUtc, DateTime endUtc)
        {
            var result = new List<ChatMessageRow>();
            var dir = GetTelegramMediaDir(chatId);
            var logPath = Path.Combine(dir, "messages.log");
            if (!File.Exists(logPath)) return result;

            foreach (var line in File.ReadLines(logPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('|');
                if (parts.Length < 2) continue;
                if (!DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                    continue;
                if (parsed < startUtc || parsed > endUtc) continue;
                if (!int.TryParse(parts[1], out var messageId))
                    continue;
                var sender = parts.Length > 2 ? parts[2] : string.Empty;
                var views = parts.Length > 3 && int.TryParse(parts[3], out var v) ? v : 0;
                var replies = parts.Length > 4 && int.TryParse(parts[4], out var r) ? r : 0;
                var sentiment = parts.Length > 5 ? parts[5] : string.Empty;
                var text = ReadMessageText(chatId, parsed, messageId);
                result.Add(new ChatMessageRow
                {
                    MessageId = messageId,
                    DateText = parsed.ToString("yyyy-MM-dd HH:mm:ss"),
                    DateTicks = parsed.Ticks,
                    FileName = $"{parsed:yyyyMMdd_HHmmss}_{messageId}.txt",
                    Sender = sender,
                    Text = text,
                    Views = views,
                    Replies = replies,
                    Sentiment = sentiment
                });
            }
            return result;
        }

        public class UpdateMessageSentimentRequest
        {
            public string ChatId { get; set; }
            public int MessageId { get; set; }
            public string Sentiment { get; set; }
        }

        [HttpPut]
        [Route("api/Chats/MessagesSentiment")]
        public HttpResponseMessage UpdateMessageSentiment(UpdateMessageSentimentRequest request)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return Request.CreateResponse(HttpStatusCode.InternalServerError);

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            if (request == null || string.IsNullOrWhiteSpace(request.ChatId) || request.MessageId <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest);

            var dir = GetTelegramMediaDir(request.ChatId);
            var logPath = Path.Combine(dir, "messages.log");
            if (!File.Exists(logPath))
                return Request.CreateResponse(HttpStatusCode.NotFound);

            try
            {
                var lines = File.ReadAllLines(logPath).ToList();
                var updated = false;
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 2) continue;
                    if (!int.TryParse(parts[1], out var id)) continue;
                    if (id != request.MessageId) continue;
                    var sentiment = (request.Sentiment ?? string.Empty).Replace("|", "/");
                    if (parts.Length >= 6)
                    {
                        parts[5] = sentiment;
                    }
                    else
                    {
                        var padded = parts.ToList();
                        while (padded.Count < 6) padded.Add(string.Empty);
                        padded[5] = sentiment;
                        parts = padded.ToArray();
                    }
                    lines[i] = string.Join("|", parts);
                    updated = true;
                    break;
                }

                if (updated)
                {
                    File.WriteAllLines(logPath, lines);
                    return Request.CreateResponse(HttpStatusCode.OK);
                }
            }
            catch
            {
            }

            return Request.CreateResponse(HttpStatusCode.NotFound);
        }

        private static string ReadMessageText(string chatId, DateTime msgDate, int messageId)
        {
            try
            {
                var dir = GetTelegramMediaDir(chatId);
                var fileName = $"{msgDate:yyyyMMdd_HHmmss}_{messageId}.txt";
                var path = Path.Combine(dir, fileName);
                if (!File.Exists(path)) return string.Empty;
                return File.ReadAllText(path);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BuildCsv(IEnumerable<ChatMessageRow> rows, string title)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(title))
            {
                sb.AppendLine($"Chat: {title}");
                sb.AppendLine();
            }
            sb.AppendLine("Date,Sender,Views,Replies,Text");
            foreach (var row in rows)
            {
                var date = row.DateText?.Replace("\"", "\"\"") ?? string.Empty;
                var sender = row.Sender?.Replace("\"", "\"\"") ?? string.Empty;
                var text = row.Text?.Replace("\"", "\"\"") ?? string.Empty;
                sb.Append('"').Append(date).Append('"').Append(',');
                sb.Append('"').Append(sender).Append('"').Append(',');
                sb.Append(row.Views).Append(',');
                sb.Append(row.Replies).Append(',');
                sb.Append('"').Append(text).Append('"').AppendLine();
            }
            return sb.ToString();
        }

        private static string LoadChatTitle(string userId, string chatId)
        {
            try
            {
                var titles = LoadChatTitles(userId);
                if (titles.TryGetValue(chatId, out var title) && !string.IsNullOrWhiteSpace(title))
                    return title;
            }
            catch
            {
            }
            return chatId;
        }

        public class UpdateChatCommentRequest
        {
            public string ChatId { get; set; }
            public string Phone { get; set; }
            public string Comment { get; set; }
        }

        [HttpPut]
        [Route("api/Chats")]
        public HttpResponseMessage UpdateComment(UpdateChatCommentRequest request)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return Request.CreateResponse(HttpStatusCode.InternalServerError);

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                return Request.CreateResponse(HttpStatusCode.BadRequest);

            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "UPDATE dbo.chats SET comment=@comment WHERE user_id=@user_id AND chat_id=@chat_id AND (@phone IS NULL OR phone=@phone)";
                cmd.Parameters.Add("@comment", SqlDbType.NVarChar, 512).Value = (object)request.Comment ?? DBNull.Value;
                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@chat_id", SqlDbType.NVarChar, 128).Value = request.ChatId;
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = (object)NormalizePhone(request.Phone) ?? DBNull.Value;
                cn.Open();
                var affected = cmd.ExecuteNonQuery();
                return Request.CreateResponse(affected > 0 ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }
        }

        private static string GetTelegramMediaDir(string chatId)
        {
            var baseDir = HttpContext.Current?.Server?.MapPath("~/App_Data") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data");
            var safeChat = NormalizeChatFolderName(chatId);
            return Path.Combine(baseDir, "telegram_media", safeChat);
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

        private static string NormalizePhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return phone;
            phone = phone.Trim();
            if (phone.StartsWith("+"))
                return phone.Substring(1);
            return phone;
        }
    }
}
