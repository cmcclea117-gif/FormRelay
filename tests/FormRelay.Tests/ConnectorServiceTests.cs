using System.Text.Json.Nodes;
using FormRelay.Api.Connectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FormRelay.Tests;

public class ConnectorServiceTests
{
    private static ConnectorService Make(Dictionary<string, string?>? cfg = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(cfg ?? []).Build(), null!, NullLogger<ConnectorService>.Instance);

    [Fact]
    public async Task Csv_connector_reads_value_and_label_columns_and_drops_duplicates()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, ["Id,Name", "1,\"Smith, Jo\"", "2,Lee", "2,Lee again", ",blank value"]);
        try
        {
            var rows = await Make().FetchAsync("csv", new JsonObject { ["path"] = path, ["valueColumn"] = "Id", ["labelColumn"] = "Name" }, null, default);
            Assert.Equal(2, rows.Count);
            Assert.Equal(new Choice("1", "Smith, Jo"), rows[0]);
            Assert.Equal("Lee", rows[1].Label);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Csv_connector_names_the_missing_column()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, ["Id,Name", "1,A"]);
        try
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Make().FetchAsync("csv", new JsonObject { ["path"] = path, ["valueColumn"] = "Code" }, null, default));
            Assert.Contains("Code", e.Message);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("DELETE FROM dbo.Forms")]
    [InlineData("EXEC sp_who")]
    public async Task Sql_connector_refuses_anything_but_select(string query)
    {
        var svc = Make(new() { ["Connectors:Secrets:erp"] = "Server=nowhere;Database=x" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.FetchAsync("sql", new JsonObject { ["query"] = query, ["valueColumn"] = "a" }, "erp", default));
    }

    [Fact]
    public async Task Sql_connector_needs_its_secret()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Make().FetchAsync("sql", new JsonObject { ["query"] = "SELECT 1 AS a", ["valueColumn"] = "a" }, "missing", default));
        Assert.Contains("secret", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rest_connector_rejects_non_http_urls()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Make().FetchAsync("rest", new JsonObject { ["url"] = "file:///c:/secrets.txt", ["valueField"] = "id" }, null, default));
    }
}
