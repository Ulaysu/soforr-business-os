using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;

namespace Soforr.Api.IntegrationTests;

/// <summary>
/// The web client's types are generated from a committed snapshot of the API's OpenAPI document
/// (<c>src/Soforr.Web/openapi/openapi.json</c>, then <c>npm run api:types</c>). This test keeps the snapshot honest: if the API
/// changes without the snapshot being regenerated, it fails, so the client can never silently drift from the API. To regenerate, run
/// this test once with the environment variable <c>SOFORR_UPDATE_OPENAPI=1</c> (it rewrites the file), then run
/// <c>npm run api:types</c> in <c>src/Soforr.Web</c> and commit both.
/// </summary>
public class OpenApiSnapshotTests
{
    private static string SnapshotPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Soforr.BusinessOS.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("The solution root was not found."), "src", "Soforr.Web", "openapi", "openapi.json");
    }

    [Fact]
    public async Task TheCommittedOpenApiSnapshot_MatchesTheDocumentTheApiServes()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();

        var live = JsonNode.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)))!.AsObject();
        live.Remove("servers"); // the address of the host that served it is not part of the contract

        var path = SnapshotPath();
        if (Environment.GetEnvironmentVariable("SOFORR_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(path, live.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        }

        var committed = JsonNode.Parse(await File.ReadAllTextAsync(path))!;

        Assert.True(
            JsonNode.DeepEquals(committed, live),
            "src/Soforr.Web/openapi/openapi.json is out of date with the API. Run this test with SOFORR_UPDATE_OPENAPI=1, then `npm run api:types` in src/Soforr.Web, and commit both.");
    }
}
