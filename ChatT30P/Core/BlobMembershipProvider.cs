using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using ChatT30P.Core;

namespace Core
{
    using System;
    using System.Collections.Specialized;
    using System.Configuration;
    using System.Configuration.Provider;
    using System.Data;
    using System.Data.Common;
    using System.Web.Security;

    /// <summary>
    /// Generic Db Membership Provider
    /// </summary>
    public class BlobMembershipProvider : MembershipProvider
    {
        /// <summary>
        /// ???????, ??? ????????
        /// </summary>
        private const string TableName = "socialt30pru";

        #region Constants and Fields

        /// <summary>
        /// The application name.
        /// </summary>
        private string applicationName;

        /// <summary>
        /// The password format.
        /// </summary>
        private MembershipPasswordFormat passwordFormat;


        #endregion

        #region Properties

        /// <summary>
        ///     Returns the application name as set in the web.config
        ///     otherwise returns BlogEngine.  Set will throw an error.
        /// </summary>
        public override string ApplicationName
        {
            get
            {
                return this.applicationName;
            }

            set
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>
        ///     Hardcoded to false
        /// </summary>
        public override bool EnablePasswordReset
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        ///     Can password be retrieved via email?
        /// </summary>
        public override bool EnablePasswordRetrieval
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        ///     Hardcoded to 5
        /// </summary>
        public override int MaxInvalidPasswordAttempts
        {
            get
            {
                return 5;
            }
        }

        /// <summary>
        ///     Hardcoded to 0
        /// </summary>
        public override int MinRequiredNonAlphanumericCharacters
        {
            get
            {
                return 0;
            }
        }

        /// <summary>
        ///     Hardcoded to 4
        /// </summary>
        public override int MinRequiredPasswordLength
        {
            get
            {
                return 4;
            }
        }

        /// <summary>
        ///     Not implemented
        /// </summary>
        public override int PasswordAttemptWindow
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        ///     Password format (Clear or Hashed)
        /// </summary>
        public override MembershipPasswordFormat PasswordFormat
        {
            get
            {
                return this.passwordFormat;
            }
        }

        /// <summary>
        ///     Not Implemented
        /// </summary>
        public override string PasswordStrengthRegularExpression
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        ///     Hardcoded to false
        /// </summary>
        public override bool RequiresQuestionAndAnswer
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        ///     Hardcoded to false
        /// </summary>
        public override bool RequiresUniqueEmail
        {
            get
            {
                return false;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Change the password if the old password matches what is stored
        /// </summary>
        /// <param name="username">The user to update the password for.</param>
        /// <param name="oldPassword">The current password for the specified user.</param>
        /// <param name="newPassword">The new password for the specified user.</param>
        /// <returns>The change password.</returns>
        public override bool ChangePassword(string username, string oldPassword, string newPassword)
        {
            var oldPasswordCorrect = false;
            var success = false;

            //var tbl = TopTools.Azure.BlobStorage.Table(TableName);
            //IQueryable<MemberEntity> q = tbl.CreateQuery<MemberEntity>();
            //var user = (from i in q where i.PartitionKey == HttpContext.Current.Request.Url.Host 
            // && i.RowKey == username select i).FirstOrDefault();
            var user = MemberEntity.Load(username);
            if (user != null)
            {
                var actualPassword = user.Password;
                if (actualPassword == string.Empty)
                {
                    // This is a special case used for resetting.
                    if (oldPassword.ToLower() == "admin")
                    {
                        oldPasswordCorrect = true;
                    }
                }
                else
                {
                    if (this.passwordFormat == MembershipPasswordFormat.Hashed)
                    {
                        if (actualPassword == HashPassword(oldPassword))
                        {
                            oldPasswordCorrect = true;
                        }
                    }
                    else if (actualPassword == oldPassword)
                    {
                        oldPasswordCorrect = true;
                    }
                }
                // Update New Password
                if (oldPasswordCorrect)
                {
                    user.Password = (this.passwordFormat == MembershipPasswordFormat.Hashed ? 
                        HashPassword(newPassword) : newPassword);
                    //tbl.Execute(TableOperation.Replace(user));
                    user.Save();
                    success = true;
                }
            }
            //using (var conn = this.CreateConnection())
            //{
            //    if (conn.HasConnection)
            //    {
            //        using (var cmd = conn.CreateTextCommand(string.Format("SELECT password FROM {0}Users WHERE BlogID = {1}blogid AND userName = {1}name", this.tablePrefix, this.parmPrefix)))
            //        {
            //            // Check Old Password

            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("blogid"), Blog.CurrentInstance.Id.ToString()));
            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("name"), username));

            //            using (var rdr = cmd.ExecuteReader())
            //            {
            //                if (rdr.Read())
            //                {
            //                    var actualPassword = rdr.GetString(0);
            //                    if (actualPassword == string.Empty)
            //                    {
            //                        // This is a special case used for resetting.
            //                        if (oldPassword.ToLower() == "admin")
            //                        {
            //                            oldPasswordCorrect = true;
            //                        }
            //                    }
            //                    else
            //                    {
            //                        if (this.passwordFormat == MembershipPasswordFormat.Hashed)
            //                        {
            //                            if (actualPassword == Utils.HashPassword(oldPassword))
            //                            {
            //                                oldPasswordCorrect = true;
            //                            }
            //                        }
            //                        else if (actualPassword == oldPassword)
            //                        {
            //                            oldPasswordCorrect = true;
            //                        }
            //                    }
            //                }
            //            }

            //            // Update New Password
            //            if (oldPasswordCorrect)
            //            {
            //                cmd.CommandText = string.Format("UPDATE {0}Users SET password = {1}pwd WHERE BlogID = {1}blogid AND userName = {1}name", this.tablePrefix, this.parmPrefix);

            //                cmd.Parameters.Add(conn.CreateParameter(FormatParamName("pwd"), (this.passwordFormat == MembershipPasswordFormat.Hashed ? Utils.HashPassword(newPassword) : newPassword)));

            //                cmd.ExecuteNonQuery();
            //                success = true;
            //            }
            //        }
            //    }
            //}

            return success;
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="username">The user to change the password question and answer for.</param>
        /// <param name="password">The password for the specified user.</param>
        /// <param name="newPasswordQuestion">The new password question for the specified user.</param>
        /// <param name="newPasswordAnswer">The new password answer for the specified user.</param>
        /// <returns>The change password question and answer.</returns>
        public override bool ChangePasswordQuestionAndAnswer(
            string username, string password, string newPasswordQuestion, string newPasswordAnswer)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Add new user to database
        /// </summary>
        /// <param name="username">The user name for the new user.</param>
        /// <param name="password">The password for the new user.</param>
        /// <param name="email">The e-mail address for the new user.</param>
        /// <param name="passwordQuestion">The password question for the new user.</param>
        /// <param name="passwordAnswer">The password answer for the new user</param>
        /// <param name="approved">Whether or not the new user is approved to be validated.</param>
        /// <param name="providerUserKey">The unique identifier from the membership data source for the user.</param>
        /// <param name="status">A <see cref="T:System.Web.Security.MembershipCreateStatus"/> enumeration value indicating whether the user was created successfully.</param>
        /// <returns>
        /// A <see cref="T:System.Web.Security.MembershipUser"/> object populated with the information for the newly created user.
        /// </returns>
        public override MembershipUser CreateUser(
            string username,
            string password,
            string email,
            string passwordQuestion,
            string passwordAnswer,
            bool approved,
            object providerUserKey,
            out MembershipCreateStatus status)
        {
            username = username.ToLower();
            var mmr = new MemberEntity(username)
            {
                Password = (this.passwordFormat == MembershipPasswordFormat.Hashed ? HashPassword(password) : password),
                Ip = HttpContext.Current.Request.UserHostAddress,
                Email = email.ToLower(),
            };
            mmr.Save();
            //var tbl = TopTools.Azure.BlobStorage.Table(TableName);
            //tbl.Execute(TableOperation.Insert(new MemberEntity(HttpContext.Current.Request.Url.Host, username)
            //{
            //    Password = (this.passwordFormat == MembershipPasswordFormat.Hashed ? HashPassword(password) : password),
            //    Ip = HttpContext.Current.Request.UserHostAddress,
            //    Email = email.ToLower(),
            //}));
            MembershipUser user = this.GetMembershipUser(username, email, DateTime.Now);
            status = MembershipCreateStatus.Success;

            return user;
        }

        /// <summary>
        /// Delete user from database
        /// </summary>
        /// <param name="username">The name of the user to delete.</param>
        /// <param name="deleteAllRelatedData">true to delete data related to the user from the database; false to leave data related to the user in the database.</param>
        /// <returns>The delete user.</returns>
        public override bool DeleteUser(string username, bool deleteAllRelatedData)
        {
            bool success = false;

            //var tbl = TopTools.Azure.BlobStorage.Table(TableName);
            //IQueryable<MemberEntity> q = tbl.CreateQuery<MemberEntity>();
            //var user = (from i in q
            //            where i.PartitionKey == HttpContext.Current.Request.Url.Host
            //                && i.RowKey == username
            //            select i).FirstOrDefault();
            var user = MemberEntity.Load(username);
            if (user != null)
            {
                user.Delete();
                //tbl.Execute(TableOperation.Delete(user));
                success = true;
            }

            //using (var conn = this.CreateConnection())
            //{
            //    if (conn.HasConnection)
            //    {
            //        using (var cmd = conn.CreateTextCommand(string.Format("DELETE FROM {0}Users WHERE blogId = {1}blogid AND userName = {1}name", this.tablePrefix, this.parmPrefix)))
            //        {
            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("blogid"), Blog.CurrentInstance.Id.ToString()));
            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("name"), username));

            //            try
            //            {
            //                cmd.ExecuteNonQuery();
            //                success = true;
            //            }
            //            catch (Exception ex)
            //            {
            //                success = false;
            //                throw;
            //            }
            //        }
            //    }
            //}

            return success;
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="emailToMatch">The e-mail address to search for.</param>
        /// <param name="pageIndex">The index of the page of results to return. <paramref name="pageIndex"/> is zero-based.</param>
        /// <param name="pageSize">The size of the page of results to return.</param>
        /// <param name="totalRecords">The total number of matched users.</param>
        /// <returns>
        /// A <see cref="T:System.Web.Security.MembershipUserCollection"/> collection that contains a page of <paramref name="pageSize"/><see cref="T:System.Web.Security.MembershipUser"/> objects beginning at the page specified by <paramref name="pageIndex"/>.
        /// </returns>
        public override MembershipUserCollection FindUsersByEmail(
            string emailToMatch, int pageIndex, int pageSize, out int totalRecords)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="usernameToMatch">The user name to search for.</param>
        /// <param name="pageIndex">The index of the page of results to return. <paramref name="pageIndex"/> is zero-based.</param>
        /// <param name="pageSize">The size of the page of results to return.</param>
        /// <param name="totalRecords">The total number of matched users.</param>
        /// <returns>
        /// A <see cref="T:System.Web.Security.MembershipUserCollection"/> collection that contains a page of <paramref name="pageSize"/><see cref="T:System.Web.Security.MembershipUser"/> objects beginning at the page specified by <paramref name="pageIndex"/>.
        /// </returns>
        public override MembershipUserCollection FindUsersByName(
            string usernameToMatch, int pageIndex, int pageSize, out int totalRecords)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Return all users in MembershipUserCollection
        /// </summary>
        /// <param name="pageIndex">The index of the page of results to return. <paramref name="pageIndex"/> is zero-based.</param>
        /// <param name="pageSize">The size of the page of results to return.</param>
        /// <param name="totalRecords">The total number of matched users.</param>
        /// <returns>
        /// A <see cref="T:System.Web.Security.MembershipUserCollection"/> collection that contains a page of <paramref name="pageSize"/><see cref="T:System.Web.Security.MembershipUser"/> objects beginning at the page specified by <paramref name="pageIndex"/>.
        /// </returns>
        public override MembershipUserCollection GetAllUsers(int pageIndex, int pageSize, out int totalRecords)
        {
            var users = new MembershipUserCollection();
            var data = MemberEntity.GetAllUsers();
            foreach (var memberEntity in data)
            {
                users.Add(this.GetMembershipUser(memberEntity.RowKey,memberEntity.Email,memberEntity.Timestamp.Date));
            }
            //using (var conn = this.CreateConnection())
            //{
            //    if (conn.HasConnection)
            //    {
            //        using (var cmd = conn.CreateTextCommand(string.Format("SELECT username, EmailAddress, lastLoginTime FROM {0}Users WHERE BlogID = {1}blogid ", this.tablePrefix, this.parmPrefix)))
            //        {
            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("blogid"), Blog.CurrentInstance.Id.ToString()));

            //            using (var rdr = cmd.ExecuteReader())
            //            {
            //                while (rdr.Read())
            //                {
            //                    users.Add(this.GetMembershipUser(rdr.GetString(0), rdr.GetString(1), rdr.GetDateTime(2)));
            //                }
            //            }
            //        }
            //    }
            //}

            totalRecords = users.Count;
            return users;
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <returns>
        /// The get number of users online.
        /// </returns>
        public override int GetNumberOfUsersOnline()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="username">The user to retrieve the password for.</param>
        /// <param name="answer">The password answer for the user.</param>
        /// <returns>The get password.</returns>
        public override string GetPassword(string username, string answer)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Gets user information from the data source based on the unique identifier for the membership user. Provides an option to update the last-activity date/time stamp for the user.
        /// </summary>
        /// <param name="providerUserKey">The unique identifier for the membership user to get information for.</param>
        /// <param name="userIsOnline">true to update the last-activity date/time stamp for the user; false to return user information without updating the last-activity date/time stamp for the user.</param>
        /// <returns>
        /// A <see cref="T:System.Web.Security.MembershipUser"/> object populated with the specified user's information from the data source.
        /// </returns>
        public override MembershipUser GetUser(object providerUserKey, bool userIsOnline)
        {
            return this.GetUser(providerUserKey.ToString(), userIsOnline);
        }

        /// <summary>
        /// Gets information from the data source for a user. Provides an option to update the last-activity date/time stamp for the user.
        /// </summary>
        /// <param name="username">The name of the user to get information for.</param>
        /// <param name="userIsOnline">true to update the last-activity date/time stamp for the user; false to return user information without updating the last-activity date/time stamp for the user.</param>
        /// <returns>
        /// A <see cref="T:System.Web.Security.MembershipUser"/> object populated with the specified user's information from the data source.
        /// </returns>
        public override MembershipUser GetUser(string username, bool userIsOnline)
        {
            MembershipUser user = null;


            if (username.Contains('@'))
            {
                username = GetUserNameByEmail(username)?? username;
            }

            var memberEntity = MemberEntity.Load(username.ToLower());
            //var tbl = TopTools.Azure.BlobStorage.Table(TableName);
            //IQueryable<MemberEntity> q = tbl.CreateQuery<MemberEntity>();
            //var memberEntity = (from i in q
            //    where i.PartitionKey == HttpContext.Current.Request.Url.Host
            //    && i.RowKey == username.ToLower()
            //    select i).FirstOrDefault();            
            if (memberEntity != null)
            {
                user = this.GetMembershipUser(memberEntity.RowKey, memberEntity.Email, memberEntity.Timestamp.Date);
                user.IsApproved = !String.IsNullOrEmpty(memberEntity.IsPaid);
            }

            //using (var conn = this.CreateConnection())
            //{
            //    if (conn.HasConnection)
            //    {
            //        using (var cmd = conn.CreateTextCommand(string.Format("SELECT username, EmailAddress, lastLoginTime FROM {0}Users WHERE BlogID = {1}blogid AND UserName = {1}name", this.tablePrefix, this.parmPrefix)))
            //        {
            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("blogid"), Blog.CurrentInstance.Id.ToString()));
            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("name"), username));

            //            using (var rdr = cmd.ExecuteReader())
            //            {
            //                if (rdr.Read())
            //                {
            //                    user = this.GetMembershipUser(username, rdr.GetString(1), rdr.GetDateTime(2));
            //                }
            //            }
            //        }
            //    }
            //}

            return user;
        }

        /// <summary>
        /// Gets the user name associated with the specified e-mail address.
        /// </summary>
        /// <param name="email">The e-mail address to search for.</param>
        /// <returns>
        /// The user name associated with the specified e-mail address. If no match is found, return null.
        /// </returns>
        public override string GetUserNameByEmail(string email)
        {
            if (email == null)
            {
                throw new ArgumentNullException("email");
            }

            string userName = null;
            var memberEntity = MemberEntity.GetAllUsers().FirstOrDefault(i => i.Email == email.ToLower());
            //var tbl = TopTools.Azure.BlobStorage.Table(TableName);
            //IQueryable<MemberEntity> q = tbl.CreateQuery<MemberEntity>();
            //var memberEntity = (from i in q
            //                    where i.PartitionKey == HttpContext.Current.Request.Url.Host
            //                    && i.Email == email.ToLower()
            //                    select i).FirstOrDefault();
            if (memberEntity != null)
            {
                userName = memberEntity.RowKey;
            }
            //using (var conn = this.CreateConnection())
            //{
            //    if (conn.HasConnection)
            //    {
            //        using (var cmd = conn.CreateTextCommand(string.Format("SELECT userName FROM {0}Users WHERE BlogID = {1}blogid AND emailAddress = {1}email", this.tablePrefix, this.parmPrefix)))
            //        {
            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("blogid"), Blog.CurrentInstance.Id.ToString()));
            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("email"), email));

            //            using (var rdr = cmd.ExecuteReader())
            //            {
            //                if (rdr.Read())
            //                {
            //                    userName = rdr.GetString(0);
            //                }
            //            }
            //        }
            //    }
            //}

            return userName;
        }

        /// <summary>
        /// Initializes the provider
        /// </summary>
        /// <param name="name">
        /// Configuration name
        /// </param>
        /// <param name="config">
        /// Configuration settings
        /// </param>
        public override void Initialize(string name, NameValueCollection config)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            if (string.IsNullOrEmpty(name))
            {
                name = "BlobMembershipProvider";
            }

                if (string.IsNullOrEmpty(config["description"]))
                {
                    config.Remove("description");
                    config.Add("description", "Generic BlobStorage Membership Provider");
                }

            base.Initialize(name, config);

            // Application Name
            if (config["applicationName"] == null)
            {
                config["applicationName"] = "BlogEngine";
            }

            this.applicationName = config["applicationName"];
            config.Remove("applicationName");

            // Password Format
            if (config["passwordFormat"] == null)
            {
                config["passwordFormat"] = "Hashed";
                this.passwordFormat = MembershipPasswordFormat.Hashed;
            }
            else if (string.Compare(config["passwordFormat"], "clear", true) == 0)
            {
                this.passwordFormat = MembershipPasswordFormat.Clear;
            }
            else
            {
                this.passwordFormat = MembershipPasswordFormat.Hashed;
            }

            config.Remove("passwordFormat");

            // Throw an exception if unrecognized attributes remain
            if (config.Count > 0)
            {
                var attr = config.GetKey(0);
                if (!string.IsNullOrEmpty(attr))
                {
                    throw new ProviderException(string.Format("Unrecognized attribute: {0}", attr));
                }
            }
        }

        /// <summary>
        /// Resets a user's password to a new, automatically generated password.
        /// </summary>
        /// <param name="username">The user to reset the password for.</param>
        /// <param name="answer">The password answer for the specified user.</param>
        /// <returns>The new password for the specified user.</returns>
        public override string ResetPassword(string username, string answer)
        {
            if (string.IsNullOrEmpty(username))
            {
                return string.Empty;
            }

            //var oldPassword = string.Empty;
            //var randomPassword = Utils.RandomPassword();

            //using (var conn = this.CreateConnection())
            //{
            //    if (conn.HasConnection)
            //    {

            //        using (var cmd = conn.CreateTextCommand(string.Format("SELECT password FROM {0}Users WHERE BlogID = {1}blogid AND userName = {1}name", this.tablePrefix, this.parmPrefix)))
            //        {
            //            // Check Old Password

            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("blogid"), Blog.CurrentInstance.Id.ToString()));
            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("name"), username));

            //            using (var rdr = cmd.ExecuteReader())
            //            {
            //                if (rdr.Read())
            //                {
            //                    oldPassword = rdr.GetString(0);
            //                }
            //            }

            //            // Update Password
            //            if (!string.IsNullOrEmpty(oldPassword))
            //            {
            //                cmd.CommandText = string.Format("UPDATE {0}Users SET password = {1}pwd WHERE BlogID = {1}blogid AND userName = {1}name", this.tablePrefix, this.parmPrefix);

            //                cmd.Parameters.Add(conn.CreateParameter(FormatParamName("pwd"), (this.passwordFormat == MembershipPasswordFormat.Hashed ? Utils.HashPassword(randomPassword) : randomPassword)));

            //                cmd.ExecuteNonQuery();
            //                return randomPassword;
            //            }
            //        }
            //    }
            //}

            return string.Empty;
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="userName">The membership user whose lock status you want to clear.</param>
        /// <returns>The unlock user.</returns>
        public override bool UnlockUser(string userName)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Update User Data (not password)
        /// </summary>
        /// <param name="user">A <see cref="T:System.Web.Security.MembershipUser"/> object that represents the user to update and the updated information for the user.</param>
        public override void UpdateUser(MembershipUser user)
        {
            var entity = MemberEntity.Load(user.UserName.ToLower());
            //var tbl = TopTools.Azure.BlobStorage.Table(TableName);
            //IQueryable<MemberEntity> q = tbl.CreateQuery<MemberEntity>();
            //var entity = (from i in q
            //            where i.PartitionKey == HttpContext.Current.Request.Url.Host
            //                && i.RowKey == user.UserName.ToLower()
            //            select i).FirstOrDefault();
            if (entity != null)
            {
                entity.Email = user.Email;
                entity.Save();
                //tbl.Execute(TableOperation.Replace(entity));
            }
            //using (var conn = this.CreateConnection())
            //{
            //    if (conn.HasConnection)
            //    {
            //        using (var cmd = conn.CreateTextCommand(string.Format("UPDATE {0}Users SET emailAddress = {1}email WHERE BlogId = {1}blogId AND userName = {1}name", this.tablePrefix, this.parmPrefix)))
            //        {
            //            var parms = cmd.Parameters;
            //            parms.Add(conn.CreateParameter(FormatParamName("blogid"), Blog.CurrentInstance.Id.ToString()));
            //            parms.Add(conn.CreateParameter(FormatParamName("name"), user.UserName));
            //            parms.Add(conn.CreateParameter(FormatParamName("email"), user.Email));

            //            cmd.ExecuteNonQuery();
            //        }
            //    }
            //}
        }

        /// <summary>
        /// Check username and password
        /// </summary>
        /// <param name="username">The name of the user to validate.</param>
        /// <param name="password">The password for the specified user.</param>
        /// <returns>The validate user.</returns>
        public override bool ValidateUser(string username, string password)
        {
            var validated = false;

            if (username.Contains('@'))
            {
                username = GetUserNameByEmail(username)??username;
            }

            var user = MemberEntity.Load(username.ToLower());
            //var tbl = TopTools.Azure.BlobStorage.Table(TableName);
            //IQueryable<MemberEntity> q = tbl.CreateQuery<MemberEntity>();
            //var user = (from i in q where i.PartitionKey == HttpContext.Current.Request.Url.Host 
            // && i.RowKey == username.ToLower() select i).FirstOrDefault();
            if (user != null)
            {
                var storedPwd = user.Password;
                if (storedPwd == string.Empty)
                {
                    // This is a special case used for resetting.
                    if (password.ToLower() == "admin")
                    {
                        validated = true;
                    }
                }
                else
                {
                    if (this.passwordFormat == MembershipPasswordFormat.Hashed)
                    {
                        if (storedPwd == HashPassword(password))
                        {
                            validated = true;
                        }
                    }
                    else if (storedPwd == password)
                    {
                        validated = true;
                    }
                }
            }
            //using (var conn = this.CreateConnection())
            //{
            //    if (conn.HasConnection)
            //    {
            //        using (var cmd = conn.CreateTextCommand(string.Format("SELECT password FROM {0}Users WHERE BlogID = {1}blogid AND UserName = {1}name", this.tablePrefix, this.parmPrefix)))
            //        {
            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("blogid"), Blog.CurrentInstance.Id.ToString()));
            //            cmd.Parameters.Add(conn.CreateParameter(FormatParamName("name"), username));

            //            using (var rdr = cmd.ExecuteReader())
            //            {
            //                if (rdr.Read())
            //                {
            //                    var storedPwd = rdr.GetString(0);

            //                    if (storedPwd == string.Empty)
            //                    {
            //                        // This is a special case used for resetting.
            //                        if (password.ToLower() == "admin")
            //                        {
            //                            validated = true;
            //                        }
            //                    }
            //                    else
            //                    {
            //                        if (this.passwordFormat == MembershipPasswordFormat.Hashed)
            //                        {
            //                            if (storedPwd == Utils.HashPassword(password))
            //                            {
            //                                validated = true;
            //                            }
            //                        }
            //                        else if (storedPwd == password)
            //                        {
            //                            validated = true;
            //                        }
            //                    }
            //                }
            //            }
            //        }
            //    }
            //}

            return validated;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Encrypts a string using the SHA256 algorithm.
        /// </summary>
        /// <param name="plainMessage">
        /// The plain Message.
        /// </param>
        /// <returns>
        /// The hash password.
        /// </returns>
        public static string HashPassword(string plainMessage)
        {
            var data = Encoding.UTF8.GetBytes(plainMessage);
            using (HashAlgorithm sha = new SHA256Managed())
            {
                sha.TransformFinalBlock(data, 0, data.Length);
                return Convert.ToBase64String(sha.Hash);
            }
        }

        /// <summary>
        /// Gets membership user.
        /// </summary>
        /// <param name="userName">
        /// The user name.
        /// </param>
        /// <param name="email">
        /// The email.
        /// </param>
        /// <param name="lastLogin">
        /// The last login.
        /// </param>
        /// <returns>
        /// A MembershipUser.
        /// </returns>
        private MembershipUser GetMembershipUser(string userName, string email, DateTime lastLogin)
        {
            var user = new MembershipUser(
                this.Name, // Provider name
                userName, // Username
                userName, // providerUserKey
                email, // Email
                string.Empty, // passwordQuestion
                string.Empty, // Comment
                false, // approved
                false, // isLockedOut
                DateTime.Now, // creationDate
                lastLogin, // lastLoginDate
                DateTime.Now, // lastActivityDate
                DateTime.Now, // lastPasswordChangedDate
                new DateTime(1980, 1, 1)); // lastLockoutDate
            return user;
        }

        #endregion
    }
}
