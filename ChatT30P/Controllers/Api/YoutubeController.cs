using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Web.Http;
using BlogsAPI;
using BlogsAPI.Sites;
using RssToolkit.Rss;
using ChatT30P.Contracts;
using ChatT30P.Controllers.Models;
using TopTools;
using Twitterizer;
using WebApi.OutputCache.V2;

public class YoutubeController : ApiController
{
    readonly IYoutubeRepository repository;

    public YoutubeController(IYoutubeRepository repository)
    {
        this.repository = repository;
    }

    [CacheOutput(ClientTimeSpan = 86400, ServerTimeSpan = 86400)]
    public IEnumerable<YoutubeItem> Get(int type = 1, int take = 0, int skip = 0,
        int min = 0, int max = int.MaxValue,
        string filter = "", string order = "followers descending")
    {
        try
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            throw new HttpResponseException(HttpStatusCode.Unauthorized);
        }
        catch (Exception e1)
        {
            Trace.Write(e1, GetType().Name);
            throw new HttpResponseException(HttpStatusCode.InternalServerError);
        }
    }

    /// <summary>
    /// ??? ???????? ?????? ?? ????? - ????? ???????? ?? ? ????
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    [HttpPost]
    public HttpResponseMessage Post(dynamic data)
    {
        try
        {
            var bloglink = Common.FixHttp(data.id.Value.Trim());
            string username;
            var yt = new YouTube();
            Uri uri = null;
            if (bloglink.Contains("youtube.com/@") && Uri.TryCreate(bloglink,UriKind.Absolute,out uri))
            {
                //???????? ??? ??????
                using (var client = new WebClient() { Encoding = System.Text.Encoding.UTF8 })
                {
                    var page = client.DownloadString(uri.OriginalString);
                    Regex rID = new Regex(@"(?:channel.external.id|externalId)\W+([^""]+)\W",
                        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    var m = rID.Match(page);
                    if (m.Success)
                    {
                        bloglink = yt.GetBlogLink(m.Groups[1].Value);
                    }
                }
            }

            if (yt.ValidateLink(bloglink, out username))
            {
                YouTubeStatistics user = yt.GetStats(username);
                if (user != null && !String.IsNullOrEmpty(user.Name))
                {
                    using (var db = new t30pDataContext())
                    {
                        //status = 1 ?????? ????????
                        top_youtube item = YoutubeIndex.InsertOrUpdate(db, user);
                    }
                    return Request.CreateResponse(HttpStatusCode.OK);
                }
            }
            return Request.CreateResponse(HttpStatusCode.BadRequest);
        }
        catch (UnauthorizedAccessException)
        {
            return Request.CreateResponse(HttpStatusCode.Unauthorized);
        }
        catch (Exception e1)
        {
            Trace.Write(e1);
            return Request.CreateResponse(HttpStatusCode.InternalServerError);
        }
    }

    [HttpPut]
    public HttpResponseMessage ProcessChecked([FromBody]List<YoutubeItem> items)
    {
        try
        {
            // ???? ?? ????????? ???? ?? ????? ??????
            if (items == null || items.Count == 0)
                throw new HttpResponseException(HttpStatusCode.ExpectationFailed);

            var action = Request.GetRouteData().Values["id"].ToString();

            if (action.ToLower() == "delete")
            {
                var ids = items.Aggregate("'", (current, b) => current + "','" + b.Id) + "'";
                using (var db = new t30pDataContext())
                {
                    db.ExecuteCommand(String.Format("DELETE FROM [arme].[top_youtube] WHERE id in ({0})", ids));
                }
                Trace.WriteLine("Deleted: " + ids);
            }
            return Request.CreateResponse(HttpStatusCode.OK);
        }
        catch (UnauthorizedAccessException)
        {
            return Request.CreateResponse(HttpStatusCode.Unauthorized);
        }
        catch (Exception e1)
        {
            Trace.Write(e1);
            return Request.CreateResponse(HttpStatusCode.InternalServerError);
        }
    }
}

