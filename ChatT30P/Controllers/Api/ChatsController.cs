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
using OfficeOpenXml;
using OfficeOpenXml.Drawing.Chart;

namespace ChatT30P.Controllers.Api
{
    public class ChatsController : ApiController
    {
        private static string ConnectionString => ConfigurationManager.ConnectionStrings["chatConnectionString"]?.ConnectionString;
        private const string TopicSuffix = ":topic:";

        private class ChatRow
        {
            public string ChatId { get; set; }
            public string Title { get; set; }
            public string Phone { get; set; }
            public string Comment { get; set; }
            public int MessageCount { get; set; }
            public int PositiveCount { get; set; }
            public int NeutralCount { get; set; }
            public int NegativeCount { get; set; }
            public int UnknownCount { get; set; }
            public string AvatarUrl { get; set; }
        }

        private class ChatMessageRow
        {
            public int MessageId { get; set; }
            public string FileName { get; set; }
            public string DateText { get; set; }
            public long DateTicks { get; set; }
            public string Sender { get; set; }
            public string SenderId { get; set; }
            public string Text { get; set; }
            public int ReplyToMessageId { get; set; }
            public string ReplyText { get; set; }
            public int Views { get; set; }
            public int Replies { get; set; }
            public string Sentiment { get; set; }
        }

        [HttpGet]
        [Route("api/Chats/Messages")]
        public HttpResponseMessage GetMessages([FromUri] string chatId, [FromUri] DateTime? start = null, [FromUri] DateTime? end = null, [FromUri] string q = null)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return Request.CreateResponse(HttpStatusCode.InternalServerError);

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            if (string.IsNullOrWhiteSpace(chatId))
                return Request.CreateResponse(HttpStatusCode.BadRequest);

            var range = NormalizeRange(start, end);
            var result = LoadMessages(chatId, range.Start, range.End, q);
            return Request.CreateResponse(HttpStatusCode.OK, result);
        }

        [HttpGet]
        [Route("api/Chats/MessagesExport")]
        public HttpResponseMessage ExportMessages([FromUri] string chatId, [FromUri] DateTime? start = null, [FromUri] DateTime? end = null, [FromUri] string mode = null)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return Request.CreateResponse(HttpStatusCode.InternalServerError);

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            if (string.IsNullOrWhiteSpace(chatId))
                return Request.CreateResponse(HttpStatusCode.BadRequest);

            var range = NormalizeRange(start, end);
            var rows = LoadMessages(chatId, range.Start, range.End, null);
            var title = LoadChatTitle(userId, chatId);
            if (string.Equals(mode, "charts", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = BuildChartsExcel(rows, title);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                var safe = NormalizeChatFolderName(chatId);
                response.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                {
                    FileName = $"messages_charts_{safe}_{DateTime.UtcNow:yyyyMMddHHmmss}.xlsx"
                };
                return response;
            }

            var csv = BuildCsv(rows, title);
            var csvResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(csv, System.Text.Encoding.UTF8, "application/vnd.ms-excel")
            };
            var csvSafe = NormalizeChatFolderName(chatId);
            csvResponse.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
            {
                FileName = $"messages_{csvSafe}_{DateTime.UtcNow:yyyyMMddHHmmss}.csv"
            };
            return csvResponse;
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

            var info = LoadChatInfo(userId, chatId);
            return Request.CreateResponse(HttpStatusCode.OK, new { Title = info.Title, Username = info.Username });
        }

        private class ChatTitleItem
        {
            public string id { get; set; }
            public string title { get; set; }
            public string username { get; set; }
            public List<ChatTopicItem> topics { get; set; }
        }

        private class ChatTopicItem
        {
            public int id { get; set; }
            public string title { get; set; }
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
                            var sentiment = CountSentimentMessages(chatId, cutoff, rangeEnd);
                            result.Add(new ChatRow
                            {
                                ChatId = chatId,
                                Phone = phone,
                                Comment = comment,
                                Title = string.IsNullOrWhiteSpace(title) ? chatId : title,
                                MessageCount = CountMediaMessages(chatId, cutoff, rangeEnd),
                                PositiveCount = sentiment.Positive,
                                NeutralCount = sentiment.Neutral,
                                NegativeCount = sentiment.Negative,
                                UnknownCount = sentiment.Unknown,
                                AvatarUrl = GetAvatarUrl(chatId)
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

        private static SentimentCounts CountSentimentMessages(string chatId, DateTime startUtc, DateTime endUtc)
        {
            var counts = new SentimentCounts();
            try
            {
                var dir = GetTelegramMediaDir(chatId);
                var logPath = Path.Combine(dir, "messages.log");
                if (!File.Exists(logPath)) return counts;

                foreach (var line in File.ReadLines(logPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 8) continue;
                    if (!DateTime.TryParseExact(parts[0], "yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                        continue;
                    if (parsed < startUtc || parsed > endUtc) continue;
                    var sentiment = parts[7] ?? string.Empty;
                    switch (sentiment.Trim().ToUpperInvariant())
                    {
                        case "P":
                            counts.Positive++;
                            break;
                        case "N":
                            counts.Negative++;
                            break;
                        case "U":
                            counts.Neutral++;
                            break;
                        case "O":
                        default:
                            counts.Unknown++;
                            break;
                    }
                }
            }
            catch
            {
            }
            return counts;
        }

        private struct SentimentCounts
        {
            public int Positive;
            public int Neutral;
            public int Negative;
            public int Unknown;
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
                                    if (item.topics != null && item.topics.Count > 0)
                                    {
                                        foreach (var topic in item.topics)
                                        {
                                            if (topic == null || topic.id <= 0) continue;
                                            var topicTitle = BuildTopicTitle(item.title, topic.title, topic.id);
                                            var topicId = BuildTopicChatId(item.id, topic.id);
                                            if (!map.ContainsKey(topicId))
                                                map[topicId] = topicTitle;
                                        }
                                    }
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
                                            Username = IsTelegramChatId(match.id) ? match.username : null
                                        };
                                }
                                if (TryExtractTopicId(chatId, out var baseId, out var topicId))
                                {
                                    var baseItem = items.FirstOrDefault(i => i != null && string.Equals(i.id, baseId, StringComparison.OrdinalIgnoreCase));
                                    if (baseItem != null)
                                    {
                                        var topicTitle = baseItem.topics == null
                                            ? null
                                            : baseItem.topics.FirstOrDefault(t => t != null && t.id == topicId)?.title;
                                        return new ChatInfo
                                        {
                                            Title = BuildTopicTitle(baseItem.title, topicTitle, topicId),
                                            Username = IsTelegramChatId(baseItem.id) ? baseItem.username : null
                                        };
                                    }
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

        private static string BuildTopicChatId(string chatId, int topicId)
        {
            if (string.IsNullOrWhiteSpace(chatId)) return chatId;
            var baseId = chatId;
            var idx = chatId.LastIndexOf(TopicSuffix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                baseId = chatId.Substring(0, idx);
            return baseId + TopicSuffix + topicId;
        }

        private static string BuildTopicTitle(string chatTitle, string topicTitle, int topicId)
        {
            var baseTitle = chatTitle ?? string.Empty;
            var topicText = string.IsNullOrWhiteSpace(topicTitle) ? ("Топик " + topicId) : topicTitle;
            if (string.IsNullOrWhiteSpace(baseTitle)) return topicText;
            return baseTitle + " - " + topicText;
        }

        private static bool IsTelegramChatId(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId)) return false;
            var baseId = chatId;
            var idx = chatId.LastIndexOf(TopicSuffix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                baseId = chatId.Substring(0, idx);
            return baseId.StartsWith("chat:", StringComparison.OrdinalIgnoreCase)
                || baseId.StartsWith("channel:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryExtractTopicId(string chatId, out string baseId, out int topicId)
        {
            baseId = chatId;
            topicId = 0;
            if (string.IsNullOrWhiteSpace(chatId)) return false;
            var idx = chatId.LastIndexOf(TopicSuffix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            baseId = chatId.Substring(0, idx);
            var part = chatId.Substring(idx + TopicSuffix.Length);
            return int.TryParse(part, out topicId);
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
            var startUtc = (start ?? endUtc.AddMonths(-2)).ToUniversalTime();
            if (endUtc < startUtc)
            {
                var tmp = endUtc;
                endUtc = startUtc;
                startUtc = tmp;
            }
            return (startUtc, endUtc);
        }

        private static List<ChatMessageRow> LoadMessages(string chatId, DateTime startUtc, DateTime endUtc, string query)
        {
            var result = new List<ChatMessageRow>();
            var dir = GetTelegramMediaDir(chatId);
            var logPath = Path.Combine(dir, "messages.log");
            if (!File.Exists(logPath)) return result;

            var queryText = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
            var entries = new List<ChatMessageLogEntry>();
            var dateMap = new Dictionary<int, DateTime>();
            foreach (var line in File.ReadLines(logPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('|');
                if (parts.Length < 8) continue;
                if (!DateTime.TryParseExact(parts[0], "yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                    continue;
                if (!int.TryParse(parts[1], out var messageId))
                    continue;
                var replyToId = int.TryParse(parts[6], out var replyId) ? replyId : 0;
                var entry = new ChatMessageLogEntry
                {
                    Date = parsed,
                    MessageId = messageId,
                    Sender = parts.Length > 2 ? parts[2] : string.Empty,
                    SenderId = parts[3],
                    Views = int.TryParse(parts[4], out var v) ? v : 0,
                    Replies = int.TryParse(parts[5], out var r) ? r : 0,
                    ReplyToMessageId = replyToId,
                    Sentiment = ExpandSentimentCode(parts[7])
                };
                entries.Add(entry);
                if (!dateMap.ContainsKey(messageId))
                    dateMap[messageId] = parsed;
            }

            foreach (var entry in entries)
            {
                if (entry.Date < startUtc || entry.Date > endUtc) continue;
                var text = ReadMessageText(chatId, entry.Date, entry.MessageId);
                if (!string.IsNullOrEmpty(queryText))
                {
                    if (string.IsNullOrEmpty(text) || text.IndexOf(queryText, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }

                var replyText = string.Empty;
                if (entry.ReplyToMessageId > 0 && dateMap.TryGetValue(entry.ReplyToMessageId, out var replyDate))
                {
                    replyText = ReadMessageText(chatId, replyDate, entry.ReplyToMessageId);
                }

                result.Add(new ChatMessageRow
                {
                    MessageId = entry.MessageId,
                    DateText = entry.Date.ToString("yyyy-MM-dd HH:mm:ss"),
                    DateTicks = entry.Date.Ticks,
                    FileName = $"{entry.Date:yyyyMMdd_HHmmss}_{entry.MessageId}.txt",
                    Sender = entry.Sender,
                    SenderId = entry.SenderId,
                    Text = text,
                    ReplyToMessageId = entry.ReplyToMessageId,
                    ReplyText = replyText,
                    Views = entry.Views,
                    Replies = entry.Replies,
                    Sentiment = entry.Sentiment
                });
            }
            return result;
        }

        private class ChatMessageLogEntry
        {
            public DateTime Date { get; set; }
            public int MessageId { get; set; }
            public string Sender { get; set; }
            public string SenderId { get; set; }
            public int Views { get; set; }
            public int Replies { get; set; }
            public int ReplyToMessageId { get; set; }
            public string Sentiment { get; set; }
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
                    if (parts.Length < 8) continue;
                    if (!int.TryParse(parts[1], out var id)) continue;
                    if (id != request.MessageId) continue;
                    var sentiment = NormalizeSentimentCode(request.Sentiment);
                    parts[7] = sentiment;
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

        private static string ExpandSentimentCode(string sentiment)
        {
            if (string.IsNullOrWhiteSpace(sentiment)) return string.Empty;
            switch (sentiment.Trim().ToUpperInvariant())
            {
                case "P":
                    return "positive";
                case "N":
                    return "negative";
                case "U":
                    return "neutral";
                case "O":
                    return "unknown";
                default:
                    return sentiment;
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

        private static byte[] BuildChartsExcel(IEnumerable<ChatMessageRow> rows, string title)
        {
            var list = rows?.ToList() ?? new List<ChatMessageRow>();
            var sentiments = new[] { "positive", "neutral", "negative", "unknown" };
            var sentimentLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["positive"] = "Позитивное",
                ["neutral"] = "Нейтральное",
                ["negative"] = "Негативное",
                ["unknown"] = "O — не определено"
            };

            var summaryCounts = sentiments.ToDictionary(s => s, s => 0, StringComparer.OrdinalIgnoreCase);
            var dayCounts = new SortedDictionary<DateTime, Dictionary<string, int>>();

            foreach (var row in list)
            {
                var sentiment = (row?.Sentiment ?? string.Empty).Trim().ToLowerInvariant();
                if (!summaryCounts.ContainsKey(sentiment))
                    sentiment = "unknown";
                summaryCounts[sentiment]++;

                if (!DateTime.TryParse(row?.DateText, out var date))
                    continue;
                var day = date.Date;
                if (!dayCounts.TryGetValue(day, out var map))
                {
                    map = sentiments.ToDictionary(s => s, s => 0, StringComparer.OrdinalIgnoreCase);
                    dayCounts[day] = map;
                }
                map[sentiment]++;
            }

            using (var package = new ExcelPackage())
            {
                var summarySheet = package.Workbook.Worksheets.Add("Summary");
                summarySheet.Cells[1, 1].Value = "Sentiment";
                summarySheet.Cells[1, 2].Value = "Count";
                var rowIndex = 2;
                foreach (var sentiment in sentiments)
                {
                    summarySheet.Cells[rowIndex, 1].Value = sentimentLabels[sentiment];
                    summarySheet.Cells[rowIndex, 2].Value = summaryCounts[sentiment];
                    rowIndex++;
                }
                summarySheet.Cells[1, 1, rowIndex - 1, 2].AutoFitColumns();

                var dailySheet = package.Workbook.Worksheets.Add("Daily");
                dailySheet.Cells[1, 1].Value = "Date";
                for (var i = 0; i < sentiments.Length; i++)
                {
                    dailySheet.Cells[1, i + 2].Value = sentimentLabels[sentiments[i]];
                }
                dailySheet.Cells[1, sentiments.Length + 2].Value = "Total";

                rowIndex = 2;
                foreach (var dayEntry in dayCounts)
                {
                    dailySheet.Cells[rowIndex, 1].Value = dayEntry.Key.ToString("yyyy-MM-dd");
                    var total = 0;
                    for (var i = 0; i < sentiments.Length; i++)
                    {
                        var value = dayEntry.Value[sentiments[i]];
                        dailySheet.Cells[rowIndex, i + 2].Value = value;
                        total += value;
                    }
                    dailySheet.Cells[rowIndex, sentiments.Length + 2].Value = total;
                    rowIndex++;
                }
                dailySheet.Cells[1, 1, Math.Max(1, rowIndex - 1), sentiments.Length + 2].AutoFitColumns();

                var chartSheet = package.Workbook.Worksheets.Add("Charts");
                if (!string.IsNullOrWhiteSpace(title))
                {
                    chartSheet.Cells[1, 1].Value = title;
                }

                var pieChart = chartSheet.Drawings.AddChart("SentimentPie", eChartType.Pie) as ExcelPieChart;
                if (pieChart != null)
                {
                    pieChart.Title.Text = "Распределение тональности";
                    pieChart.SetPosition(2, 0, 0, 0);
                    pieChart.SetSize(400, 300);
                    var labelRange = summarySheet.Cells[2, 1, sentiments.Length + 1, 1];
                    var valueRange = summarySheet.Cells[2, 2, sentiments.Length + 1, 2];
                    pieChart.Series.Add(valueRange, labelRange);
                }

                var stackedChart = chartSheet.Drawings.AddChart("SentimentByDay", eChartType.ColumnStacked);
                stackedChart.Title.Text = "Динамика по периодам";
                stackedChart.SetPosition(2, 0, 6, 0);
                stackedChart.SetSize(600, 300);
                var lastRow = Math.Max(2, dayCounts.Count + 1);
                var xRange = dailySheet.Cells[2, 1, lastRow, 1];
                for (var i = 0; i < sentiments.Length; i++)
                {
                    var dataRange = dailySheet.Cells[2, i + 2, lastRow, i + 2];
                    var series = stackedChart.Series.Add(dataRange, xRange);
                    series.Header = dailySheet.Cells[1, i + 2].Value?.ToString();
                }

                return package.GetAsByteArray();
            }
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

        private static string GetAvatarUrl(string chatId)
        {
            try
            {
                var fileName = NormalizeChatFolderName(chatId) + ".jpg";
                var baseDir = HttpContext.Current?.Server?.MapPath("~/Content/ava") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Content", "ava");
                var path = Path.Combine(baseDir, fileName);
                if (File.Exists(path))
                    return "/Content/ava/" + fileName;
            }
            catch
            {
            }
            return null;
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
