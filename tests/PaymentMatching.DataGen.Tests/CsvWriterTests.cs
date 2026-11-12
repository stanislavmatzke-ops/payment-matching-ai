using PaymentMatching.DataGen.Csv;

namespace PaymentMatching.DataGen.Tests;

public class CsvWriterTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("", "")]
    public void Escapes_fields_per_rfc4180(string field, string expected) =>
        Assert.Equal(expected, CsvWriter.Escape(field));

    [Fact]
    public void Writes_header_and_rows_with_lf_line_endings()
    {
        var csv = CsvWriter.Write(["a", "b"], [["1", "x;y"]]);

        Assert.Equal("a;b\n1;\"x;y\"\n", csv);
    }

    [Fact]
    public void Writes_four_files_with_headers()
    {
        var files = DatasetCsv.ToCsv(DataGenerator.Generate(new GeneratorOptions { PaymentCount = 20 }));

        Assert.Equal(4, files.Count);
        Assert.StartsWith("payment_id;booking_date;amount;currency;", files[DatasetCsv.StatementFile], StringComparison.Ordinal);
        Assert.StartsWith("payment_id;case_type;expected_outcome;", files[DatasetCsv.ExpectedFile], StringComparison.Ordinal);
    }
}
