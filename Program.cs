using System;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

class Program
{
    static void Main(string[] args)
    {
        // Looad configuration from appsettings.json
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // Retrieves connection string
        string connectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrEmpty(connectionString))
        {
            Console.WriteLine("Error: Connection string did not find appsettings.json");
            return;
        }

        // Opening connection to SQL server SMSS
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            Console.WriteLine("Connection to SQL server was successful using appsettings.json!");


            string createTableSql = @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Products')
                BEGIN
                    CREATE TABLE Products (
                        Id INT,
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

            string insertSql = "INSERT INTO Products (Id, Name, Price) VALUES (@id, @name, @price);";

            Console.WriteLine("Parsing products from SOAP-response:\n");

            foreach (var p in products)
            {
                if (int.TryParse(p.Element(ns + "Id")?.Value, out int id) &&
                    int.TryParse(p.Element(ns + "Price")?.Value, out int price))
                {
                    string name = p.Element(ns + "Name")?.Value;

                    using (var insertCommand = new SqlCommand(insertSql, connection))
                    {
                        insertCommand.Parameters.AddWithValue("@id", id);
                        insertCommand.Parameters.AddWithValue("@name", name ?? (object)DBNull.Value);
                        insertCommand.Parameters.AddWithValue("@price", price);
                        insertCommand.ExecuteNonQuery();
                    }
                    Console.WriteLine($"ID: {id}, Nimi: {name}, Hinta: {price} €");
                }

                
            }
        }
    }
}