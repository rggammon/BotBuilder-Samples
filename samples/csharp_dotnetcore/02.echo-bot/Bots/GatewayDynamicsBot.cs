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
        private readonly bool _enableYouSaid = true;
        private readonly bool _enableStartWithOk = true;
        private readonly string _voice = "Microsoft Server Speech Text to Speech Voice (en-US, BenjaminRUS)";

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
            bool.TryParse(configuration["EnableYouSaid"], out _enableYouSaid);
            bool.TryParse(configuration["EnableStartWithOk"], out _enableStartWithOk);
            _voice = configuration["Voice"];
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

            _logger.LogInformation($"{turnContext.Activity?.Id} Entering OnMessageActivityAsync, conversationId: {turnContext.Activity?.Conversation?.Id}, activityType: {turnContext.Activity.Type}");

            // Read state
            var props = await _storage.ReadAsync(new[] { turnContext.Activity.Conversation.Id });
            if (props.Count == 0)
            {
                _logger.LogInformation($"{turnContext.Activity?.Id} Getting token for a new conversation....");
                token = await GetTokenAsync();
            }
            else
            {
                props = (IDictionary<string, object>)props[turnContext.Activity.Conversation.Id];
                token = (string)props["token"];
                conversationId = (string)props["conversationId"];
                watermark = (string)props["watermark"];
            }

            _logger.LogInformation($"{turnContext.Activity?.Id} Got token, {token.Substring(0, 6)}, conversationId: {conversationId}, watermark: {watermark}");

            try
            {
                Func<string, Task> saveConversationId = (c) => WriteState(turnContext, c, token, watermark);
                (conversationId, watermark) = await ProcessTurnAsync(turnContext, token, conversationId, watermark, saveConversationId);
            }
            catch (Exception ex)
            {
                _telemetryClient.TrackException(ex);
                var activity = turnContext.Activity.CreateReply();
                activity.Text = activity.Speak = ex.Message;
                await turnContext.SendActivitiesAsync(new[] { activity });
            }

            await WriteState(turnContext, conversationId, token, watermark);

            _logger.LogInformation($"{turnContext.Activity?.Id} Exiting OnMessageActivityAsync, conversationId: {turnContext.Activity?.Conversation?.Id}");
        }

        private async Task WriteState(ITurnContext turnContext, string conversationId, string token, string watermark)
        {
            _logger.LogInformation($"{turnContext.Activity?.Id} Writing conversation state...");

            // Write state
            await _storage.WriteAsync(new Dictionary<string, object>()
            {
                [turnContext.Activity.Conversation.Id] = new Dictionary<string, object>()
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
                    _logger.LogInformation($"{turnContext.Activity?.Id} Starting conversation");

                    var conversation = await directLineClient.Conversations.StartConversationAsync();
                    conversationId = conversation.ConversationId;

                    // Save state, so if there are later exceptions, we don't repeat the message below.
                    await saveConversationId(conversationId);

                    if (turnContext.Activity.Type == ActivityTypes.ConversationUpdate)
                    {
                        var activity = turnContext.Activity.CreateReply();
                        if (_enableStartWithOk)
                        {
                            activity.Text = "For this demo, start your sentences with the word OK.";
                        }
                        else
                        {
                            activity.Text = "Hello, thanks for calling the Voice Power Virtual Agent demo.";
                        }

                        activity.Speak = SimpleConvertToSSML(activity.Text);

                        await turnContext.SendActivitiesAsync(new[] { activity });
                    }
                }

                if (turnContext.Activity.Type == ActivityTypes.Message && !string.IsNullOrEmpty(turnContext.Activity.Text))
                {
                    if (!_enableStartWithOk || (turnContext.Activity.Text.StartsWith("OK", true, CultureInfo.InvariantCulture)))
                    {
                        var text = _enableStartWithOk ? turnContext.Activity.Text.Substring(turnContext.Activity.Text.IndexOf(' ') + 1) : turnContext.Activity.Text;

                        await SendToPowerVA(conversationId, text, turnContext, directLineClient);

                        watermark = await ReceiveFromPowerVA(conversationId, watermark, turnContext, directLineClient);
                    }
                    else
                    {
                        _logger.LogInformation($"{turnContext.Activity?.Id} Discarding, conversationId={conversationId}, text={turnContext.Activity.Text}");
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
            _logger.LogInformation($"{turnContext.Activity?.Id} Sending, conversationId={conversationId}, text={text}");

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

            if (_enableYouSaid)
            {
                var youSaid = turnContext.Activity.CreateReply();
                youSaid.Text = $"You said: {text}.";
                youSaid.Speak = SimpleConvertToSSML(youSaid.Text);
                _logger.LogInformation($"{turnContext.Activity?.Id} Speaking: {youSaid.Speak}");

                await turnContext.SendActivitiesAsync(new[] { youSaid });
            }
        }

        private string SimpleConvertToSSML(string text)
        {
            var locale = "en-US";

            string ssmlTemplate = @"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{2}'>
                    <voice name='{1}'>{0}</voice>
                </speak>";

            return string.Format(ssmlTemplate, text, _voice, locale);
        }

        //private string SimpleConvertToSSML(string Message)
        //{
        //    string Lang = "en-US";
        //    string VoiceName = "Microsoft Server Speech Text to Speech Voice (en-US, JessaNeural)";
        //    string ExpressionType = "cheerful";

        //    return $@"<speak version=""1.0"" xmlns=""https://www.w3.org/2001/10/synthesis"" xmlns:mstts=""https://www.w3.org/2001/mstts"" xml:lang=""{Lang}"">
        //        <voice name=""{VoiceName}"">
        //            <mstts:express-as type=""{ExpressionType}"">
        //                {Message}
        //            </mstts:express-as>
        //        </voice>
        //    </speak>";
        //}

        private async Task<string> ReceiveFromPowerVA(string conversationId, string watermark, ITurnContext turnContext, DirectLineClient directLineClient)
        {
            _logger.LogInformation($"{turnContext.Activity?.Id} Receiving, conversationId={conversationId}, watermark={watermark}");

            // Receive from Power VA
            var startReceive = DateTime.UtcNow;
            do
            {
                await Task.Delay(_pollingDelay);

                var activitySet = await directLineClient.Conversations.GetActivitiesAsync(conversationId, watermark);
                var responseActivities = activitySet?.Activities?
                    .Where(x => x.Type == ActivityTypes.Message && x.From.Name == _dynamicsBotName)
                    .Select(m =>
                    {
                        _logger.LogInformation($"{turnContext.Activity?.Id} Received, {m.Text}");

                        var activity = turnContext.Activity.CreateReply(m.Text);
                        activity.Speak = SimpleConvertToSSML(activity.Text);
                        activity.InputHint = InputHints.ExpectingInput;
                        return activity;
                    })
                    .Cast<Bot.Schema.IActivity>()
                    .ToArray();

                if (activitySet != null)
                {
                    if (responseActivities != null && responseActivities.Length > 0)
                    {
                        _logger.LogInformation($"{turnContext.Activity?.Id} Received, count={responseActivities.Length}");
                        await turnContext.SendActivitiesAsync(responseActivities);

                        // Reset the clock, sliding the window
                        startReceive = DateTime.UtcNow;
                    }

                    watermark = activitySet.Watermark;
                }
            } while ((DateTime.UtcNow - startReceive) < _receiveWindow);

            return watermark;
        }
    }
}
