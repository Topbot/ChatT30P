using Core;

namespace Account
{
    using System;
    using System.Web;
    using System.Web.Security;
    using System.Web.UI;
    using System.Web.UI.WebControls;

    using Resources;

    /// <summary>
    /// The login.
    /// </summary>
    public partial class Login : System.Web.UI.Page
    {
        #region Methods

        /// <summary>
        /// Handles the Load event of the Page control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected void Page_Load(object sender, EventArgs e)
        {
            var linkForgotPassword = (HyperLink)LoginUser.FindControl("linkForgotPassword");
            if (linkForgotPassword != null)
            {
                linkForgotPassword.NavigateUrl = "/Account/password-retrieval.aspx";
            }

            this.RegisterHyperLink.NavigateUrl = "/Account/register.aspx?ReturnUrl=" +
                                                 HttpUtility.UrlEncode(this.Request.QueryString["ReturnUrl"]);
            this.RegisterHyperLink.Text = labels.createNow;
            ((PlaceHolder)LoginUser.FindControl("phResetPassword")).Visible = false;

            if (this.Request.QueryString.ToString() == "logoff")
            {
                Security.SignOut();
                if (this.Request.UrlReferrer != null && this.Request.UrlReferrer != this.Request.Url && this.Request.UrlReferrer.LocalPath.IndexOf("/admin/", StringComparison.OrdinalIgnoreCase) == -1)
                {
                    this.Response.Redirect(this.Request.UrlReferrer.ToString(), true);
                }
                else
                {
                    this.Response.Redirect("/");
                }

                return;
            }

            if (!this.Page.IsPostBack || Security.IsAuthenticated)
            {
                return;
            }

            this.Master.SetStatus("warning", Resources.labels.loginFailed);
        }

        protected void LoginUser_OnAuthenticate(object sender, AuthenticateEventArgs e)
        {
            // always set to false
            e.Authenticated = false;

            Security.AuthenticateUser(LoginUser.UserName, LoginUser.Password, LoginUser.RememberMeSet);
        }

        #endregion
    }
}
