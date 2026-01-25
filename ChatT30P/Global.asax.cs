using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Web.Http;
using System.Web.Optimization;

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
                if (!Request.IsLocal && !Request.IsSecureConnection)
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

