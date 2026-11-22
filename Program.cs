using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Xml.Linq;
using Serilog;
using Polly;

class Program
{
    // Main becomes async Task so that I can use await commands.
    static async Task Main(string[] args)
    {
        // Creating Generic host, which controls DI-container, configurations and log.
        // Reads appsettings.json automatically.
        var builder = Host.CreateApplicationBuilder(args);

        // Looad configuration from appsettings.json
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // Retrieves log string path
        string logPath = configuration["Logging:LogPath"] ?? "logs/default_log.txt";

        // Retrieves connection string path
        string connectionString = configuration.GetConnectionString("DefaultConnection");

        // Configure Serilog to save to console and file
        // RollingInterval.Day means system creates automatically new log file every day.
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/integration_log.txt", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog();

        // Registering own ingration service to DI-container (Transient = creating whenever needed)
        // builder.Services.AddTransient<SoapIntegrationService>();
        builder.Services.AddTransient<ISoapIntegrationService, SoapIntegrationService>();

        var host = builder.Build();

        // Requesting DI-container to give me ready integration service and performing it
        Log.Information("Program is launching through DI-container...");

        using (var scope = host.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<ISoapIntegrationService>();
            await processor.RunAsync();
        }

        Log.CloseAndFlush();
    }
}

public interface ISoapIntegrationService
{
    Task RunAsync();
}

public class IntegrationSettings
{
    public const string SectionName = "IntegrationSettings";

    public string XmlFilePath { get; set; } = "soap_response.xml";
    public string XmlNamespace { get; set; } = "http://tempuri.org/";
    public int RetryCount { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 2;
}

    public class SoapIntegrationService : ISoapIntegrationService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SoapIntegrationService> _logger;
        private readonly IntegrationSettings _settings;

        // DI-container gives automatically Iconfiguration-object to here (Constructor injection)
        public SoapIntegrationService(IConfiguration configuration,
                                      ILogger<SoapIntegrationService> logger,
                                      IOptions<IntegrationSettings> options)
        {
            _configuration = configuration;
            _logger = logger;
        _settings = options.Value;
        }

        public async Task RunAsync()
        {
            string connectionString = _configuration.GetConnectionString("DefaultConnection");

            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.LogWarning("Error: Connection string did not find appsettings.json");
                return;
            }

            // Polly's retry rule
            var retryPolicy = Policy.Handle<SqlException>().WaitAndRetryAsync(3, attempt =>
        TimeSpan.FromSeconds(2), (exception, timeSpan, attempt, context) =>
        {
            _logger.LogWarning("Database error: '{ErrorMessage}'. Trying again ({Attempt}/3)...",
            exception.Message, attempt);
        });

            // Opening connection to SQL server SMSS
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            Log.Information("Connection to SQL server was successful using appsettings.json!");


            string createTableSql = @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Products')
                BEGIN
                    CREATE TABLE Products (
                        Id INT PRIMARY KEY,
                        Name NVARCHAR(100),
                        Price INT
                    );
                END";

            using (var createCommand = new SqlCommand(createTableSql, connection))
            {
                await retryPolicy.ExecuteAsync(async () => await createCommand.ExecuteNonQueryAsync());
            }


        _logger.LogInformation("Loading XML-file from path: {XmlPath}", _settings.XmlFilePath);
        XDocument doc = XDocument.Load(_settings.XmlFilePath);
        XNamespace ns = _settings.XmlNamespace;

            // Searches for all <Product>-elements by namespace reference
            var products = doc.Descendants(ns + "Product");


            string insertSql = "MERGE INTO Products AS target USING (VALUES (@id, @name, @price)) AS " +
            "source (Id, Name, Price) ON target.Id = source.Id WHEN MATCHED THEN UPDATE SET " +
            "target.Name = source.Name, target.Price = source.Price WHEN NOT MATCHED THEN " +
            "INSERT (Id, Name, Price) VALUES (source.Id, source.Name, source.Price);";

            _logger.LogInformation("Parsing products from SOAP-response:\n");

            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var p in products)
                {
                    // Checking if Id is existing and valid
                    var idElement = p.Element(ns + "Id");
                    var priceElement = p.Element(ns + "Price");
                    var nameElement = p.Element(ns + "Name");

                    // If Id is missing or is not number, throwing exception to trigger
                    // rollback of transcation.
                    if (idElement == null || !int.TryParse(idElement.Value, out int id))
                    {
                        throw new Exception($"Error: Product is missing required id " +
                            $"or id is faulty!");
                    }

                    if (priceElement == null || !int.TryParse(priceElement.Value, out int price))
                    {
                        throw new Exception($"Error: Product {id} has faulty or missing price!");
                    }
                    if (nameElement == null || string.IsNullOrWhiteSpace(nameElement.Value))
                    {
                        throw new Exception($"Error: Product {id} has faulty or missing name!");
                    }

                    string? name = nameElement?.Value;

                    using (var insertCommand = new SqlCommand(insertSql, connection))
                    {
                        // Ties the commmand to transaction.
                        insertCommand.Transaction = transaction;
                        insertCommand.Parameters.AddWithValue("@id", id);
                        insertCommand.Parameters.AddWithValue("@name", name ?? (object)DBNull.Value);
                        insertCommand.Parameters.AddWithValue("@price", price);

                        await retryPolicy.ExecuteAsync(async () =>
                        {
                            await insertCommand.ExecuteNonQueryAsync();
                        });
                    }
                    _logger.LogInformation("Processing: Id={ProductId}, Name={ProductName}, Price={ProductPrice} €",
                                    id,
                                    name,
                                    price);
                }
                transaction.Commit();
                _logger.LogInformation("\n All products are saved succesfully in transaction!");
            }
            catch (Exception ex)
            {
                // If something went wrong, rollback all changes at once!
                transaction.Rollback();
                _logger.LogInformation($"\nError during saving process, all changes are rolled back: {ex.Message}");
            }
        }
    }
        