// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Connector.DirectLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        private readonly TelemetryClient _telemetryClient;

        public GatewayDynamicsBot(IConfiguration configuration, IHttpClientFactory httpClientFactory, IStorage storage, ILogger<GatewayDynamicsBot> logger, IOptions<TelemetryConfiguration> telemetryConfiguration)
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
            _telemetryClient = new TelemetryClient(telemetryConfiguration.Value);
        }

        public override async Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            string token;
            string conversationId = null;
            string watermark = null;

            _logger.LogInformation($"Entering OnMessageActivityAsync, conversationId: {turnContext.Activity?.Conversation?.Id}, activityType: {turnContext.Activity.Type}");

            // Read state
            var props = await _storage.ReadAsync(new[] { turnContext.Activity.Conversation.Id });
            if (props.Count == 0)
            {
                _logger.LogInformation("Getting token for a new conversation....");
                token = await GetTokenAsync();
            }
            else
            {
                props = (IDictionary<string, object>)props[turnContext.Activity.Conversation.Id];
                token = (string)props["token"];
                conversationId = (string)props["conversationId"];
                watermark = (string)props["watermark"];
            }

            _logger.LogInformation($"Got token, {token.Substring(0, 6)}, conversationId: {conversationId}, watermark: {watermark}");

            try
            {
                Func<string, Task> saveConversationId = (c) => WriteState(turnContext.Activity.Conversation.Id, c, token, watermark);
                (conversationId, watermark) = await ProcessTurnAsync(turnContext, token, conversationId, watermark, saveConversationId);
            }
            catch (Exception ex)
            {
                _telemetryClient.TrackException(ex);
                var activity = turnContext.Activity.CreateReply();
                activity.Text = activity.Speak = ex.Message;
                await turnContext.SendActivitiesAsync(new[] { activity });
            }

            await WriteState(turnContext.Activity.Conversation.Id, conversationId, token, watermark);

            _logger.LogInformation($"Exiting OnMessageActivityAsync, conversationId: {turnContext.Activity?.Conversation?.Id}");
        }

        private async Task WriteState(string gatewayConversationId, string conversationId, string token, string watermark)
        {
            _logger.LogInformation($"Writing conversation state...");

            // Write state
            await _storage.WriteAsync(new Dictionary<string, object>()
            {
                [gatewayConversationId] = new Dictionary<string, object>()
                {
                    ["token"] = token,
                    ["conversationId"] = conversationId,
                    ["watermark"] = watermark,
                }
            });
        }

        private async Task<(string, string)> ProcessTurnAsync(ITurnContext turnContext, string token, string conversationId, string watermark, Func<string, Task> saveConversationId)
        {
            using (var directLineClient = new DirectLineClient(token))
            {
                if (string.IsNullOrEmpty(conversationId))
                {
                    _logger.LogInformation($"Starting conversation");

                    var conversation = await directLineClient.Conversations.StartConversationAsync();
                    conversationId = conversation.ConversationId;

                    // Save state, so if there are later exceptions, we don't repeat the message below.
                    await saveConversationId(conversationId);

                    var activity = turnContext.Activity.CreateReply();
                    activity.Text = activity.Speak = "For this demo, start your sentences with the word OK.";

                    await turnContext.SendActivitiesAsync(new[] { activity });
                }

                if (turnContext.Activity.Type == ActivityTypes.Message)
                {
                    if (turnContext.Activity.Text?.StartsWith("OK", true, CultureInfo.InvariantCulture) ?? false)
                    {
                        var text = turnContext.Activity.Text.Substring(turnContext.Activity.Text.IndexOf(' ') + 1);

                        await SendToPowerVA(conversationId, text, turnContext, directLineClient);

                        watermark = await ReceiveFromPowerVA(conversationId, watermark, turnContext, directLineClient);
                    }
                    else
                    {
                        _logger.LogInformation($"Discarding, conversationId={conversationId}, text={turnContext.Activity.Text}");
                    }
                }
            }

            return (conversationId, watermark);
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

        private async Task SendToPowerVA(string conversationId, string text, ITurnContext turnContext, DirectLineClient directLineClient)
        {
            _logger.LogInformation($"Sending, conversationId={conversationId}, text={text}");

            // Send to Power VA
            var response = await directLineClient.Conversations.PostActivityAsync(conversationId, new Activity()
            {
                Type = ActivityTypes.Message,
                From = new ChannelAccount { Id = "userId", Name = "userName" },
                Text = text,
                ChannelData = _channelData,
                TextFormat = "plain",
                Locale = "en-US"
            });

            var youSaid = turnContext.Activity.CreateReply();
            youSaid.Text = $"You said: {text}.";
            youSaid.Speak = youSaid.Text;

            await turnContext.SendActivitiesAsync(new[] { youSaid });
        }

        private async Task<string> ReceiveFromPowerVA(string conversationId, string watermark, ITurnContext turnContext, DirectLineClient directLineClient)
        {
            _logger.LogInformation($"Receiving, conversationId={conversationId}, watermark={watermark}");

            // Receive from Power VA
            var startReceive = DateTime.UtcNow;
            do
            {
                var activitySet = await directLineClient.Conversations.GetActivitiesAsync(conversationId, watermark);
                var responseActivities = activitySet?.Activities?
                    .Where(x => x.Type == ActivityTypes.Message && x.From.Name == _dynamicsBotName)
                    .Select(m =>
                    {
                        _logger.LogInformation($"Received, {m.Text}");

                        var activity = turnContext.Activity.CreateReply(m.Text);
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

            return watermark;
        }
    }
}
