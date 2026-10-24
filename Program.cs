using System;
using System.Xml.Linq;

class Program
{
    static void Main(string[] args)
    {
        // Loads XML-file to memory
        XDocument doc = XDocument.Load("soap_response.xml");

        // Defines XML-namespaces, which are used on XML file
        XNamespace ns = "http://tempuri.org/";

        // Searches for all <Product>-elements by namespace reference
        var products = doc.Descendants(ns + "Product");

        Console.WriteLine("Parsing products from SOAP-response:\n");

        foreach (var p in products)
        {
            string id = p.Element(ns + "Id")?.Value;
            string name = p.Element(ns + "Name")?.Value;
            string price = p.Element(ns + "Price")?.Value;

            Console.WriteLine($"ID: {id}, Nimi: {name}, Hinta: {price} €");
        }
    }
}