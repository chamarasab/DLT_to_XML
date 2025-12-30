using System.IO;
using Xunit;

public class ConverterTests
{
    [Fact]
    public void Convert_CreatesValidXmlAndPassesXsd()
    {
        var root = Directory.GetCurrentDirectory();
        // project root is tests/ bin context when running; compute paths relative to repo
        var repoRoot = Path.Combine(root, "..", "..", "..");
        var input = Path.GetFullPath(Path.Combine(repoRoot, "sources", "file.dlt"));
        var xsd = Path.GetFullPath(Path.Combine(repoRoot, "sources", "BouncedChequeTemplate.xsd"));
        var output = Path.GetFullPath(Path.Combine(repoRoot, "sources", "test_new_file.xml"));

        if (File.Exists(output)) File.Delete(output);

        var ok = DltConverter.Convert(input, output, xsd);

        Assert.True(File.Exists(output), "Output XML file should exist");
        Assert.True(ok, "XML should validate against XSD");

        // cleanup
        if (File.Exists(output)) File.Delete(output);
    }
}
