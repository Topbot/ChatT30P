using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Web;
using System.Web.Http;
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
        private const string AdsPowerGroupName = "CHAT";

        [HttpGet]
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
SELECT user_id, platform, phone, status,
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
                            Created = r[4] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r[4])
                        });
                    }
                }
            }

            return result;
        }

        [HttpPost]
        public async Task<HttpResponseMessage> Post(ChatAccountItem item)
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

            if (!Security.IsPaid)
            {
                return Request.CreateResponse((HttpStatusCode)402, "No active subscription");
            }

            if (item == null || string.IsNullOrWhiteSpace(item.Platform) || string.IsNullOrWhiteSpace(item.Phone))
            {
                return Request.CreateResponse(HttpStatusCode.BadRequest);
            }

            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO accounts(user_id, platform, phone, status, created)
VALUES(@user_id, @platform, @phone, @status, GETDATE())";

                cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                cmd.Parameters.Add("@platform", SqlDbType.NVarChar, 32).Value = item.Platform.Trim();
                cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 64).Value = item.Phone.Trim();
                cmd.Parameters.Add("@status", SqlDbType.Int).Value = item.Status;

                cn.Open();
                cmd.ExecuteNonQuery();
            }

            // Create AdsPower profile in group CHAT after inserting to DB.
            // If AdsPower is not configured, skip.
            if (!string.IsNullOrWhiteSpace(AdsPowerBaseUrl) && !string.IsNullOrWhiteSpace(AdsPowerToken))
            {
                var groupId = await EnsureAdsPowerGroupAsync(AdsPowerGroupName);
                if (string.IsNullOrEmpty(groupId) || !await CreateAdsPowerProfileAsync(groupId, item))
                    return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, "На сервере случилась критическая ошибка, обратитесь к администратору.");
            }

            return Request.CreateResponse(HttpStatusCode.OK);
        }

        private async Task<string> EnsureAdsPowerGroupAsync(string groupName)
        {
            // API endpoints are based on AdsPower Local API docs.
            // Query: /api/v1/group/list?group_name=xxx
            // Create: /api/v1/group/create
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(15);

                var listUrl = $"{AdsPowerBaseUrl.TrimEnd('/')}/api/v1/group/list?group_name={Uri.EscapeDataString(groupName)}&token={Uri.EscapeDataString(AdsPowerToken)}";
                var listResp = await http.GetAsync(listUrl);
                if (!listResp.IsSuccessStatusCode)
                    return null;
                var listJson = await listResp.Content.ReadAsStringAsync();

                dynamic listObj = null;
                try { listObj = JsonConvert.DeserializeObject(listJson); } catch { }

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
                if (!createResp.IsSuccessStatusCode)
                    return null;
                var createJson = await createResp.Content.ReadAsStringAsync();

                dynamic createObj = null;
                try { createObj = JsonConvert.DeserializeObject(createJson); } catch { }

                if (createObj != null && createObj.code == 0 && createObj.data != null)
                {
                    // docs vary: sometimes group_id is in data.group_id
                    if (createObj.data.group_id != null) return (string)createObj.data.group_id;
                    if (createObj.data.id != null) return (string)createObj.data.id;
                }

                return null;
            }
        }

        private async Task<bool> CreateAdsPowerProfileAsync(string groupId, ChatAccountItem item)
        {
            // Create profile: /api/v1/user/create (POST), requires group_id and fingerprint_config
            // We'll use minimal fingerprint_config with automatic timezone.
            var url = $"{AdsPowerBaseUrl.TrimEnd('/')}/api/v1/user/create?token={Uri.EscapeDataString(AdsPowerToken)}";
            var body = new
            {
                name = $"{item.Platform}-{item.Phone}",
                group_id = groupId,
                domain_name = "web.telegram.org",
                fingerprint_config = new
                {
                    automatic_timezone = "1"
                }
            };

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(20);
                var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(url, content);
                if (!resp.IsSuccessStatusCode)
                    return false;

                var json = await resp.Content.ReadAsStringAsync();
                dynamic obj = null;
                try { obj = JsonConvert.DeserializeObject(json); } catch { }
                return obj != null && obj.code == 0 && obj.data != null && obj.data.id != null;
            }
        }

        [HttpDelete]
        public HttpResponseMessage Delete(string platform, string phone)
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
    }
}
