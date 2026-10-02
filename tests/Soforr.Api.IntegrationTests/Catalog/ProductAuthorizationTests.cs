using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Identity;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Catalog;

/// <summary>
/// The authorization matrix and tenant isolation of the Product endpoints through the real application (ADR 0002):
/// anonymous, each role, and a member of another organization, against every operation.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProductAuthorizationTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public ProductAuthorizationTests(PostgresDatabaseFixture database)
    {
        _database = database;
        _factory = CreateFactory(database.ConnectionString);
    }

    public void Dispose()
    {
        _clients.ForEach(client => client.Dispose());
        _factory.Dispose();
    }

    public enum Operation
    {
        Create,
        Search,
        Get,
        Update,
        Deactivate,
    }

    private static readonly object Body = new { sku = "NEW-SKU", name = "New", unitOfMeasure = "bag", sellingPrice = 1, costPrice = 1 };

    private static async Task<HttpResponseMessage> PerformAsync(HttpClient client, Operation operation, Guid productId) => operation switch
    {
        Operation.Create => await PostAsync(client, "/api/v1/products", Body),
        Operation.Search => await client.GetAsync(Relative("/api/v1/products")),
        Operation.Get => await client.GetAsync(Relative($"/api/v1/products/{productId}")),
        Operation.Update => await SendAsync(client, HttpMethod.Put, $"/api/v1/products/{productId}", new { sku = "UPD-SKU", name = "Upd", unitOfMeasure = "bag", sellingPrice = 1, costPrice = 1 }),
        Operation.Deactivate => await PostAsync(client, $"/api/v1/products/{productId}/deactivate", body: null),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    // Anonymous: 401 everywhere. Owner and Manager: everything allowed. Staff: may view, not change.
    public static TheoryData<string, Operation, HttpStatusCode> Matrix()
    {
        var data = new TheoryData<string, Operation, HttpStatusCode>();
        foreach (var operation in Enum.GetValues<Operation>())
        {
            data.Add("Anonymous", operation, HttpStatusCode.Unauthorized);
            data.Add("Owner", operation, operation == Operation.Create ? HttpStatusCode.Created : HttpStatusCode.OK);
            data.Add("Manager", operation, operation == Operation.Create ? HttpStatusCode.Created : HttpStatusCode.OK);
        }

        foreach (var viewing in new[] { Operation.Search, Operation.Get })
        {
            data.Add("Staff", viewing, HttpStatusCode.OK);
        }

        foreach (var changing in new[] { Operation.Create, Operation.Update, Operation.Deactivate })
        {
            data.Add("Staff", changing, HttpStatusCode.Forbidden);
        }

        return data;
    }

    private async Task<(HttpClient Owner, Guid Organization, Guid ProductId)> OrganizationWithAProductAsync()
    {
        var owner = NewClient(_factory);
        _clients.Add(owner);
        var (_, organization, _) = await RegisterAndSignInAsync(owner);
        var created = await PostAsync(owner, "/api/v1/products", new { sku = "SEED", name = "Seed", unitOfMeasure = "bag", sellingPrice = 1, costPrice = 1 });
        created.EnsureSuccessStatusCode();
        return (owner, organization, (await ReadAsync(created)).GetProperty("id").GetGuid());
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryOperation_IsAllowedOrDenied_AsTheMatrixSays(string principal, Operation operation, HttpStatusCode expected)
    {
        var (owner, organization, productId) = await OrganizationWithAProductAsync();
        HttpClient client;
        if (principal == "Anonymous")
        {
            client = NewClient(_factory);
            _clients.Add(client);
        }
        else if (principal == "Owner")
        {
            client = owner;
        }
        else
        {
            client = await SignInAsMemberAsync(_factory, _database, organization, Enum.Parse<Role>(principal));
            _clients.Add(client);
        }

        var response = await PerformAsync(client, operation, productId);

        Assert.Equal(expected, response.StatusCode);
    }

    // Another organization's Owner may use the operations but must not reach the first organization's product.
    [Theory]
    [InlineData(Operation.Get)]
    [InlineData(Operation.Update)]
    [InlineData(Operation.Deactivate)]
    public async Task AForeignProduct_IsNotFound_ForAnOwnerOfAnotherOrganization(Operation operation)
    {
        var (_, _, productId) = await OrganizationWithAProductAsync();
        var other = NewClient(_factory);
        _clients.Add(other);
        await RegisterAndSignInAsync(other);

        var response = await PerformAsync(other, operation, productId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("product.not_found", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AForeignProduct_IsUnchanged_AfterTheOtherOrganizationTriedToChangeIt()
    {
        var (owner, _, productId) = await OrganizationWithAProductAsync();
        var other = NewClient(_factory);
        _clients.Add(other);
        await RegisterAndSignInAsync(other);

        await PerformAsync(other, Operation.Update, productId);
        await PerformAsync(other, Operation.Deactivate, productId);

        var stored = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/products/{productId}")));
        Assert.Equal("SEED", stored.GetProperty("sku").GetString());
        Assert.True(stored.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Search_ListsOnlyTheCallersOwnProducts_AndTheSameSkuMayExistInBoth()
    {
        var (owner, _, _) = await OrganizationWithAProductAsync();
        var other = NewClient(_factory);
        _clients.Add(other);
        await RegisterAndSignInAsync(other);
        var sameSku = await PostAsync(other, "/api/v1/products", new { sku = "seed", name = "Theirs", unitOfMeasure = "bag", sellingPrice = 1, costPrice = 1 });

        var mine = await ReadAsync(await owner.GetAsync(Relative("/api/v1/products")));
        var theirs = await ReadAsync(await other.GetAsync(Relative("/api/v1/products")));

        Assert.Equal(HttpStatusCode.Created, sameSku.StatusCode);
        Assert.Equal(["Seed"], mine.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(["Theirs"], theirs.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
    }
}
