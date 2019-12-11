namespace Microsoft.BotBuilderSamples
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Bot.Connector.Authentication;

    /// <summary>
    /// this disables jwt tokens that the bot uses so anything can connect to your bot
    /// </summary>
    public class DisabledAuthCredentialProvider : ICredentialProvider
    {
        /// <summary>
        /// this gets the application password
        /// </summary>
        /// <param name="appId">the app id we need the password for</param>
        /// <returns>the password</returns>
        public Task<string> GetAppPasswordAsync(string appId)
        {
            return Task.FromResult("c4Ec^Tl_J#i97t|sZ)g;g5r{FnG7$");
        }

        /// <summary>
        /// checks to see if authentication is disabled or not
        /// </summary>
        /// <returns>true if auth is disabled.</returns>
        public Task<bool> IsAuthenticationDisabledAsync()
        {
            return Task.FromResult(true);
        }

        /// <summary>
        /// checks to see if the app id is valid
        /// </summary>
        /// <param name="appId">the appid we need to check</param>
        /// <returns>true if the app id is valid false otherwise</returns>
        public Task<bool> IsValidAppIdAsync(string appId)
        {
            return Task.FromResult(true);
        }
    }
}
