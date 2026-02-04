using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web;
using System.Web.Http;
using Core;

namespace ChatT30P.Controllers.Api
{
    public class ProblemsController : ApiController
    {
        private static string ConnectionString => ConfigurationManager.ConnectionStrings["chatConnectionString"]?.ConnectionString;

        public class ProblemItem
        {
            public int ProblemId { get; set; }
            public string Title { get; set; }
            public string Description { get; set; }
            public string Owner { get; set; }
            public bool IsSelected { get; set; }
            public bool Notify { get; set; }
        }

        public class ProblemCreate
        {
            public string Title { get; set; }
            public string Description { get; set; }
        }

        public class NotifySelection
        {
            public List<int> ProblemIds { get; set; }
        }

        [HttpGet]
        [Route("api/Problems")]
        public HttpResponseMessage Get()
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return Request.CreateResponse(HttpStatusCode.InternalServerError);

            if (!Security.IsAuthenticated)
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrWhiteSpace(userId))
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            EnsureProblemsTables();

            var selected = LoadSelectedProblemIds(userId);
            var notify = LoadNotifyProblemIds(userId);
            var items = LoadProblems(userId, selected, notify);
            return Request.CreateResponse(HttpStatusCode.OK, items);
        }

        [HttpPost]
        [Route("api/Problems")]
        public HttpResponseMessage Post(ProblemCreate dto)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return Request.CreateResponse(HttpStatusCode.InternalServerError);

            if (!Security.IsAuthenticated)
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrWhiteSpace(userId))
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            if (dto == null || string.IsNullOrWhiteSpace(dto.Title))
                return Request.CreateResponse(HttpStatusCode.BadRequest);

            EnsureProblemsTables();

            using (var cn = new SqlConnection(ConnectionString))
            {
                cn.Open();
                using (var tr = cn.BeginTransaction())
                using (var cmd = cn.CreateCommand())
                {
                    cmd.Transaction = tr;
                    cmd.CommandText = @"INSERT INTO dbo.problems (title, description, owner)
VALUES (@title, @description, @owner);
SELECT CAST(SCOPE_IDENTITY() as int);";
                    cmd.Parameters.Add("@title", SqlDbType.NVarChar, 256).Value = dto.Title.Trim();
                    cmd.Parameters.Add("@description", SqlDbType.NVarChar).Value = (object)dto.Description ?? DBNull.Value;
                    cmd.Parameters.Add("@owner", SqlDbType.NVarChar, 128).Value = userId;
                    var id = (int)cmd.ExecuteScalar();

                    cmd.Parameters.Clear();
                    cmd.CommandText = "INSERT INTO dbo.problem_users (problem_id, user_id, notify) VALUES (@problem_id, @user_id, 0)";
                    cmd.Parameters.Add("@problem_id", SqlDbType.Int).Value = id;
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                    cmd.ExecuteNonQuery();

                    tr.Commit();
                    var item = new ProblemItem
                    {
                        ProblemId = id,
                        Title = dto.Title.Trim(),
                        Description = dto.Description,
                        Owner = userId,
                        IsSelected = true,
                        Notify = false
                    };
                    return Request.CreateResponse(HttpStatusCode.OK, item);
                }
            }
        }

        [HttpPut]
        [Route("api/Problems/Selection")]
        public IHttpActionResult SaveSelection([FromBody] List<int> problemIds)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return InternalServerError();

            if (!Security.IsAuthenticated)
                return Unauthorized();

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrWhiteSpace(userId))
                return Unauthorized();

            EnsureProblemsTables();

            var ids = problemIds ?? new List<int>();

            using (var cn = new SqlConnection(ConnectionString))
            {
                cn.Open();
                using (var tr = cn.BeginTransaction())
                {
                    using (var cmd = cn.CreateCommand())
                    {
                        cmd.Transaction = tr;
                        cmd.CommandText = "DELETE FROM dbo.problem_users WHERE user_id=@user_id";
                        cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                        cmd.ExecuteNonQuery();
                    }

                    if (ids.Count > 0)
                    {
                        using (var cmd = cn.CreateCommand())
                        {
                            cmd.Transaction = tr;
                            cmd.CommandText = "INSERT INTO dbo.problem_users (problem_id, user_id, notify) VALUES (@problem_id, @user_id, 0)";
                            cmd.Parameters.Add("@problem_id", SqlDbType.Int);
                            cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                            foreach (var id in ids)
                            {
                                cmd.Parameters["@problem_id"].Value = id;
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    tr.Commit();
                }
            }

            return Ok();
        }

        [HttpPut]
        [Route("api/Problems/Notify")]
        public IHttpActionResult SaveNotify([FromBody] NotifySelection request)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return InternalServerError();

            if (!Security.IsAuthenticated)
                return Unauthorized();

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrWhiteSpace(userId))
                return Unauthorized();

            EnsureProblemsTables();

            var ids = request?.ProblemIds ?? new List<int>();

            using (var cn = new SqlConnection(ConnectionString))
            {
                cn.Open();
                using (var tr = cn.BeginTransaction())
                using (var cmd = cn.CreateCommand())
                {
                    cmd.Transaction = tr;
                    cmd.CommandText = "UPDATE dbo.problem_users SET notify=0 WHERE user_id=@user_id";
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                    cmd.ExecuteNonQuery();

                    if (ids.Count > 0)
                    {
                        cmd.Parameters.Clear();
                        cmd.CommandText = "UPDATE dbo.problem_users SET notify=1 WHERE user_id=@user_id AND problem_id=@problem_id";
                        cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                        cmd.Parameters.Add("@problem_id", SqlDbType.Int);
                        foreach (var id in ids)
                        {
                            cmd.Parameters["@problem_id"].Value = id;
                            cmd.ExecuteNonQuery();
                        }
                    }

                    tr.Commit();
                }
            }

            return Ok();
        }

        [HttpDelete]
        [Route("api/Problems")]
        public IHttpActionResult Delete([FromUri] int problemId)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return InternalServerError();

            if (!Security.IsAuthenticated)
                return Unauthorized();

            var userId = HttpContext.Current?.User?.Identity?.Name;
            if (string.IsNullOrWhiteSpace(userId))
                return Unauthorized();

            if (problemId <= 0)
                return BadRequest();

            EnsureProblemsTables();

            string owner = null;
            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT owner FROM dbo.problems WHERE problem_id=@problem_id";
                cmd.Parameters.Add("@problem_id", SqlDbType.Int).Value = problemId;
                cn.Open();
                owner = cmd.ExecuteScalar() as string;
            }

            if (string.IsNullOrWhiteSpace(owner))
                return NotFound();

            if (string.Equals(owner, "all", StringComparison.OrdinalIgnoreCase) && !Security.IsAdmin)
                return StatusCode(HttpStatusCode.Forbidden);

            if (!string.Equals(owner, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(owner, userId, StringComparison.OrdinalIgnoreCase) &&
                !Security.IsAdmin)
            {
                return StatusCode(HttpStatusCode.Forbidden);
            }

            using (var cn = new SqlConnection(ConnectionString))
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"DELETE FROM dbo.problem_users WHERE problem_id=@problem_id;
DELETE FROM dbo.problems WHERE problem_id=@problem_id;";
                cmd.Parameters.Add("@problem_id", SqlDbType.Int).Value = problemId;
                cn.Open();
                cmd.ExecuteNonQuery();
            }

            return Ok();
        }

        private static void EnsureProblemsTables()
        {
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"
IF OBJECT_ID('dbo.problems', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.problems(
        problem_id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        title NVARCHAR(256) NOT NULL,
        description NVARCHAR(MAX) NULL,
        owner NVARCHAR(128) NOT NULL
    );
    CREATE INDEX IX_problems_owner ON dbo.problems(owner);
END
IF OBJECT_ID('dbo.problem_users', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.problem_users(
        problem_id INT NOT NULL,
        user_id NVARCHAR(128) NOT NULL,
        notify BIT NOT NULL CONSTRAINT DF_problem_users_notify DEFAULT(0),
        CONSTRAINT PK_problem_users PRIMARY KEY (problem_id, user_id),
        CONSTRAINT FK_problem_users_problem FOREIGN KEY (problem_id) REFERENCES dbo.problems(problem_id)
    );
    CREATE INDEX IX_problem_users_user ON dbo.problem_users(user_id);
END
IF COL_LENGTH('dbo.problem_users','notify') IS NULL
BEGIN
    ALTER TABLE dbo.problem_users ADD notify BIT NOT NULL CONSTRAINT DF_problem_users_notify DEFAULT(0);
END";
                    cn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch
            {
            }
        }

        private static HashSet<int> LoadSelectedProblemIds(string userId)
        {
            var result = new HashSet<int>();
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT problem_id FROM dbo.problem_users WHERE user_id=@user_id";
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                    cn.Open();
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            if (r[0] is int id)
                                result.Add(id);
                        }
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        private static HashSet<int> LoadNotifyProblemIds(string userId)
        {
            var result = new HashSet<int>();
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT problem_id FROM dbo.problem_users WHERE user_id=@user_id AND notify=1";
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 128).Value = userId;
                    cn.Open();
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            if (r[0] is int id)
                                result.Add(id);
                        }
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        private static List<ProblemItem> LoadProblems(string userId, HashSet<int> selected, HashSet<int> notify)
        {
            var items = new List<ProblemItem>();
            try
            {
                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT problem_id, title, description, owner FROM dbo.problems WHERE owner='all' OR owner=@owner ORDER BY title";
                    cmd.Parameters.Add("@owner", SqlDbType.NVarChar, 128).Value = userId;
                    cn.Open();
                    using (var r = cmd.ExecuteReader())
                    {
                        var titleMap = new Dictionary<string, ProblemItem>(StringComparer.OrdinalIgnoreCase);
                        while (r.Read())
                        {
                            var id = r.GetInt32(0);
                            var title = r[1] as string;
                            var description = r[2] as string;
                            var owner = r[3] as string;
                            var isSelected = selected != null && selected.Contains(id);
                            var isNotify = notify != null && notify.Contains(id);

                            if (!string.IsNullOrWhiteSpace(title) && titleMap.TryGetValue(title, out var existing))
                            {
                                if (isSelected)
                                    existing.IsSelected = true;
                                continue;
                            }

                            var item = new ProblemItem
                            {
                                ProblemId = id,
                                Title = title,
                                Description = description,
                                Owner = owner,
                                IsSelected = isSelected,
                                Notify = isNotify
                            };
                            if (!string.IsNullOrWhiteSpace(title))
                                titleMap[title] = item;
                            items.Add(item);
                        }
                    }
                }
            }
            catch
            {
            }
            return items;
        }
    }
}
