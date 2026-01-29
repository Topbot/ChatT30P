using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Web.Http;
using System.Web.Optimization;
using System.Text;

namespace ChatT30P
{
    public class Global : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            GlobalConfiguration.Configure(WebApiConfig.Register);
            UnityConfig.Register(GlobalConfiguration.Configuration);
            BundleConfig.RegisterBundles(BundleTable.Bundles);
            //Enabling Bundling and Minification
            BundleTable.EnableOptimizations = true;
        }

        protected void Application_Error(object sender, EventArgs e)
        {
            try
            {
                var ex = Server.GetLastError();
                if (ex == null) return;

                var baseDir = Server?.MapPath("~/App_Data/logs") ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "logs");
                System.IO.Directory.CreateDirectory(baseDir);
                var path = System.IO.Path.Combine(baseDir, "application-unhandled-errors.log");
                System.IO.File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + ex + Environment.NewLine + Environment.NewLine, Encoding.UTF8);

                try
                {
                    var source = "ChatT30P";
                    if (!System.Diagnostics.EventLog.SourceExists(source))
                    {
                        System.Diagnostics.EventLog.CreateEventSource(source, "Application");
                    }
                    System.Diagnostics.EventLog.WriteEntry(source, ex.ToString(), System.Diagnostics.EventLogEntryType.Error);
                }
                catch
                {
                    // ignore EventLog failures
                }
            }
            catch
            {
            }
        }


        /// <summary>
        /// Sets the culture based on the language selection in the settings.
        /// </summary>
        void Application_PreRequestHandlerExecute(object sender, EventArgs e)
        {
            CultureInfo defaultCulture = CultureInfo.CurrentCulture;
            Thread.CurrentThread.CurrentUICulture = defaultCulture;
            Thread.CurrentThread.CurrentCulture = defaultCulture;
        }

        protected void Application_BeginRequest(Object sender, EventArgs e)
        {
            try
            {
                if (Request.IsLocal)
                {
                    return;
                }

                // If the request is already secure (directly) or the original scheme
                // as indicated by a proxy header is HTTPS, do not redirect.
                var forwardedProto = Request.Headers["X-Forwarded-Proto"];
                bool forwardedIsHttps = false;
                if (!string.IsNullOrEmpty(forwardedProto))
                {
                    // header can contain a comma-separated list, take the first value
                    var first = forwardedProto.Split(',')[0].Trim();
                    forwardedIsHttps = first.Equals("https", StringComparison.OrdinalIgnoreCase);
                }

                if (!Request.IsSecureConnection && !forwardedIsHttps)
                {
                    string redirectUrl = Request.Url.ToString().Replace("http:", "https:");
                    Response.Redirect(redirectUrl, true);
                }
            }
            catch
            {

            }
        }
    }
}

