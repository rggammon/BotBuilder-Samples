// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.BotBuilderSamples.Bots;
using Microsoft.Bot.Builder.Azure;
using Microsoft.Bot.Builder.StreamingExtensions;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Bot.Connector.Authentication;

namespace Microsoft.BotBuilderSamples
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            var aiOptions = new Microsoft.ApplicationInsights.AspNetCore.Extensions.ApplicationInsightsServiceOptions();
            aiOptions.EnableAdaptiveSampling = false;

            services.AddApplicationInsightsTelemetry(aiOptions);
            services.Configure<TelemetryConfiguration>(Configuration.GetSection("ApplicationInsights"));

            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_1);

            // Create the Bot Framework Adapter with error handling enabled.
            services.AddSingleton<IBotFrameworkHttpAdapter, WebSocketEnabledHttpAdapter>();

            services.AddSingleton<ICredentialProvider, DisabledAuthCredentialProvider>();

            // Create the storage we'll be using for state
            services.AddSingleton<IStorage>(new AzureBlobStorage(Configuration["BlobStorageConnectionString"], Configuration["ContainerName"]));

            services.AddHttpClient();

            // Create the bot as a transient. In this case the ASP Controller is expecting an IBot.
            services.AddTransient<IBot, GatewayDynamicsBot>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseHsts();
            }

            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.UseWebSockets();

            //app.UseHttpsRedirection();
            app.UseMvc();
        }
    }
}
