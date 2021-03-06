using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using App.Metrics;

using Dapper;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using PluralKit.Bot.Commands;
using PluralKit.Bot.CommandSystem;

using Sentry;
using Serilog;
using Serilog.Events;

namespace PluralKit.Bot
{
    class Initialize
    {
        private IConfiguration _config;
        
        static void Main(string[] args) => new Initialize { _config = InitUtils.BuildConfiguration(args).Build()}.MainAsync().GetAwaiter().GetResult();

        private async Task MainAsync()
        {
            ThreadPool.SetMinThreads(32, 32);
            ThreadPool.SetMaxThreads(128, 128);
            
            Console.WriteLine("Starting PluralKit...");
            
            InitUtils.Init();

            // Set up a CancellationToken and a SIGINT hook to properly dispose of things when the app is closed
            // The Task.Delay line will throw/exit (forgot which) and the stack and using statements will properly unwind
            var token = new CancellationTokenSource();
            Console.CancelKeyPress += delegate(object e, ConsoleCancelEventArgs args)
            {
                args.Cancel = true;
                token.Cancel();
            };
            
            using (var services = BuildServiceProvider())
            {
                var logger = services.GetRequiredService<ILogger>().ForContext<Initialize>();
                var coreConfig = services.GetRequiredService<CoreConfig>();
                var botConfig = services.GetRequiredService<BotConfig>();
                var schema = services.GetRequiredService<SchemaService>();

                using (Sentry.SentrySdk.Init(coreConfig.SentryUrl))
                {
                    logger.Information("Connecting to database");
                    await schema.ApplyMigrations();

                    logger.Information("Connecting to Discord");
                    var client = services.GetRequiredService<IDiscordClient>() as DiscordShardedClient;
                    await client.LoginAsync(TokenType.Bot, botConfig.Token);

                    logger.Information("Initializing bot");
                    await services.GetRequiredService<Bot>().Init();
                    
                    await client.StartAsync();

                    try
                    {
                        await Task.Delay(-1, token.Token);
                    }
                    catch (TaskCanceledException) { } // We'll just exit normally
                    logger.Information("Shutting down");
                }
            }
        }

        public ServiceProvider BuildServiceProvider() => new ServiceCollection()
            .AddTransient(_ => _config.GetSection("PluralKit").Get<CoreConfig>() ?? new CoreConfig())
            .AddTransient(_ => _config.GetSection("PluralKit").GetSection("Bot").Get<BotConfig>() ?? new BotConfig())

            .AddSingleton<DbConnectionCountHolder>()
            .AddTransient<DbConnectionFactory>()
            .AddTransient<SchemaService>()

            .AddSingleton<IDiscordClient, DiscordShardedClient>(_ => new DiscordShardedClient(new DiscordSocketConfig
            {
                MessageCacheSize = 0,
                ConnectionTimeout = 2*60*1000,
                ExclusiveBulkDelete = true,
                LargeThreshold = 50,
                DefaultRetryMode = RetryMode.RetryTimeouts | RetryMode.RetryRatelimit
                // Commented this out since Debug actually sends, uh, quite a lot that's not necessary in production
                // but leaving it here in case I (or someone else) get[s] confused about why logging isn't working again :p
                // LogLevel = LogSeverity.Debug // We filter log levels in Serilog, so just pass everything through (Debug is lower than Verbose)
            }))
            .AddSingleton<Bot>()
            .AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            .AddTransient<CommandTree>()
            
            .AddTransient<SystemCommands>()
            .AddTransient<MemberCommands>()
            .AddTransient<SwitchCommands>()
            .AddTransient<LinkCommands>()
            .AddTransient<APICommands>()
            .AddTransient<ImportExportCommands>()
            .AddTransient<HelpCommands>()
            .AddTransient<ModCommands>()
            .AddTransient<MiscCommands>()
            
            .AddTransient<EmbedService>()
            .AddTransient<ProxyService>()
            .AddTransient<LogChannelService>()
            .AddTransient<DataFileService>()
            .AddTransient<WebhookExecutorService>()

            .AddTransient<ProxyCacheService>()
            .AddSingleton<WebhookCacheService>()
            .AddSingleton<ShardInfoService>()
            .AddSingleton<CpuStatService>()

            .AddTransient<IDataStore, PostgresDataStore>()

            .AddSingleton(svc => InitUtils.InitMetrics(svc.GetRequiredService<CoreConfig>()))
            .AddSingleton<PeriodicStatCollector>()
            
            .AddScoped(_ => new Sentry.Scope(null))
            .AddTransient<PKEventHandler>()

            .AddScoped<EventIdProvider>()
            .AddSingleton(svc => new LoggerProvider(svc.GetRequiredService<CoreConfig>(), "bot"))
            .AddScoped(svc => svc.GetRequiredService<LoggerProvider>().RootLogger.ForContext("EventId", svc.GetRequiredService<EventIdProvider>().EventId))
            
            .AddMemoryCache()
            
            .BuildServiceProvider();
    }
    class Bot
    {
        private IServiceProvider _services;
        private DiscordShardedClient _client;
        private Timer _updateTimer;
        private IMetrics _metrics;
        private PeriodicStatCollector _collector;
        private ILogger _logger;
        private PKPerformanceEventListener _pl;

        public Bot(IServiceProvider services, IDiscordClient client, IMetrics metrics, PeriodicStatCollector collector, ILogger logger)
        {
            _pl = new PKPerformanceEventListener();
            _services = services;
            _client = client as DiscordShardedClient;
            _metrics = metrics;
            _collector = collector;
            _logger = logger.ForContext<Bot>();
        }

        public Task Init()
        {
            _client.ShardDisconnected += ShardDisconnected;
            _client.ShardReady += ShardReady;
            _client.Log += FrameworkLog;
            
            _client.MessageReceived += (msg) => HandleEvent(s => s.AddMessageBreadcrumb(msg), eh => eh.HandleMessage(msg));
            _client.ReactionAdded += (msg, channel, reaction) => HandleEvent(s => s.AddReactionAddedBreadcrumb(msg, channel, reaction), eh => eh.HandleReactionAdded(msg, channel, reaction));
            _client.MessageDeleted += (msg, channel) => HandleEvent(s => s.AddMessageDeleteBreadcrumb(msg, channel), eh => eh.HandleMessageDeleted(msg, channel));
            _client.MessagesBulkDeleted += (msgs, channel) => HandleEvent(s => s.AddMessageBulkDeleteBreadcrumb(msgs, channel), eh => eh.HandleMessagesBulkDelete(msgs, channel));
            
            _services.GetService<ShardInfoService>().Init(_client);

            return Task.CompletedTask;
        }

        private Task ShardDisconnected(Exception ex, DiscordSocketClient shard)
        {
            _logger.Warning(ex, $"Shard #{shard.ShardId} disconnected");
            return Task.CompletedTask;
        }

        private Task FrameworkLog(LogMessage msg)
        {
            // Bridge D.NET logging to Serilog
            LogEventLevel level = LogEventLevel.Verbose;
            if (msg.Severity == LogSeverity.Critical)
                level = LogEventLevel.Fatal;
            else if (msg.Severity == LogSeverity.Debug)
                level = LogEventLevel.Debug;
            else if (msg.Severity == LogSeverity.Error)
                level = LogEventLevel.Error;
            else if (msg.Severity == LogSeverity.Info)
                level = LogEventLevel.Information;
            else if (msg.Severity == LogSeverity.Debug) // D.NET's lowest level is Debug and Verbose is greater, Serilog's is the other way around
                level = LogEventLevel.Verbose;
            else if (msg.Severity == LogSeverity.Verbose)
                level = LogEventLevel.Debug;

            _logger.Write(level, msg.Exception, "Discord.Net {Source}: {Message}", msg.Source, msg.Message);
            return Task.CompletedTask;
        }

        // Method called every 60 seconds
        private async Task UpdatePeriodic()
        {
            // Change bot status
            await _client.SetGameAsync($"pk;help | in {_client.Guilds.Count} servers");
            
            await _collector.CollectStats();
            
            _logger.Information("Submitted metrics to backend");
            await Task.WhenAll(((IMetricsRoot) _metrics).ReportRunner.RunAllAsync());
        }

        private Task ShardReady(DiscordSocketClient shardClient)
        {
            _logger.Information("Shard {Shard} connected", shardClient.ShardId);
            Console.WriteLine($"Shard #{shardClient.ShardId} connected to {shardClient.Guilds.Sum(g => g.Channels.Count)} channels in {shardClient.Guilds.Count} guilds.");

            if (shardClient.ShardId == 0)
            {
                _updateTimer = new Timer((_) => {
                    HandleEvent(s => s.AddPeriodicBreadcrumb(), __ => UpdatePeriodic()); 
                }, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

                Console.WriteLine(
                    $"PluralKit started as {_client.CurrentUser.Username}#{_client.CurrentUser.Discriminator} ({_client.CurrentUser.Id}).");
            }

            return Task.CompletedTask;
        }

        private Task HandleEvent(Action<Scope> breadcrumbFactory, Func<PKEventHandler, Task> handler)
        {
            // Inner function so we can await the handler without stalling the entire pipeline
            async Task Inner()
            {
                // "Fork" this task off by ~~yeeting~~ yielding it at the back of the task queue
                // This prevents any synchronous nonsense from also stalling the pipeline before the first await point
                await Task.Yield();
                
                // Create a DI scope for this event
                // and log the breadcrumb to the newly created (in-svc-scope) Sentry scope
                using (var scope = _services.CreateScope())
                {
                    var evtid = scope.ServiceProvider.GetService<EventIdProvider>().EventId;
                    
                    try
                    {
                        await handler(scope.ServiceProvider.GetRequiredService<PKEventHandler>());
                    }
                    catch (Exception e)
                    {
                        var sentryScope = scope.ServiceProvider.GetRequiredService<Scope>();
                        sentryScope.SetTag("evtid", evtid.ToString());
                        breadcrumbFactory(sentryScope);
                        
                        HandleRuntimeError(e, scope.ServiceProvider);
                    }
                }

            }

#pragma warning disable 4014
            Inner();
#pragma warning restore 4014
            return Task.CompletedTask;
        }

        private void HandleRuntimeError(Exception e, IServiceProvider services)
        {
            var logger = services.GetRequiredService<ILogger>();
            var scope = services.GetRequiredService<Scope>();
            
            logger.Error(e, "Exception in bot event handler");
            
            var evt = new SentryEvent(e);
            
            // Don't blow out our Sentry budget on sporadic not-our-problem erorrs
            if (e.IsOurProblem())
                SentrySdk.CaptureEvent(evt, scope);
        }
    }
    
    class PKEventHandler {
        private ProxyService _proxy;
        private ILogger _logger;
        private IMetrics _metrics;
        private DiscordShardedClient _client;
        private DbConnectionFactory _connectionFactory;
        private IServiceProvider _services;
        private CommandTree _tree;
        private IDataStore _data;

        public PKEventHandler(ProxyService proxy, ILogger logger, IMetrics metrics, IDiscordClient client, DbConnectionFactory connectionFactory, IServiceProvider services, CommandTree tree, IDataStore data)
        {
            _proxy = proxy;
            _logger = logger;
            _metrics = metrics;
            _client = (DiscordShardedClient) client;
            _connectionFactory = connectionFactory;
            _services = services;
            _tree = tree;
            _data = data;
        }

        public async Task HandleMessage(SocketMessage arg)
        {
            if (_client.GetShardFor((arg.Channel as IGuildChannel)?.Guild).ConnectionState != ConnectionState.Connected)
                return; // Discard messages while the bot "catches up" to avoid unnecessary CPU pressure causing timeouts
            
            
            RegisterMessageMetrics(arg);

            // Ignore system messages (member joined, message pinned, etc)
            var msg = arg as SocketUserMessage;
            if (msg == null) return;

            // Ignore bot messages
            if (msg.Author.IsBot || msg.Author.IsWebhook) return;
            
            int argPos = -1;
            // Check if message starts with the command prefix
            if (msg.Content.StartsWith("pk;", StringComparison.InvariantCultureIgnoreCase)) argPos = 3;
            else if (msg.Content.StartsWith("pk!", StringComparison.InvariantCultureIgnoreCase)) argPos = 3;
            else if (msg.Content != null && Utils.HasMentionPrefix(msg.Content, ref argPos, out var id)) // Set argPos to the proper value
                if (id != _client.CurrentUser.Id) // But undo it if it's someone else's ping
                    argPos = -1;
            
            // If it does, try executing a command
            if (argPos > -1)
            {
                _logger.Verbose("Parsing command {Command} from message {Channel}-{Message}", msg.Content, msg.Channel.Id, msg.Id);
                
                // Essentially move the argPos pointer by however much whitespace is at the start of the post-argPos string
                var trimStartLengthDiff = msg.Content.Substring(argPos).Length -
                                          msg.Content.Substring(argPos).TrimStart().Length;
                argPos += trimStartLengthDiff;

                // If it does, fetch the sender's system (because most commands need that) into the context,
                // and start command execution
                // Note system may be null if user has no system, hence `OrDefault`
                PKSystem system;
                using (var conn = await _connectionFactory.Obtain())
                    system = await conn.QueryFirstOrDefaultAsync<PKSystem>(
                        "select systems.* from systems, accounts where accounts.uid = @Id and systems.id = accounts.system",
                        new {Id = msg.Author.Id});

                try
                {
                    await _tree.ExecuteCommand(new Context(_services, msg, argPos, system));
                }
                catch (Exception e)
                {
                    await HandleCommandError(msg, e);
                    // HandleCommandError only *reports* the error, we gotta pass it through to the parent
                    // error handler by rethrowing:
                    throw;
                }
            }
            else
            {
                // If not, try proxying anyway
                try
                {
                    await _proxy.HandleMessageAsync(msg);
                }
                catch (PKError e)
                {
                    await arg.Channel.SendMessageAsync($"{Emojis.Error} {e.Message}");
                }
            }
        }

        private async Task HandleCommandError(SocketUserMessage msg, Exception exception)
        {
            // This function *specifically* handles reporting a command execution error to the user.
            // We'll fetch the event ID and send a user-facing error message.
            // ONLY IF this error's actually our problem. As for what defines an error as "our problem",
            // check the extension method :)
            if (exception.IsOurProblem())
            {
                var eid = _services.GetService<EventIdProvider>().EventId;
                await msg.Channel.SendMessageAsync(
                    $"{Emojis.Error} Internal error occurred. Please join the support server (<https://discord.gg/PczBt78>), and send the developer this ID: `{eid}`");
            }
            
            // If not, don't care. lol.
        }

        private void RegisterMessageMetrics(SocketMessage msg)
        {
            _metrics.Measure.Meter.Mark(BotMetrics.MessagesReceived);

            var gatewayLatency = DateTimeOffset.Now - msg.CreatedAt;
            _logger.Verbose("Message received with latency {Latency}", gatewayLatency);
        }

        public Task HandleReactionAdded(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel,
            SocketReaction reaction) => _proxy.HandleReactionAddedAsync(message, channel, reaction);

        public Task HandleMessageDeleted(Cacheable<IMessage, ulong> message, ISocketMessageChannel channel) =>
            _proxy.HandleMessageDeletedAsync(message, channel);

        public Task HandleMessagesBulkDelete(IReadOnlyCollection<Cacheable<IMessage, ulong>> messages,
            IMessageChannel channel) => _proxy.HandleMessageBulkDeleteAsync(messages, channel);
    }
}