using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Globalization;

class Program
{
	static void Main()
	{
		var inputPath = Path.Combine("sources", "file.dlt");
		var outputPath = Path.Combine("sources", "new_file.xml");
		var xsdPath = Path.Combine("sources", "BouncedChequeTemplate.xsd");

		try
		{
			var ok = DltConverter.Convert(inputPath, outputPath, xsdPath);
			Console.WriteLine(ok ? $"Conversion and validation successful: {outputPath}" : $"Conversion completed with validation issues: {outputPath}");
			// also produce consumer file if present
			var consumerInput = Path.Combine("sources", "file_consumer.dlt");
			var consumerOutput = Path.Combine("sources", "file_consumer.xml");
			if (File.Exists(consumerInput))
			{
				var ok2 = DltConverter.ConvertConsumer(consumerInput, consumerOutput, xsdPath);
				Console.WriteLine(ok2 ? $"Consumer conversion successful: {consumerOutput}" : $"Consumer conversion completed with validation issues: {consumerOutput}");
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Error: {ex.Message}");
		}
	}
}
