using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web;
using System.Web.Security;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace Core
{
    /// <summary>
    /// Class to provide a unified area of authentication/authorization checking.
    /// </summary>
    public partial class Security : IHttpModule
    {
        static Security()
        {

        }

        /// <summary>
        /// Disposes of the resources (other than memory) used by the module that implements <see cref="T:System.Web.IHttpModule"/>.
        /// </summary>
        public void Dispose()
        {
            // Nothing to dispose
        }

        /// <summary>
        /// Initializes a module and prepares it to handle requests.
        /// </summary>
        /// <param name="context">An <see cref="T:System.Web.HttpApplication"/> that provides access to the methods, properties, and events common to all application objects within an ASP.NET application</param>
        public void Init(HttpApplication context)
        {
            context.AuthenticateRequest += ContextAuthenticateRequest;
        }

        /// <summary>
        /// Handles the AuthenticateRequest event of the context control.
        /// </summary>
        /// <param name="sender">
        /// The source of the event.
        /// </param>
        /// <param name="e">
        /// The <see cref="System.EventArgs"/> instance containing the event data.
        /// </param>
        private static void ContextAuthenticateRequest(object sender, EventArgs e)
        {
            var context = ((HttpApplication)sender).Context;

            // FormsAuthCookieName is a custom cookie name based on the current instance.
            HttpCookie authCookie = context.Request.Cookies[FormsAuthCookieName];
            if (authCookie != null)
            {
                FormsAuthenticationTicket authTicket = null;
                try
                {
                    authTicket = FormsAuthentication.Decrypt(authCookie.Value);
                }
                catch (ArgumentException)
                {
                    // Cookie value may be invalid; FormsAuthentication.Decrypt can throw and return null.
                }
                catch (HttpException)
                {
                    // Cookie value may be invalid; FormsAuthentication.Decrypt can throw and return null.
                    /*
                     * Unable to validate data. 
 Description: An unhandled exception occurred during the execution of the current web request. Please review the stack trace for more information about the error and where it originated in the code. 

 Exception Details: System.Web.HttpException: Unable to validate data.

Source Error: 
 An unhandled exception was generated during the execution of the current web request. Information regarding the origin and location of the exception can be identified using the exception stack trace below. 


Stack Trace: 

[HttpException (0x80004005): Unable to validate data.]
   System.Web.Configuration.MachineKeySection.EncryptOrDecryptData(Boolean fEncrypt, Byte[] buf, Byte[] modifier, Int32 start, Int32 length, Boolean useValidationSymAlgo, Boolean useLegacyMode, IVType ivType, Boolean signData) +4846871
   System.Web.Configuration.MachineKeySection.EncryptOrDecryptData(Boolean fEncrypt, Byte[] buf, Byte[] modifier, Int32 start, Int32 length, Boolean useValidationSymAlgo, Boolean useLegacyMode, IVType ivType) +155
   System.Web.Security.FormsAuthentication.Decrypt(String encryptedTicket) +293
                   BlogEngine.Core.Security.ContextAuthenticateRequest(Object sender, EventArgs e) in <source>:64
   System.Web.SyncEventExecutionStep.System.Web.HttpApplication.IExecutionStep.Execute() +80
   System.Web.HttpApplication.ExecuteStep(IExecutionStep step, Boolean& completedSynchronously) +266
                     * (/
                     */
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("Failed to decrypt the FormsAuthentication cookie, signing out to avoid future failure attempts." + ex);
                    SignOut();
                }


                // for extra security, make sure the UserData matches the current blog instance.
                // this would prevent a cookie name change for a forms auth cookie encrypted in
                // the same application (different blog) as being valid for this blog instance.
                if (authTicket != null && !string.IsNullOrWhiteSpace(authTicket.UserData) && authTicket.UserData.Equals(HttpContext.Current.Request.Url.Host, StringComparison.OrdinalIgnoreCase))
                {
                    CustomIdentity identity = new CustomIdentity(authTicket.Name, true);
                    CustomPrincipal principal = new CustomPrincipal(identity);

                    context.User = principal;
                    return;
                }
            }

            // need to create an empty/unauthenticated user to assign to context.User.
            CustomIdentity unauthIdentity = new CustomIdentity(string.Empty, false);
            CustomPrincipal unauthPrincipal = new CustomPrincipal(unauthIdentity);
            context.User = unauthPrincipal;
        }

        /// <summary>
        /// Name of the Forms authentication cookie for the current blog instance.
        /// </summary>
        public static string FormsAuthCookieName
        {
            get
            {
                return FormsAuthentication.FormsCookieName + "-" + HttpContext.Current.Request.Url.Host;
            }
        }

        /// <summary>
        /// Signs out user out of the current blog instance.
        /// </summary>
        public static void SignOut()
        {
            // using a custom cookie name based on the current blog instance.
            var cookie = new HttpCookie(FormsAuthCookieName, string.Empty);
            cookie.Expires = DateTime.Now.AddYears(-3);
            HttpContext.Current.Response.Cookies.Add(cookie);

            // admin UI injects user info in JavaScript resource file
            // so it can be used in Angular scripts. Crear cache on log off.
            string cacheKey = "admin.resource.axd - ru-RU";
            HttpContext.Current.Cache.Remove(cacheKey);
        }

        public static bool AuthenticateUser(string username, string password, bool rememberMe)
        {
            string un = (username ?? string.Empty).Trim();
            string pw = (password ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(un) && !string.IsNullOrWhiteSpace(pw))
            {
                bool isValidated = Membership.ValidateUser(un, pw);

                if (isValidated)
                {
                    HttpContext context = HttpContext.Current;
                    DateTime expirationDate = DateTime.Now.Add(FormsAuthentication.Timeout);

                    FormsAuthenticationTicket ticket = new FormsAuthenticationTicket(
                        1,
                        un,
                        DateTime.Now,
                        expirationDate,
                        rememberMe,
                        HttpContext.Current.Request.Url.Host,
                        FormsAuthentication.FormsCookiePath
                    );

                    string encryptedTicket = FormsAuthentication.Encrypt(ticket);

                    // setting a custom cookie name based on the current blog instance.
                    // If rememberMe is false, leave Expires unset so the cookie is session-only.
                    HttpCookie cookie = new HttpCookie(FormsAuthCookieName, encryptedTicket)
                    {
                        Path = FormsAuthentication.FormsCookiePath,
                        HttpOnly = true
                    };
                    if (rememberMe)
                    {
                        cookie.Expires = expirationDate;
                    }
                    context.Response.Cookies.Set(cookie);

                    string returnUrl = context.Request.QueryString["ReturnUrl"];

                    // ignore Return URLs not beginning with a forward slash, such as remote sites.
                    if (string.IsNullOrWhiteSpace(returnUrl) ||
                        (!returnUrl.StartsWith("/") && !returnUrl.Contains("chat.t30p.ru")))
                        returnUrl = null;

                    if (!string.IsNullOrWhiteSpace(returnUrl))
                    {
                        context.Response.Redirect(returnUrl);
                    }
                    else
                    {
                        context.Response.Redirect("/index.cshtml");
                    }

                    return true;
                }
            }

            return false;
        }

        #region "Properties"

        /// <summary>
        /// If the current user is authenticated, returns the current MembershipUser. If not, returns null. This is just a shortcut to Membership.GetUser().
        /// </summary>
        public static MembershipUser CurrentMembershipUser
        {
            get
            {
                return Membership.GetUser();
            }
        }

        /// <summary>
        /// Gets the current user for the current HttpContext.
        /// </summary>
        /// <remarks>
        /// This should always return HttpContext.Current.User. That value and Thread.CurrentPrincipal can't be
        /// guaranteed to always be the same value, as they can be set independently from one another. Looking
        /// through the .Net source, the System.Web.Security.Roles class also returns the HttpContext's User.
        /// </remarks>
        public static System.Security.Principal.IPrincipal CurrentUser
        {
            get
            {
                return HttpContext.Current.User;
            }
        }

        /// <summary>
        /// Gets whether the current user is logged in.
        /// </summary>
        public static bool IsAuthenticated
        {
            get
            {
                return Security.CurrentUser.Identity.IsAuthenticated;
            }
        }

        /// <summary>
        /// Gets whether the current user is logged in.
        /// </summary>
        public static bool IsPaid
        {
            get
            {
                if (!IsAuthenticated) return false;
                var user = Membership.GetUser(Security.CurrentUser.Identity.Name);
                if (user == null) return false;
                return user.IsApproved;
            }
        }

        public static bool IsAdmin
        {
            get
            {
                if (!IsAuthenticated) return false;
                try
                {
                    var username = Security.CurrentUser.Identity.Name;
                    if (string.IsNullOrWhiteSpace(username)) return false;

                    var entity = ChatT30P.Core.MemberEntity.Load(username.ToLower());
                    if (entity == null) return false;
                    return string.Equals(entity.IsAdmin, "true", StringComparison.OrdinalIgnoreCase) || entity.IsAdmin == "1";
                }
                catch
                {
                    return false;
                }
            }
        }
 
        #endregion

        #region "Public Methods"

        public static void RedirectForUnauthorizedRequest()
        {
            HttpContext context = HttpContext.Current;
            Uri referrer = context.Request.UrlReferrer;
            bool isFromLoginPage = referrer != null && referrer.LocalPath.IndexOf("/Account/login.aspx", StringComparison.OrdinalIgnoreCase) != -1;

            // If the user was just redirected from the login page to the current page,
            // we will then redirect them to the homepage, rather than back to the
            // login page to prevent confusion.
            if (isFromLoginPage)
            {
                context.Response.Redirect("/");
            }
            else
            {
                context.Response.Redirect(string.Format("/Account/login.aspx?ReturnURL={0}", HttpUtility.UrlPathEncode(context.Request.RawUrl)));
                //var redirectUrl = HttpUtility.UrlPathEncode(Utils.ConvertToPublicUrl(context, context.Request.Url));
                //var url = string.Format("{0}Account/login.aspx?ReturnURL={1}", Utils.RelativeWebRoot, redirectUrl);
                //var uri = Utils.ConvertToPublicUrl(context, new Uri(url, UriKind.Relative));
                //context.Response.Redirect(uri);
            }
        }

        #endregion

    }
}

