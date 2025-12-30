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
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Error: {ex.Message}");
		}
	}
}
