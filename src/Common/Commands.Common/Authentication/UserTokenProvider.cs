﻿// ----------------------------------------------------------------------------------
//
// Copyright Microsoft Corporation
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// ----------------------------------------------------------------------------------

using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Windows.Forms;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.WindowsAzure.Commands.Common.Models;
using Microsoft.WindowsAzure.Commands.Common.Properties;

namespace Microsoft.WindowsAzure.Commands.Utilities.Common.Authentication
{
    /// <summary>
    /// A token provider that uses ADAL to retrieve
    /// tokens from Azure Active Directory for user
    /// credentials.
    /// </summary>
    internal class UserTokenProvider : ITokenProvider
    {
        private readonly IWin32Window parentWindow;

        public UserTokenProvider(IWin32Window parentWindow)
        {
            this.parentWindow = parentWindow;
        }

        public IAccessToken GetAccessToken(AdalConfiguration config, ShowDialog promptBehavior, string userId, SecureString password,
            AzureAccount.AccountType credentialType)
        {
            if (credentialType != AzureAccount.AccountType.User)
            {
                throw new ArgumentException(string.Format(Resources.InvalidCredentialType, "User"), "credentialType");
            }

            return new AdalAccessToken(AcquireToken(config, promptBehavior, userId, password), this, config);
        }

        private readonly static TimeSpan thresholdExpiration = new TimeSpan(0, 5, 0);

        private bool IsExpired(AdalAccessToken token)
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("FORCE_EXPIRED_ACCESS_TOKEN") != null)
            {
                return true;
            }
#endif

            return token.AuthResult.ExpiresOn - DateTimeOffset.Now < thresholdExpiration;
        }

        private void Renew(AdalAccessToken token)
        {
            if (IsExpired(token))
            {
                AuthenticationResult result = AcquireToken(token.Configuration, ShowDialog.Never, token.UserId, null);

                if (result == null)
                {
                    throw new Exception(Resources.ExpiredRefreshToken);
                }
                else
                {
                    token.AuthResult = result;
                }
            }
        }

        private AuthenticationContext CreateContext(AdalConfiguration config)
        {
            return new AuthenticationContext(config.AdEndpoint + config.AdDomain, config.ValidateAuthority, ProtectedFileTokenCache.Instance)
            {
                OwnerWindow = parentWindow
            };
        }

        // We have to run this in a separate thread to guarantee that it's STA. This method
        // handles the threading details.
        private AuthenticationResult AcquireToken(AdalConfiguration config, ShowDialog promptBehavior, string userId, 
            SecureString password)
        {
            AuthenticationResult result = null;
            Exception ex = null;
            if (promptBehavior == ShowDialog.Never)
            {
                result = SafeAquireToken(config, promptBehavior, userId, password, out ex);
            }
            else
            {
                var thread = new Thread(() =>
                {
                    result = SafeAquireToken(config, promptBehavior, userId, password, out ex);
                });

                thread.SetApartmentState(ApartmentState.STA);
                thread.Name = "AcquireTokenThread";
                thread.Start();
                thread.Join();
            }

            if (ex != null)
            {
                var adex = ex as AdalException;
                if (adex != null)
                {
                    if (adex.ErrorCode == AdalError.AuthenticationCanceled)
                    {
                        throw new AadAuthenticationCanceledException(adex.Message, adex);
                    }
                }
                if (ex is AadAuthenticationException)
                {
                    throw ex;
                }
                throw new AadAuthenticationFailedException(GetExceptionMessage(ex), ex);
            }

            return result;
        }

        private AuthenticationResult SafeAquireToken(
            AdalConfiguration config, 
            ShowDialog showDialog, 
            string userId,
            SecureString password, 
            out Exception ex)
        {
            try
            {
                ex = null;
                var promptBehavior = (PromptBehavior)Enum.Parse(typeof(PromptBehavior), showDialog.ToString());

                return DoAcquireToken(config, promptBehavior, userId, password);
            }
            catch (AdalException adalEx)
            {
                if (adalEx.ErrorCode == AdalError.UserInteractionRequired ||
                    adalEx.ErrorCode == AdalError.MultipleTokensMatched)
                {
                    ex = new AadAuthenticationFailedWithoutPopupException(Resources.InvalidSubscriptionState, adalEx);
                }
                else if (adalEx.ErrorCode == AdalError.MissingFederationMetadataUrl)
                {
                    ex = new AadAuthenticationFailedException(Resources.CredentialOrganizationIdMessage, adalEx);
                }
                else
                {
                    ex = adalEx;
                }
            }
            catch (Exception threadEx)
            {
                ex = threadEx;
            }
            return null;
        }

        private AuthenticationResult DoAcquireToken(AdalConfiguration config, PromptBehavior promptBehavior, string userId, 
            SecureString password)
        {
            AuthenticationResult result;
            var context = CreateContext(config);

            if (string.IsNullOrEmpty(userId))
            {
                if (promptBehavior != PromptBehavior.Never)
                {
                    ClearCookies();
                }

                result = context.AcquireToken(config.ResourceClientUri, config.ClientId,
                        config.ClientRedirectUri, promptBehavior,
                        UserIdentifier.AnyUser, AdalConfiguration.EnableEbdMagicCookie);
            }
            else
            {
                if (password == null)
                {
                    result = context.AcquireToken(config.ResourceClientUri, config.ClientId,
                        config.ClientRedirectUri, promptBehavior,
                        new UserIdentifier(userId, UserIdentifierType.OptionalDisplayableId),
                        AdalConfiguration.EnableEbdMagicCookie);
                }
                else
                {
                    UserCredential credential = new UserCredential(userId, password);
                    result = context.AcquireToken(config.ResourceClientUri, config.ClientId, credential);
                }
            }
            return result;
        }

        private string GetExceptionMessage(Exception ex)
        {
            string message = ex.Message;
            if (ex.InnerException != null)
            {
                message += ": " + ex.InnerException.Message;
            }
            return message;
        }
        /// <summary>
        /// Implementation of <see cref="IAccessToken"/> using data from ADAL
        /// </summary>
        private class AdalAccessToken : IAccessToken
        {
            internal readonly AdalConfiguration Configuration;
            internal AuthenticationResult AuthResult;
            private readonly UserTokenProvider tokenProvider;

            public AdalAccessToken(AuthenticationResult authResult, UserTokenProvider tokenProvider, AdalConfiguration configuration)
            {
                AuthResult = authResult;
                this.tokenProvider = tokenProvider;
                Configuration = configuration;
            }

            public void AuthorizeRequest(Action<string, string> authTokenSetter)
            {
                tokenProvider.Renew(this);
                authTokenSetter(AuthResult.AccessTokenType, AuthResult.AccessToken);
            }

            public string AccessToken { get { return AuthResult.AccessToken; } }
            public string UserId { get { return AuthResult.UserInfo.DisplayableId; } }

            public string TenantId { get { return AuthResult.TenantId; } }

            public LoginType LoginType
            {
                get
                {
                    if (AuthResult.UserInfo.IdentityProvider != null)
                    {
                        return LoginType.LiveId;
                    }
                    return LoginType.OrgId;
                }
            }
        }


        private void ClearCookies()
        {
            NativeMethods.InternetSetOption(IntPtr.Zero, NativeMethods.INTERNET_OPTION_END_BROWSER_SESSION, IntPtr.Zero, 0);
        }

        private static class NativeMethods
        {
            internal const int INTERNET_OPTION_END_BROWSER_SESSION = 42;

            [DllImport("wininet.dll", SetLastError = true)]
            internal static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer,
                int lpdwBufferLength);
        }

    }
}
