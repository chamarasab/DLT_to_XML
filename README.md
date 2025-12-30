# DLT_to_XML

Simple converter that reads a pipe-delimited `.dlt` file and writes an XML file conforming to the `BouncedCheque` XSD.

## Repository

https://github.com/chamarasab/DLT_to_XML

## Requirements

- .NET 10 SDK (or compatible SDK installed)

## Quickstart

From the repository root:

```bash
dotnet build
dotnet run
```

By default the program reads `sources/file.dlt` and writes `sources/new_file.xml`.

## Tests

Run the test suite (xUnit):

```bash
dotnet test
```

There are unit/integration tests under the `tests/` folder that exercise multiple DLT record mappings and validation against `sources/BouncedChequeTemplate.xsd`.

## Usage (programmatic)

You can call the converter from code:

```csharp
// DltConverter.Convert(inputPath, outputPath, xsdPath)
var ok = DltConverter.Convert("sources/file.dlt", "sources/new_file.xml", "sources/BouncedChequeTemplate.xsd");
```

`ok` will be `true` if conversion succeeded and validation (when XSD provided) passed.

## Notes

- The converter uses heuristics to map DLT `CMDC` records into `BouncedCheque` elements. If you need a different mapping, update `DltConverter.cs`.
- Generated XML is validated against `sources/BouncedChequeTemplate.xsd` during conversion if present.

## License

MIT — feel free to change the license file to suit your needs.
