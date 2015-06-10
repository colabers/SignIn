﻿using System;
using System.Security.Cryptography;
using System.Web;
using Starcounter;
using Starcounter.Internal;
using Concepts.Ring3;
using Concepts.Ring1;
using Concepts.Ring8.Polyjuice;
using Concepts.Ring8.Tunity;
using Tunity.Common;

namespace SignIn {
    public class SignInOut {
        internal static string AdminGroupName = "Admin (System Users)";
        internal static string AdminGroupDescription = "System User Administrator Group";
        internal static string AdminUsername = "admin";
        internal static string AdminPassword = "admin";

        static public UserSession GetCurrentSystemUserSession() {
            return Db.SQL<UserSession>("SELECT o FROM Concepts.Ring8.Tunity.UserSession o WHERE o.SessionIdString = ?", Session.Current.SessionIdString).First;
        }

        /// <summary>
        /// Sign-in user
        /// </summary>
        /// <param name="UserId"></param>
        /// <param name="Password"></param>
        /// <param name="SignInAuthToken"></param>
        /// <param name="Message"></param>
        /// <returns></returns>
        static public UserSession SignInSystemUser(string UserId, string Password, string SignInAuthToken, out string Message) {
            Message = null;

            if (!string.IsNullOrEmpty(UserId)) {
                UserSession userSession = SignInSystemUser(UserId, Password);
                
                if (userSession != null) {
                    return userSession;
                }

                Message = "Invalid username or password";
                return null;
            }

            if (string.IsNullOrEmpty(SignInAuthToken)) {

                Message = "Invalid username or password";
                return null;
            }

            // Use Auth token cookie if it exist
            return SignInSystemUser(SignInAuthToken);
        }

        static public UserSession SignInSystemUser(TunityUser systemUser) {
            if (systemUser == null) {
                return null;
            }

            UserSession userSession = null;

            Db.Transact(() => {
                TunitySessionCookie cookie = new TunitySessionCookie();
                
                cookie.Created = cookie.LastUsed = DateTime.UtcNow;
                cookie.Name = CreateAuthToken(systemUser.Username);
                cookie.User = systemUser;

                userSession = AssureSystemUserSession(cookie);
            });

            return userSession;
        }

        /// <summary>
        /// Sign-in user
        /// </summary>
        /// <param name="UserId"></param>
        /// <param name="Password"></param>
        static private UserSession SignInSystemUser(string userId, string password) {

            UserSession userSession = null;
            TunityUser systemUser = TunityDbHelper.FromName<TunityUser>(userId);

           if (systemUser != null) {
               if (PasswordHash.ValidatePassword(password, systemUser.Password))
               {
                   Db.Transact(() =>
                   {
                       TunitySessionCookie cookie = new TunitySessionCookie();
                       cookie.Created = cookie.LastUsed = DateTime.UtcNow;
                       cookie.Name = CreateAuthToken(systemUser.Username);
                       cookie.User = systemUser;

                       userSession = AssureSystemUserSession(cookie);
                   });
               }
            }


            
           
            return userSession;
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="UserId"></param>
        /// <returns></returns>
        static public String CreateAuthToken(string UserId) {
            // Server has a secret key K (a sequence of, say, 128 bits, produced by a cryptographically secure PRNG).
            // A token contains the user name (U), the time of issuance (T), and a keyed integrity check computed over U and T (together),
            // keyed with K (by default, use HMAC with SHA-256 or SHA-1).
            // Auth token    Username+tokendate
            byte[] randomNumber = new byte[16];

            RNGCryptoServiceProvider rngCsp = new RNGCryptoServiceProvider();
            rngCsp.GetBytes(randomNumber);

            return HttpServerUtility.UrlTokenEncode(randomNumber);
        }

        /// <summary>
        /// Sign-in user
        /// </summary>
        /// <param name="AuthToken"></param>
        static public UserSession SignInSystemUser(string AuthToken) {
            TunitySessionCookie oldToken = TunityDbHelper.FromName<TunitySessionCookie>(AuthToken);
            
            if (oldToken == null) {
                return null;
            }

            if (oldToken.User == null) {
                Db.Transact(() => {
                    DeleteToken(oldToken);
                });

                return null;
            }

            // TODO: Check if token should expire (to old for reuse)?
            TimeSpan ts = new TimeSpan(DateTime.UtcNow.Ticks - oldToken.LastUsed.Ticks);
            
            if (ts.TotalDays > 7) {

                Db.Transact(() => {
                    DeleteToken(oldToken);
                });

                return null;
            }

            UserSession session = null;

            Db.Transact(() => {
                session = AssureSystemUserSession(oldToken);
            });

            return session;
        }

        /// <summary>
        /// Create system user session
        /// </summary>
        /// <param name="Token"></param>
        /// <returns></returns>
        static private UserSession AssureSystemUserSession(TunitySessionCookie cookie) {
           UserSession userSession = null;

            bool bSessionCreated = false;

            Db.Transact(() => {
                userSession = Db.SQL<UserSession>("SELECT o FROM Concepts.Ring8.Tunity.UserSession o WHERE o.SessionIdString=?", Session.Current.SessionIdString).First;

                if (userSession == null) {
                    userSession = new UserSession();
                    userSession.Created = DateTime.UtcNow;
                    userSession.SessionIdString = Session.Current.SessionIdString;
                    bSessionCreated = true;
                }

                userSession.Token = cookie;
                userSession.Touched = DateTime.UtcNow;
            });

            if (bSessionCreated) {
                InvokeSignInCommitHook(userSession.SessionIdString);
            }

            return userSession;
        }

        static private void DeleteToken(TunitySessionCookie Token) {
            QueryResultRows<UserSession> sessions = Db.SQL<UserSession>("SELECT o FROM Concepts.Ring8.Tunity.UserSession o WHERE o.Token=?", Token);

            foreach (var session in sessions) {
                session.Delete();
            }

            Token.Delete();
        }

        static public bool SignOutSystemUser() {
            UserSession session = GetCurrentSystemUserSession();

            if (session != null) {
                return SignOutSystemUser(session.Token.Name);
            }

            return false;
        }

        /// <summary>
        /// Sign out user on all sessions that uses the same auth token
        /// </summary>
        /// <param name="AuthToken"></param>
        /// <returns>True if a user was signed out, otherwice false is user is already signed out</returns>
        static public bool SignOutSystemUser(string AuthToken) {
            if (AuthToken == null) {
                return false;
            }

            TunitySessionCookie token = TunityDbHelper.FromName<TunitySessionCookie>(AuthToken);

            if (token == null) {
                return false;
            }

            bool bUserWasSignedOut = false;

            Db.Transact(() => {
                QueryResultRows<UserSession> result = Db.SQL<UserSession>("SELECT o FROM Concepts.Ring8.Tunity.UserSession o WHERE o.Token=?", token);

                foreach (UserSession userSession in result) {
                    string sessoinId = userSession.SessionIdString;

                    userSession.Delete();
                    InvokeSignOutCommitHook(sessoinId);
                    bUserWasSignedOut = true;
                }

                token.Delete();
            });

            return bUserWasSignedOut;
        }

        #region Default admin user
        /// <summary>
        /// Assure that there is at least one system user beloning to the admin group 
        /// </summary>
        internal static void AssureAdminSystemUser() {

            Db.Transact(() =>
            {
           
                AssureHelper.Assure("admin", delegate(TunityUser user)
                {
                    user.Password = PasswordHash.CreateHash("admin");
                    user.SuperUser = true;
                    user.Hidden = true;
                    if (user.WhoIs == null)
                    {
                        Person p = new Person()
                        {
                            FirstName = "Tunity",
                            Surname = "Administrator"
                        };
                        user.WhoIs = p;
                    }
                });
            });
        }

        #endregion

        #region Commit Hook replacement

        /// <summary>
        /// Temporary code until starcounter implements commit hooks
        /// </summary>
        /// <param name="user"></param>
        static void InvokeSignInCommitHook(string SessionIdString) {
            Response r = Self.POST(CommitHooks.MappedTo, SessionIdString, null);

            if (r.StatusCode < 200 || r.StatusCode >= 300) {
            }
        }

        /// <summary>
        /// Temporary code until starcounter implements commit hooks
        /// </summary>
        /// <param name="user"></param>
        static void InvokeSignOutCommitHook(string SessionIdString) {
            Response r = Self.DELETE(CommitHooks.MappedTo, SessionIdString, null);

            if (r.StatusCode < 200 || r.StatusCode >= 300) {
            }
        }
        #endregion
    }
}