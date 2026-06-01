using System.Diagnostics.CodeAnalysis;
using Azure.Identity;
using Evolve.Digital.LedgerService.Shared.Internal;
using Evolve.Digital.LedgerService.Shared.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentServices.Shared.Extensions;
using PaymentServices.Transfer.Models;
using PaymentServices.Transfer.Repositories;
using PaymentServices.Transfer.Services;
using Serilog;
using Serilog.Events;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using LedgerLibILedgerService = Evolve.Digital.LedgerService.Shared.Services.ILedgerService;
using LedgerLibLedgerService = Evolve.Digital.LedgerService.Shared.Services.LedgerService;
using TransferILedgerService = PaymentServices.Transfer.Services.ILedgerService;

namespace PaymentServices.Transfer;

[ExcludeFromCodeCoverage]
public static class Program
{
    private const string Prefix = "transfer:AppSettings";

    public static async Task Main(string[] args)
    {
        var host = new HostBuilder()
            .ConfigureAppConfiguration(SetupAppConfiguration)
            .ConfigureFunctionsWebApplication()
            .ConfigureServices((context, services) =>
            {
                var config = context.Configuration;

                SetupSerilog(config);

                // Application Insights
                services.AddApplicationInsightsTelemetryWorkerService();
                services.ConfigureFunctionsApplicationInsights();

                // Shared infrastructure
                services.AddPaymentAppSettings(config, Prefix);
                services.AddPaymentCosmosClient(config, Prefix);
                services.AddPaymentServiceBusPublisher(config, Prefix);

                // Transfer-specific settings
                services.AddOptions<TransferSettings>()
                    .Configure<IConfiguration>((settings, cfg) =>
                        cfg.GetSection(Prefix).Bind(settings));

                // Cosmos containers
                RegisterCosmosContainers(services, config);

                // Repositories
                services.AddTransient<ITransactionStateRepository, TransactionStateRepository>();

                // Pre-ledger checks — mocked (swap for real NuGets later)
                services.AddSingleton<ILimitService, NoOpLimitService>();
                services.AddSingleton<IScreeningService, NoOpScreeningService>();

                // Ledger via Evolve.Digital.LedgerService NuGet (source debit + NSF)
                RegisterLedgerServices(services, config);

                // Services
                services.AddTransient<ITransferService, TransferService>();

                services.AddHealthChecks();
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.Services.Configure<LoggerFilterOptions>(options =>
                {
                    var defaultRule = options.Rules.FirstOrDefault(rule =>
                        rule.ProviderName ==
                        "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
                    if (defaultRule is not null)
                        options.Rules.Remove(defaultRule);
                });

                logging.AddSerilog(dispose: true);
            })
            .Build();

        await host.RunAsync();
    }

    private static void SetupAppConfiguration(IConfigurationBuilder builder)
    {
        builder.AddEnvironmentVariables();
        var settings = builder.Build();

        var appConfigUrl = settings["AppConfig:Endpoint"];
        var azureClientId = settings["AZURE_CLIENT_ID"];

        if (!string.IsNullOrWhiteSpace(appConfigUrl) && !string.IsNullOrWhiteSpace(azureClientId))
        {
            var credentialOptions = new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = azureClientId
            };
            var credential = new DefaultAzureCredential(credentialOptions);

            builder.AddAzureAppConfiguration(options =>
            {
                options
                    .Connect(new Uri(appConfigUrl), credential)
                    .Select("transfer:*")
                    .Select("telemetry:*")
                    .ConfigureKeyVault(kv => kv.SetCredential(credential));
            });
        }

        builder
            .SetBasePath(Environment.CurrentDirectory)
            .AddJsonFile("local.settings.json", optional: true, reloadOnChange: false);
    }

    private static void SetupSerilog(IConfiguration config)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Azure.Functions.Worker", LogEventLevel.Warning)
            .MinimumLevel.Override("Host", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", "PaymentServices.Transfer")
            .Enrich.WithProperty("Environment",
                Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT") ?? "Production")
            .CreateLogger();
    }

    private static void RegisterCosmosContainers(IServiceCollection services, IConfiguration config)
    {
        var tptchDb = config[$"{Prefix}:COSMOS_DATABASE"] ?? "tptch";

        services.AddKeyedSingleton<CosmosContainer>("transactions", (sp, _) =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            var container = config[$"{Prefix}:COSMOS_TRANSACTIONS_CONTAINER"] ?? "tchSendTransactions";
            return client.GetContainer(tptchDb, container);
        });

        services.AddKeyedSingleton<CosmosContainer>("platforms", (sp, _) =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            var container = config[$"{Prefix}:COSMOS_PLATFORMS_CONTAINER"] ?? "platforms";
            return client.GetContainer(tptchDb, container);
        });

        services.AddKeyedSingleton<CosmosContainer>("customers", (sp, _) =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            var container = config[$"{Prefix}:COSMOS_CUSTOMERS_CONTAINER"] ?? "customers";
            return client.GetContainer(tptchDb, container);
        });

        services.AddKeyedSingleton<CosmosContainer>("accounts", (sp, _) =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            var container = config[$"{Prefix}:COSMOS_ACCOUNTS_CONTAINER"] ?? "accounts";
            return client.GetContainer(tptchDb, container);
        });

        var ledgerDb = config[$"{Prefix}:COSMOS_LEDGER_DATABASE"] ?? "ledgers";

        services.AddKeyedSingleton<CosmosContainer>("ledgers", (sp, _) =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            var container = config[$"{Prefix}:COSMOS_LEDGER_CONTAINER"] ?? "ledgers";
            return client.GetContainer(ledgerDb, container);
        });

        services.AddKeyedSingleton<CosmosContainer>("ledgerEntries", (sp, _) =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            var container = config[$"{Prefix}:COSMOS_LEDGER_ENTRIES_CONTAINER"] ?? "ledgerEntries";
            return client.GetContainer(ledgerDb, container);
        });
    }

    /// <summary>
    /// Registers the Evolve.Digital.LedgerService.Shared(.Internal) stack and
    /// the Transfer ILedgerService adapter (EvolveLedgerService).
    ///
    /// The Evolve ledger models are PascalCase. The default CosmosClient from
    /// PaymentServices.Shared uses a camelCase serializer, which would corrupt
    /// ledger docs on round-trip. So we register a SEPARATE keyed CosmosClient
    /// ("ledger") with the SDK default (PascalCase) serializer and inject that
    /// into the upstream LedgerService/BatchService only.
    /// </summary>
    private static void RegisterLedgerServices(IServiceCollection services, IConfiguration config)
    {
        services.AddKeyedPaymentCosmosClient(
            configuration: config,
            serviceKey: "ledger",
            prefix: Prefix,
            useDefaultSerializer: true);

        services.AddSingleton<LedgerLibILedgerService>(sp =>
        {
            var cosmos = sp.GetRequiredKeyedService<CosmosClient>("ledger");
            var settings = sp.GetRequiredService<IOptions<TransferSettings>>().Value;
            return new LedgerLibLedgerService(cosmos, settings.COSMOS_LEDGER_DATABASE);
        });

        services.AddSingleton<IBatchService>(sp =>
        {
            var cosmos = sp.GetRequiredKeyedService<CosmosClient>("ledger");
            var settings = sp.GetRequiredService<IOptions<TransferSettings>>().Value;
            return new BatchService(cosmos, settings.COSMOS_LEDGER_DATABASE);
        });

        services.AddSingleton<ILedgerInternalClient, LedgerInternalClient>();

        // Transfer's own ledger adapter
        services.AddTransient<TransferILedgerService, EvolveLedgerService>();
    }
}