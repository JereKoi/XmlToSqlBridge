using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Xml.Linq;
using Serilog;
class Program
{
    static void Main(string[] args)
    {
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



        if (string.IsNullOrEmpty(connectionString))
        {
            Log.Information("Error: Connection string did not find appsettings.json");
            return;
        }

        // Opening connection to SQL server SMSS
        using var connection = new SqlConnection(connectionString);
        connection.Open();
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
            createCommand.ExecuteNonQuery();
        }

        // Parsing and saving

        // Loads XML-file to memory
        XDocument doc = XDocument.Load("soap_response.xml");

        // Defines XML-namespaces, which are used on XML file
        XNamespace ns = "http://tempuri.org/";

        // Searches for all <Product>-elements by namespace reference
        var products = doc.Descendants(ns + "Product");

        string insertSql = "MERGE INTO Products AS target USING (VALUES (@id, @name, @price)) AS " +
            "source (Id, Name, Price) ON target.Id = source.Id WHEN MATCHED THEN UPDATE SET " +
            "target.Name = source.Name, target.Price = source.Price WHEN NOT MATCHED THEN " +
            "INSERT (Id, Name, Price) VALUES (source.Id, source.Name, source.Price);";

        Log.Information("Parsing products from SOAP-response:\n");

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
                    insertCommand.ExecuteNonQuery();
                }
                Log.Information("Processing: Id={ProductId}, Name={ProductName}, Price={ProductPrice} €",
                                id,
                                name,
                                price);
            }
            transaction.Commit();
            Log.Information("\n All products are saved succesfully in transaction!");
        }
        catch (Exception ex)
        {
            // If something went wrong, rollback all changes at once!
            transaction.Rollback();
            Log.Information($"\nError during saving process, all changes are rolled back: {ex.Message}");
        }
        Log.CloseAndFlush();
    }
}
        