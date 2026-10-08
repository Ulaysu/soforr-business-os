using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Common;
using Soforr.Domain.Customers;
using Soforr.Domain.Identity;
using Soforr.Domain.Sales;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Intelligence;

/// <summary>
/// <c>GET /api/v1/intelligence/receivables</c> through the running application: who may call it, validation, the shape of the
/// answer and tenant isolation. The application uses the real clock here, so due dates are placed relative to now and well away
/// from the bucket edges; exact boundaries are covered with a fixed clock in <see cref="ReceivablesTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReceivablesApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public ReceivablesApiTests(PostgresDatabaseFixture database)
    {
        _database = database;
        _factory = CreateFactory(database.ConnectionString);
    }

    public void Dispose()
    {
        _clients.ForEach(client => client.Dispose());
        _factory.Dispose();
    }

    private async Task<(HttpClient Owner, Guid Organization)> OwnerAsync()
    {
        var client = NewClient(_factory);
        _clients.Add(client);
        var (_, organization, _) = await RegisterAndSignInAsync(client);
        return (client, organization);
    }

    private static async Task<Guid> ProductAsync(HttpClient owner, string name)
    {
        var response = await PostAsync(owner, "/api/v1/products", new { sku = "SKU-" + Guid.NewGuid().ToString("N"), name, unitOfMeasure = "bag", sellingPrice = 10, costPrice = 6 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CustomerAsync(Guid organization, string name)
    {
        var customer = Customer.Create(Guid.CreateVersion7(), organization, name, null, 30, DateTimeOffset.UtcNow.AddDays(-300));
        await using var db = _database.CreateContext(organization);
        db.Set<Customer>().Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>A completed sale on account of <paramref name="owed"/>, due <paramref name="daysPastDue"/> days ago (negative: in the future).</summary>
    private async Task OwesAsync(Guid organization, Guid product, Guid customer, decimal owed, int daysPastDue)
    {
        var gmd = Currency.Create("GMD");
        var due = DateTimeOffset.UtcNow.AddDays(-daysPastDue).AddHours(-2);
        var line = new SaleLineInput(Guid.CreateVersion7(), product, "SKU", "Product", "bag", Quantity.Create(owed), Money.Create(1m, gmd), Money.Create(1m, gmd), Money.Create(0.5m, gmd), null);
        var sale = Sale.Complete(Guid.CreateVersion7(), organization, gmd, Guid.CreateVersion7(), due.AddDays(-30), [line], customer, due);
        await using var db = _database.CreateContext(organization);
        db.Set<Sale>().Add(sale);
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string query = "") =>
        client.GetAsync(Relative("/api/v1/intelligence/receivables" + query));

    [Fact]
    public async Task TheReport_ReturnsTheTotalsBucketsAndCustomers_AsJson()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner, "Rice");
        var awa = await CustomerAsync(organization, "Awa Jallow");
        var bakary = await CustomerAsync(organization, "Bakary Ceesay");
        await OwesAsync(organization, product, awa, 100m, daysPastDue: 10);
        await OwesAsync(organization, product, awa, 40m, daysPastDue: 100);
        await OwesAsync(organization, product, bakary, 25m, daysPastDue: -5);

        var response = await GetAsync(owner);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(("GMD", "Europe/London"), (body.GetProperty("currency").GetString(), body.GetProperty("timeZoneId").GetString()));
        Assert.Equal((165m, 25m, 140m), (body.GetProperty("totalOutstanding").GetDecimal(), body.GetProperty("totalNotYetDue").GetDecimal(), body.GetProperty("totalOverdue").GetDecimal()));
        Assert.Equal((2, 1), (body.GetProperty("customersWithBalance").GetInt32(), body.GetProperty("customersOverdue").GetInt32()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("materialityThreshold").ValueKind);
        var buckets = body.GetProperty("buckets");
        Assert.Equal((100m, 0m, 0m, 40m), (buckets.GetProperty("days0To30").GetDecimal(), buckets.GetProperty("days31To60").GetDecimal(), buckets.GetProperty("days61To90").GetDecimal(), buckets.GetProperty("days91Plus").GetDecimal()));
        var customers = body.GetProperty("customers").EnumerateArray().ToArray();
        Assert.Equal(["Awa Jallow", "Bakary Ceesay"], customers.Select(c => c.GetProperty("name").GetString()!).ToArray());
        var sales = customers[0].GetProperty("overdueSales").EnumerateArray().ToArray();
        Assert.Equal(["91+", "0-30"], sales.Select(s => s.GetProperty("bucket").GetString()!).ToArray());
        Assert.Equal((100, 10), (sales[0].GetProperty("daysPastDue").GetInt32(), sales[1].GetProperty("daysPastDue").GetInt32()));
        Assert.Empty(customers[1].GetProperty("overdueSales").EnumerateArray());
    }

    [Fact]
    public async Task AnEmptyBusiness_IsNotAnError()
    {
        var (owner, _) = await OwnerAsync();

        var body = await ReadAsync(await GetAsync(owner));

        Assert.Equal(0m, body.GetProperty("totalOutstanding").GetDecimal());
        Assert.Empty(body.GetProperty("customers").EnumerateArray());
    }

    public enum Principal
    {
        Anonymous,
        Owner,
        Manager,
        Staff,
    }

    [Theory]
    [InlineData(Principal.Anonymous, HttpStatusCode.Unauthorized)]
    [InlineData(Principal.Owner, HttpStatusCode.OK)]
    [InlineData(Principal.Manager, HttpStatusCode.OK)]
    [InlineData(Principal.Staff, HttpStatusCode.Forbidden)]
    public async Task OnlyThoseWhoMayViewInsights_MaySeeTheReceivables(Principal principal, HttpStatusCode expected)
    {
        var (owner, organization) = await OwnerAsync();
        var client = principal switch
        {
            Principal.Owner => owner,
            Principal.Anonymous => NewClient(_factory),
            _ => await SignInAsMemberAsync(_factory, _database, organization, Enum.Parse<Role>(principal.ToString())),
        };
        if (principal != Principal.Owner)
        {
            _clients.Add(client);
        }

        var response = await GetAsync(client);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("?top=0")]
    [InlineData("?top=101")]
    public async Task InvalidTop_Returns400_WithTheCodeAndTheField(string query)
    {
        var (owner, _) = await OwnerAsync();

        var response = await GetAsync(owner, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("receivables.top_invalid", problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty("top").GetArrayLength());
    }

    [Fact]
    public async Task AnotherOrganizationsBalances_AreNeverInTheReport()
    {
        var (first, firstOrganization) = await OwnerAsync();
        var (second, secondOrganization) = await OwnerAsync();
        await OwesAsync(firstOrganization, await ProductAsync(first, "Mine"), await CustomerAsync(firstOrganization, "Mine"), 10m, 5);
        await OwesAsync(secondOrganization, await ProductAsync(second, "Theirs"), await CustomerAsync(secondOrganization, "Theirs"), 700m, 5);

        var mine = await ReadAsync(await GetAsync(first));
        var theirs = await ReadAsync(await GetAsync(second));

        Assert.Equal(10m, mine.GetProperty("totalOverdue").GetDecimal());
        Assert.Equal(["Mine"], mine.GetProperty("customers").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(700m, theirs.GetProperty("totalOverdue").GetDecimal());
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsTheEndpoint()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/v1/intelligence/receivables").GetProperty("get");
        Assert.Equal("GetReceivables", operation.GetProperty("operationId").GetString());
        Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("400", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("ReceivablesDto", schemas);
        Assert.DoesNotContain("ReceivableSale", schemas);
    }
}
