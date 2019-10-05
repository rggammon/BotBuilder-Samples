// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Connector.DirectLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Microsoft.BotBuilderSamples.Bots
{
    // A variation on https://docs.microsoft.com/en-us/dynamics365/ai/customer-service-virtual-agent/how-to-use-dispatcher,
    // essentially relaying incoming messages to a dynamics bot over Direct Line channel.
    // Also, https://github.com/microsoftgraph/microsoft-graph-comms-samples for teams calling bots.
    public class GatewayDynamicsBot : ActivityHandler
    {
        private readonly JObject _channelData;
        private readonly string _dynamicsBotTokenEndpoint;
        private readonly string _dynamicsBotName;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IStorage _storage;
        private readonly ILogger<GatewayDynamicsBot> _logger;
        private readonly TimeSpan _receiveWindow = TimeSpan.FromSeconds(3);
        private readonly TimeSpan _pollingDelay = TimeSpan.FromSeconds(0.5);

        public GatewayDynamicsBot(IConfiguration configuration, IHttpClientFactory httpClientFactory, IStorage storage, ILogger<GatewayDynamicsBot> logger)
        {
            var botId = configuration["DynamicsBotId"];
            var tenantId = configuration["DynamicsBotTenantId"];
            _channelData = new JObject()
            {
                ["bot_id"] = botId,
                ["tenant_id"] = tenantId,
            };
            _dynamicsBotTokenEndpoint = configuration["DynamicsBotTokenEndpoint"] + $"?botId={botId}&tenantId={tenantId}";
            _dynamicsBotName = configuration["DynamicsBotName"];
            _httpClientFactory = httpClientFactory;
            _storage = storage;
            _logger = logger;
        }

        protected override async Task OnMessageActivityAsync(ITurnContext<Bot.Schema.IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            string token;

            _logger.LogInformation($"Entering OnMessageActivityAsync, conversationId: {turnContext.Activity?.Conversation?.Id}");

            // Read state
            var props = await _storage.ReadAsync(new[] { turnContext.Activity.Conversation.Id });
            if (props.Count == 0)
            {
                _logger.LogInformation("Getting token for a new conversation....");
                token = await GetTokenAsync();
            }
            else
            {
                token = (string)props["token"];
            }

            _logger.LogInformation($"Got token, {token.Substring(0, 6)}");

            using (var directLineClient = new DirectLineClient(token))
            {
                string conversationId = null;
                string watermark = null;
                if (props.Count == 0)
                {
                    _logger.LogInformation($"Starting conversation");

                    var conversation = await directLineClient.Conversations.StartConversationAsync();
                    conversationId = conversation.ConversationId;
                }
                else
                {
                    conversationId = (string)props["conversationId"];
                    watermark = (string)props["watermark"];
                }

                _logger.LogInformation($"Sending, conversationId={conversationId}");

                // Send to dynamics bot
                var response = await directLineClient.Conversations.PostActivityAsync(conversationId, new Activity()
                {
                    Type = ActivityTypes.Message,
                    From = new ChannelAccount { Id = "userId", Name = "userName" },
                    Text = turnContext.Activity.Text,
                    ChannelData = _channelData,
                    TextFormat = "plain",
                    Locale = "en-US"
                });

                _logger.LogInformation($"Receiving, conversationId={conversationId}, watermark={watermark}");

                // Receive from dynamics bot
                var startReceive = DateTime.UtcNow;
                do
                {
                    var activitySet = await directLineClient.Conversations.GetActivitiesAsync(conversationId, watermark);
                    var responseActivities = activitySet?.Activities?
                        .Where(x => x.Type == ActivityTypes.Message && x.From.Name == _dynamicsBotName)
                        .Select(m =>
                        {
                            var activity = ((Bot.Schema.Activity)turnContext.Activity).CreateReply(m.Text);
                            activity.Speak = activity.Text;
                            activity.InputHint = InputHints.ExpectingInput;
                            return activity;
                        })
                        .Cast<Bot.Schema.IActivity>()
                        .ToArray();

                    if (responseActivities.Length > 0)
                    {
                        _logger.LogInformation($"Received, count={responseActivities.Length}");
                        await turnContext.SendActivitiesAsync(responseActivities);

                        // Reset the clock, sliding the window
                        startReceive = DateTime.UtcNow;
                    }

                    watermark = activitySet.Watermark;
                    await Task.Delay(_pollingDelay);
                } while ((DateTime.UtcNow - startReceive) < _receiveWindow);

                _logger.LogInformation($"Writing converstation state...");

                // Write state
                await _storage.WriteAsync(new Dictionary<string, object>()
                {
                    ["token"] = token,
                    ["conversationId"] = conversationId,
                    ["watermark"] = watermark,
                });
            }

            _logger.LogInformation($"Exiting OnMessageActivityAsync, conversationId: {turnContext.Activity?.Conversation?.Id}");
        }

        private async Task<string> GetTokenAsync()
        {
            using (var httpClient = _httpClientFactory.CreateClient())
            {
                var response = await httpClient.GetAsync(_dynamicsBotTokenEndpoint);
                response.EnsureSuccessStatusCode();
                dynamic content = await response.Content.ReadAsAsync<JObject>();
                return content.token;
            }
        }
    }
}
