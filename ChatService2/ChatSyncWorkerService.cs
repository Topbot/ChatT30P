using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TL;
using WTelegram;

namespace ChatService2
{
    public sealed class ChatSyncWorkerService : BackgroundService
    {
        private const int DbCommandTimeoutSeconds = 60;
        private const string TopicSuffix = ":topic:";
        private const string TelegramMediaRoot = @"C:\inetpub\chatt30pru\App_Data\telegram_media";
        private const string TelegramSessionRoot = @"C:\inetpub\chatt30pru\App_Data\telegram_sessions";
        private readonly ILogger<ChatSyncWorkerService> _logger;
        private readonly string? _connectionString;
        private readonly string? _apiId;
        private readonly string? _apiHash;
        private readonly SentimentAnalyzer? _sentimentAnalyzer;

        public ChatSyncWorkerService(ILogger<ChatSyncWorkerService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _connectionString = configuration["CHAT_CONNECTION_STRING"] ?? configuration.GetConnectionString("Chat");
            _apiId = configuration["Telegram:ApiId"];
            _apiHash = configuration["Telegram:ApiHash"];
            var modelPath = Path.Combine(AppContext.BaseDirectory, "YandexGPT-5-Lite-8B-instruct-Q4_K_M.gguf");
            _sentimentAnalyzer = SentimentAnalyzer.TryCreate(modelPath, _logger);
        }

        private static bool IsSessionFileLocked(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessStaleChatsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chat sync tick failed");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private async Task ProcessStaleChatsAsync(CancellationToken stoppingToken)
        {
            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                _logger.LogWarning("CHAT_CONNECTION_STRING is not configured");
                return;
            }

            if (string.IsNullOrWhiteSpace(_apiId) || string.IsNullOrWhiteSpace(_apiHash))
            {
                _logger.LogWarning("Telegram API credentials are missing");
                return;
            }

            var chats = LoadStaleChats();
            if (chats.Count == 0)
                return;

            var groups = chats
                .Where(c => !string.IsNullOrWhiteSpace(c.UserId) && !string.IsNullOrWhiteSpace(c.Phone))
                .GroupBy(c => BuildSessionKey(c.UserId, NormalizePhone(c.Phone)));

            foreach (var group in groups)
            {
                if (stoppingToken.IsCancellationRequested) return;

                var first = group.First();
                var userId = first.UserId;
                var phone = NormalizePhone(first.Phone);
                var sessionPath = GetTelegramSessionFilePath(userId, phone);
                if (!File.Exists(sessionPath))
                {
                    _logger.LogWarning("Session file missing for user={UserId} phone={Phone}", userId, phone);
                    continue;
                }

                if (IsSessionFileLocked(sessionPath))
                {
                    _logger.LogInformation("Session file is in use for user={UserId} phone={Phone}. Skipping this cycle.", userId, phone);
                    continue;
                }

                using var client = new Client(what =>
                {
                    switch (what)
                    {
                        case "api_id": return _apiId;
                        case "api_hash": return _apiHash;
                        case "phone_number": return phone;
                        case "session_pathname": return sessionPath;
                        default: return null;
                    }
                });

                try
                {
                    await client.ConnectAsync();
                    var dialogs = await client.Messages_GetDialogs();
                    var peerMap = BuildPeerMap(dialogs);

                    foreach (var chat in group)
                    {
                        if (stoppingToken.IsCancellationRequested) return;

                        var lookupId = ExtractBaseChatId(chat.ChatId);
                        if (!peerMap.TryGetValue(lookupId, out var peer))
                        {
                            _logger.LogWarning("Peer not found for chatId={ChatId}", chat.ChatId);
                            continue;
                        }

                        var startUtc = chat.UpdatedAt ?? DateTime.UtcNow.AddHours(-1);
                        var endUtc = DateTime.UtcNow;
                        var minUtc = DateTime.UtcNow.AddMonths(-2);
                        if (startUtc < minUtc)
                            startUtc = minUtc;
                        if (endUtc < startUtc)
                            endUtc = startUtc;
                        var dir = GetTelegramMediaDir(chat.ChatId);

                        var totalMessages = 0;
                        var sw = Stopwatch.StartNew();
                        if (TryExtractTopicId(chat.ChatId, out var topicId))
                        {
                            totalMessages = await DownloadTopicMessagesAsync(client, peer, topicId, dir, startUtc, endUtc, _sentimentAnalyzer);
                        }
                        else
                        {
                            totalMessages = await DownloadChatMessagesAsync(client, peer, dir, startUtc, endUtc, _sentimentAnalyzer);
                        }
                        sw.Stop();

                        var elapsedSeconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                        var perSecond = totalMessages / elapsedSeconds;
                        var perMinute = perSecond * 60;

                        _logger.LogInformation("Chat sync completed for chatId={ChatId}. Messages loaded: {MessageCount}. Rate: {MessagesPerSecond:F2}/s ({MessagesPerMinute:F2}/min)", chat.ChatId, totalMessages, perSecond, perMinute);

                        UpdateChatTimestamp(chat.UserId, chat.ChatId, chat.Phone, endUtc);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to sync chats for user={UserId} phone={Phone}", userId, phone);
                }
            }
        }

        private List<ChatWorkItem> LoadStaleChats()
        {
            var result = new List<ChatWorkItem>();
            try
            {
                using var cn = new SqlConnection(_connectionString);
                using var cmd = cn.CreateCommand();
                cmd.CommandTimeout = DbCommandTimeoutSeconds;
                cmd.CommandText = @"SELECT user_id, phone, chat_id, updated_at FROM dbo.chats WHERE updated_at < DATEADD(hour, -1, SYSUTCDATETIME())";
                cn.Open();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    result.Add(new ChatWorkItem
                    {
                        UserId = r[0] as string,
                        Phone = r[1] as string,
                        ChatId = r[2] as string,
                        UpdatedAt = r[3] == DBNull.Value ? null : r.GetDateTime(3).ToUniversalTime()
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load stale chats");
            }

            return result;
        }

        private void UpdateChatTimestamp(string userId, string chatId, string? phone, DateTime updatedAt)
        {
            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(userId)) return;
            try
            {
                using var cn = new SqlConnection(_connectionString);
                using var cmd = cn.CreateCommand();
                cmd.CommandTimeout = DbCommandTimeoutSeconds;
                cmd.CommandText = "UPDATE dbo.chats SET updated_at=@updated_at WHERE chat_id=@chat_id";
                cmd.Parameters.Add("@updated_at", SqlDbType.DateTime).Value = updatedAt;
                cmd.Parameters.Add("@chat_id", SqlDbType.NVarChar, 128).Value = chatId;
                cn.Open();
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update chat timestamp for chatId={ChatId}", chatId);
            }
        }

        private static async Task<int> DownloadChatMessagesAsync(Client client, InputPeer peer, string dir, DateTime startUtc, DateTime endUtc, SentimentAnalyzer? sentimentAnalyzer)
        {
            var offsetId = 0;
            var totalMessages = 0;
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
                    if (msgDate < startUtc)
                    {
                        shouldStop = true;
                        break;
                    }

                    if (msgDate > endUtc) continue;

                    if (!string.IsNullOrWhiteSpace(msg.message))
                    {
                        var replyText = GetReplyText(pageMessages, msg);
                        var sentiment = sentimentAnalyzer?.Predict(msg.message, replyText) ?? "neutral";
                        var filePath = SaveTextMessage(dir, msg.id, msgDate, msg.message);
                        if (!string.IsNullOrWhiteSpace(filePath))
                        {
                            var sender = GetSenderLabel(msg, senderMap);
                            var senderId = GetSenderId(msg);
                            var views = GetMessageViews(msg);
                            var replies = GetMessageReplies(msg);
                            var replyToId = GetReplyToMessageId(msg);
                            UpsertMessageLog(dir, msgDate, msg.id, sender, senderId, views, replies, replyToId, sentiment);
                            totalMessages++;
                        }
                    }
                }

                offsetId = pageMessages.Min(m => m.id);
                if (shouldStop) break;
            }

            return totalMessages;
        }

        private static async Task<int> DownloadTopicMessagesAsync(Client client, InputPeer peer, int topicId, string dir, DateTime startUtc, DateTime endUtc, SentimentAnalyzer? sentimentAnalyzer)
        {
            var offsetId = 0;
            var totalMessages = 0;
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
                    if (msgDate < startUtc)
                    {
                        shouldStop = true;
                        break;
                    }

                    if (msgDate > endUtc) continue;

                    if (!string.IsNullOrWhiteSpace(msg.message))
                    {
                        var replyText = GetReplyText(pageMessages, msg);
                        var sentiment = sentimentAnalyzer?.Predict(msg.message, replyText) ?? "neutral";
                        var filePath = SaveTextMessage(dir, msg.id, msgDate, msg.message);
                        if (!string.IsNullOrWhiteSpace(filePath))
                        {
                            var sender = GetSenderLabel(msg, senderMap);
                            var senderId = GetSenderId(msg);
                            var views = GetMessageViews(msg);
                            var replies = GetMessageReplies(msg);
                            var replyToId = GetReplyToMessageId(msg);
                            UpsertMessageLog(dir, msgDate, msg.id, sender, senderId, views, replies, replyToId, sentiment);
                            totalMessages++;
                        }
                    }
                }

                offsetId = pageMessages.Min(m => m.id);
                if (shouldStop) break;
            }

            return totalMessages;
        }

        private static string SaveTextMessage(string dir, int messageId, DateTime msgDate, string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                Directory.CreateDirectory(dir);
                var fileName = $"{msgDate:yyyyMMdd_HHmmss}_{messageId}.txt";
                var path = Path.Combine(dir, fileName);
                File.WriteAllText(path, text);
                return path;
            }
            catch
            {
                return null;
            }
        }

        private static string GetReplyText(List<Message> messages, Message msg)
        {
            if (msg == null || messages == null || messages.Count == 0) return string.Empty;
            var replyToId = GetReplyToMessageId(msg);
            if (!replyToId.HasValue || replyToId.Value <= 0) return string.Empty;
            var reply = messages.FirstOrDefault(m => m.id == replyToId.Value);
            return reply?.message ?? string.Empty;
        }

        private static object? TryGetMemberValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName)) return null;
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase;
            var type = target.GetType();
            while (type != null)
            {
                var prop = type.GetProperty(memberName, flags);
                if (prop != null) return prop.GetValue(target);
                var field = type.GetField(memberName, flags);
                if (field != null) return field.GetValue(target);
                type = type.BaseType;
            }
            return null;
        }

        private static void UpsertMessageLog(string dir, DateTime msgDate, int messageId, string sender, string senderId, int views, int replies, long? replyToId, string sentiment)
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

        private static bool TryExtractTopicId(string chatId, out int topicId)
        {
            topicId = 0;
            if (string.IsNullOrWhiteSpace(chatId)) return false;
            var idx = chatId.LastIndexOf(TopicSuffix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            var part = chatId.Substring(idx + TopicSuffix.Length);
            return int.TryParse(part, out topicId);
        }

        private static string ExtractBaseChatId(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId)) return chatId;
            var idx = chatId.LastIndexOf(TopicSuffix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                chatId = chatId.Substring(0, idx);
            if (chatId.StartsWith("channel:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = chatId.Substring("channel:".Length);
                var parts = rest.Split(':');
                return "channel:" + parts[0];
            }
            return chatId;
        }

        private static string GetTelegramMediaDir(string chatId)
        {
            var safeChat = NormalizeChatFolderName(chatId);
            var dir = Path.Combine(TelegramMediaRoot, safeChat);
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

        private static string GetTelegramSessionFilePath(string userId, string phone)
        {
            var safeUser = (userId ?? string.Empty).Replace("\\", "_").Replace("/", "_").Replace(":", "_");
            var safePhone = (phone ?? string.Empty).Replace("+", string.Empty).Replace(" ", string.Empty);
            Directory.CreateDirectory(TelegramSessionRoot);
            return Path.Combine(TelegramSessionRoot, $"tg_{safeUser}_{safePhone}.session");
        }

        private static string NormalizePhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return phone ?? string.Empty;
            phone = phone.Trim();
            if (phone.StartsWith("+"))
                return phone.Substring(1);
            return phone;
        }

        private static string BuildSessionKey(string userId, string phone)
        {
            var safeUser = (userId ?? string.Empty).Trim().ToLowerInvariant();
            var safePhone = (phone ?? string.Empty).Trim();
            return safeUser + "|" + safePhone;
        }

        private sealed class ChatWorkItem
        {
            public string UserId { get; set; }
            public string Phone { get; set; }
            public string ChatId { get; set; }
            public DateTime? UpdatedAt { get; set; }
        }
    }
}
