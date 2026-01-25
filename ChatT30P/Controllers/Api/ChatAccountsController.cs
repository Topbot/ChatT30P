using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web;
using System.Web.Http;
using ChatT30P.Controllers.Models;

namespace ChatT30P.Controllers.Api
{
    public class ChatAccountsController : ApiController
    {
        private static string ConnectionString => ConfigurationManager.ConnectionStrings["chatConnectionString"]?.ConnectionString;

        [HttpGet]
        public IEnumerable<ChatAccountItem> Get()
        {
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
        public HttpResponseMessage Post(ChatAccountItem item)
        {
            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
            {
                return Request.CreateResponse(HttpStatusCode.Unauthorized);
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

            return Request.CreateResponse(HttpStatusCode.OK);
        }
    }
}
