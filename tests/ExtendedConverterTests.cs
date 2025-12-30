using System;
using System.IO;
using Xunit;

public class ExtendedConverterTests
{
    string RepoPath()
    {
        var root = Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(root, "..", "..", ".."));
    }

    [Fact]
    public void Convert_MultipleRecords_ProducesMultipleBouncedCheques()
    {
        var repo = RepoPath();
        var xsd = Path.Combine(repo, "sources", "BouncedChequeTemplate.xsd");
        var input = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dlt");
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");

        var dlt = string.Join(Environment.NewLine, new[] {
            "HDHD|BATCH123|617|30-Nov-2025|30-Nov-2025|000004|004",
            "CMDC|BATCH123|10|0012345678|123456|1000|LKR|19-Nov-2025|001||561691223V|||999|09:01:001|||JOHN DOE||10 SOME STREET  CITY 10000|||10000||||001|",
            "CMDC|BATCH123|10|0098765432|654321|500|LKR|10-Nov-2025|001||628633916V|||999|09:01:001|||JANE SMITH||44 OTHER AVE CITY 20000|||20000||||001|",
            "TLTL|BATCH123|617|2"
        });

        File.WriteAllText(input, dlt);

        try
        {
            var ok = DltConverter.Convert(input, output, xsd);
            Assert.True(File.Exists(output));
            var xml = File.ReadAllText(output);
            Assert.Contains("BouncedCheque", xml);
            // expect two CMDC -> two BouncedCheque entries
            Assert.Equal(2, CountOccurrences(xml, "<BouncedCheque"));
            Assert.True(ok);
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void Convert_InvalidDates_DoesNotAddEmptyDateElements()
    {
        var repo = RepoPath();
        var xsd = Path.Combine(repo, "sources", "BouncedChequeTemplate.xsd");
        var input = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dlt");
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");

        var dlt = string.Join(Environment.NewLine, new[] {
            "HDHD|BATCHX|617|30-Nov-2025|30-Nov-2025|000004|004",
            "CMDC|BATCHX|10|0001112223|111222|250|LKR|not-a-date|001||000000000V|||999|09:01:001|||ALICE||ADDR 123 CITY|||123||||001|",
            "TLTL|BATCHX|617|1"
        });

        File.WriteAllText(input, dlt);

        try
        {
            var ok = DltConverter.Convert(input, output, xsd);
            Assert.True(File.Exists(output));
            var xml = File.ReadAllText(output);
            // should not have an empty <DateDishonoured></DateDishonoured> because parser only adds when valid
            Assert.DoesNotMatch("<DateDishonoured>\s*</DateDishonoured>", xml);
            Assert.True(ok);
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void Convert_MissingOptionalFields_StillValid()
    {
        var repo = RepoPath();
        var xsd = Path.Combine(repo, "sources", "BouncedChequeTemplate.xsd");
        var input = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dlt");
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");

        var dlt = string.Join(Environment.NewLine, new[] {
            "HDHD|BATCHY|617|30-Nov-2025|30-Nov-2025|000004|004",
            // minimal CMDC with many missing fields
            "CMDC|BATCHY|||||LKR|||001||||||||||||||||||||",
            "TLTL|BATCHY|617|1"
        });

        File.WriteAllText(input, dlt);

        try
        {
            var ok = DltConverter.Convert(input, output, xsd);
            Assert.True(File.Exists(output));
            Assert.True(ok);
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    int CountOccurrences(string source, string substring)
    {
        int count = 0, index = 0;
        while ((index = source.IndexOf(substring, index, StringComparison.Ordinal)) != -1)
        {
            count++; index += substring.Length;
        }
        return count;
    }
}
