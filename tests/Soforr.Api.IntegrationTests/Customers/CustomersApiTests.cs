using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Audit;
using Soforr.Domain.Customers;
using Soforr.Domain.Identity;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Customers;

/// <summary>
/// The Customer endpoints through the running application against real PostgreSQL: create, read, search, update and
/// deactivate, validation, who may do what, that contact details are shown only to those who manage customers, tenant isolation,
/// the audit trail (which never carries personal data) and that there is no way to delete a customer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CustomersApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public CustomersApiTests(PostgresDatabaseFixture database)
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

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string? name = "Awa Jallow", string? contact = "+220 555 0100", int? terms = 30) =>
        PostAsync(client, "/api/v1/customers", new { name, contactDetails = contact, creditTermsDays = terms });

    private static async Task<Guid> CreatedIdAsync(HttpClient client, string name = "Awa Jallow", string? contact = "+220 555 0100", int? terms = 30)
    {
        var response = await CreateAsync(client, name, contact, terms);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    // ---- create and read

    [Fact]
    public async Task ACustomer_IsCreated_WithItsDetails_AndCanBeReadBack()
    {
        var (owner, _) = await OwnerAsync();

        var response = await CreateAsync(owner, "  Awa Jallow  ", "  +220 555 0100  ", 30);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadAsync(response);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/customers/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal(("Awa Jallow", "+220 555 0100", 30, true), (created.GetProperty("name").GetString(), created.GetProperty("contactDetails").GetString(), created.GetProperty("creditTermsDays").GetInt32(), created.GetProperty("isActive").GetBoolean()));

        var read = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/customers/{id}")));
        Assert.Equal("Awa Jallow", read.GetProperty("name").GetString());
    }

    [Fact]
    public async Task OptionalDetails_MayBeLeftOut()
    {
        var (owner, _) = await OwnerAsync();

        var created = await ReadAsync(await CreateAsync(owner, "Walk-in regular", contact: null, terms: null));

        Assert.Equal(JsonValueKind.Null, created.GetProperty("contactDetails").ValueKind);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("creditTermsDays").ValueKind);
    }

    [Theory]
    [InlineData(null, 30, "name", "customer.name_required")]
    [InlineData("   ", 30, "name", "customer.name_required")]
    [InlineData("N", -1, "creditTermsDays", "customer.credit_terms_negative")]
    [InlineData("N", 366, "creditTermsDays", "customer.credit_terms_too_long")]
    public async Task InvalidCustomers_Return400_WithTheFieldTheCodeAndTheCorrelationId(string? name, int terms, string field, string code)
    {
        var (owner, organization) = await OwnerAsync();

        var response = await CreateAsync(owner, name, terms: terms);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty(field).GetArrayLength());
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-Correlation-ID")), problem.GetProperty("correlationId").GetString());
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<Customer>().CountAsync());
    }

    [Fact]
    public async Task TooLongText_IsRejected_WithTheField()
    {
        var (owner, _) = await OwnerAsync();

        var name = await CreateAsync(owner, new string('x', 201));
        var contact = await CreateAsync(owner, "N", new string('x', 501));

        Assert.Equal(HttpStatusCode.BadRequest, name.StatusCode);
        Assert.Equal("customer.name_too_long", (await ReadAsync(name)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, contact.StatusCode);
        Assert.Equal("customer.contact_details_too_long", (await ReadAsync(contact)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TwoCustomers_MayHaveTheSameName()
    {
        var (owner, _) = await OwnerAsync();

        await CreatedIdAsync(owner, "Awa Jallow");
        await CreatedIdAsync(owner, "Awa Jallow");

        var list = await ReadAsync(await owner.GetAsync(Relative("/api/v1/customers")));
        Assert.Equal(2, list.GetProperty("totalCount").GetInt32());
    }

    // ---- search

    [Fact]
    public async Task Search_ListsByName_MatchesPartsOfTheNameWithoutRegardToCase_AndPages()
    {
        var (owner, _) = await OwnerAsync();
        // The names share one letter case on purpose: the order of mixed-case names depends on the database collation.
        foreach (var name in new[] { "Zainab Sanneh", "Awa Jallow", "Ousman Jallow", "Mamadou Ceesay" })
        {
            await CreatedIdAsync(owner, name);
        }

        var all = await ReadAsync(await owner.GetAsync(Relative("/api/v1/customers")));
        var jallow = await ReadAsync(await owner.GetAsync(Relative("/api/v1/customers?q=JaLLoW")));
        var secondPage = await ReadAsync(await owner.GetAsync(Relative("/api/v1/customers?page=2&pageSize=3")));
        var none = await ReadAsync(await owner.GetAsync(Relative("/api/v1/customers?q=nobody")));

        Assert.Equal(["Awa Jallow", "Mamadou Ceesay", "Ousman Jallow", "Zainab Sanneh"], all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(["Awa Jallow", "Ousman Jallow"], jallow.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(2, jallow.GetProperty("totalCount").GetInt32());
        Assert.Equal(["Zainab Sanneh"], secondPage.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal((2, 3, 4), (secondPage.GetProperty("page").GetInt32(), secondPage.GetProperty("pageSize").GetInt32(), secondPage.GetProperty("totalCount").GetInt32()));
        Assert.Equal(0, none.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Search_TreatsPercentAndUnderscoreAsPlainText_AndNeverSearchesContactDetails()
    {
        var (owner, _) = await OwnerAsync();
        await CreatedIdAsync(owner, "100% Fresh", contact: "phone 555");
        await CreatedIdAsync(owner, "Other", contact: "fresh");

        var percent = await ReadAsync(await owner.GetAsync(Relative("/api/v1/customers?q=%25")));
        var underscore = await ReadAsync(await owner.GetAsync(Relative("/api/v1/customers?q=_")));
        var byContact = await ReadAsync(await owner.GetAsync(Relative("/api/v1/customers?q=555")));

        Assert.Equal(["100% Fresh"], percent.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(0, underscore.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, byContact.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("?page=0", "pagination.page_invalid")]
    [InlineData("?pageSize=0", "pagination.page_size_invalid")]
    [InlineData("?pageSize=101", "pagination.page_size_invalid")]
    public async Task InvalidPaging_Returns400(string query, string code)
    {
        var (owner, _) = await OwnerAsync();

        var response = await owner.GetAsync(Relative("/api/v1/customers" + query));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ATooLongSearchText_Returns400()
    {
        var (owner, _) = await OwnerAsync();

        var response = await owner.GetAsync(Relative("/api/v1/customers?q=" + new string('x', 101)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("customer.search_text_too_long", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    // ---- update

    [Fact]
    public async Task AnUpdate_ReplacesTheDetails_AndAnAbsentContactOrTermsClearsIt()
    {
        var (owner, _) = await OwnerAsync();
        var id = await CreatedIdAsync(owner);

        var response = await SendAsync(owner, HttpMethod.Put, $"/api/v1/customers/{id}", new { name = "Awa J.", contactDetails = (string?)null, creditTermsDays = (int?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadAsync(response);
        Assert.Equal("Awa J.", updated.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("contactDetails").ValueKind);
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("creditTermsDays").ValueKind);
        Assert.True(updated.GetProperty("updatedAtUtc").GetDateTimeOffset() >= updated.GetProperty("createdAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task ARejectedUpdate_ChangesNothing()
    {
        var (owner, _) = await OwnerAsync();
        var id = await CreatedIdAsync(owner, "Awa Jallow", "+220 555 0100", 30);

        var response = await SendAsync(owner, HttpMethod.Put, $"/api/v1/customers/{id}", new { name = "New name", contactDetails = "new", creditTermsDays = 9999 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var read = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/customers/{id}")));
        Assert.Equal(("Awa Jallow", "+220 555 0100", 30), (read.GetProperty("name").GetString(), read.GetProperty("contactDetails").GetString(), read.GetProperty("creditTermsDays").GetInt32()));
    }

    [Fact]
    public async Task UpdatingAnUnknownCustomer_Returns404()
    {
        var (owner, _) = await OwnerAsync();

        var response = await SendAsync(owner, HttpMethod.Put, $"/api/v1/customers/{Guid.CreateVersion7()}", new { name = "X" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("customer.not_found", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    // ---- deactivate

    [Fact]
    public async Task ADeactivatedCustomer_IsHiddenFromTheDefaultList_ButStillReadable_AndNeverDeleted()
    {
        var (owner, organization) = await OwnerAsync();
        var id = await CreatedIdAsync(owner, "Awa Jallow");
        await CreatedIdAsync(owner, "Ousman Ceesay");

        var response = await PostAsync(owner, $"/api/v1/customers/{id}/deactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ReadAsync(response)).GetProperty("isActive").GetBoolean());
        var active = await ReadAsync(await owner.GetAsync(Relative("/api/v1/customers")));
        var all = await ReadAsync(await owner.GetAsync(Relative("/api/v1/customers?includeInactive=true")));
        var single = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/customers/{id}")));
        Assert.Equal(["Ousman Ceesay"], active.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(2, all.GetProperty("totalCount").GetInt32());
        Assert.False(single.GetProperty("isActive").GetBoolean());
        await using var db = _database.CreateContext(organization);
        Assert.Equal(2, await db.Set<Customer>().CountAsync());
    }

    [Fact]
    public async Task DeactivatingTwice_ChangesNothing_AndIsAuditedOnce()
    {
        var (owner, organization) = await OwnerAsync();
        var id = await CreatedIdAsync(owner);
        var first = await ReadAsync(await PostAsync(owner, $"/api/v1/customers/{id}/deactivate", null));

        var second = await ReadAsync(await PostAsync(owner, $"/api/v1/customers/{id}/deactivate", null));

        Assert.Equal(first.GetProperty("updatedAtUtc").GetDateTimeOffset(), second.GetProperty("updatedAtUtc").GetDateTimeOffset());
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "customers.customer_deactivated"));
    }

    [Fact]
    public async Task ADeactivatedCustomer_CanStillBeUpdated()
    {
        var (owner, _) = await OwnerAsync();
        var id = await CreatedIdAsync(owner);
        await PostAsync(owner, $"/api/v1/customers/{id}/deactivate", null);

        var response = await SendAsync(owner, HttpMethod.Put, $"/api/v1/customers/{id}", new { name = "Renamed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ReadAsync(response)).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task ThereIsNoWayToDeleteACustomer()
    {
        var (owner, _) = await OwnerAsync();
        var id = await CreatedIdAsync(owner);

        var attempts = new[]
        {
            await SendAsync(owner, HttpMethod.Delete, $"/api/v1/customers/{id}", body: null),
            await SendAsync(owner, HttpMethod.Delete, "/api/v1/customers", body: null),
            await SendAsync(owner, HttpMethod.Put, $"/api/v1/customers/{id}/deactivate", body: null),
        };

        Assert.All(attempts, response => Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(Relative($"/api/v1/customers/{id}"))).StatusCode);
    }

    // ---- audit

    [Fact]
    public async Task EachChange_IsAudited_WithoutAnyPersonalData()
    {
        var (owner, organization) = await OwnerAsync();
        var id = await CreatedIdAsync(owner, "Secret Name", "secret-phone-123");
        await SendAsync(owner, HttpMethod.Put, $"/api/v1/customers/{id}", new { name = "Other Secret", contactDetails = "other-phone-456" });
        await PostAsync(owner, $"/api/v1/customers/{id}/deactivate", null);

        await using var db = _database.CreateContext(organization);
        var events = await db.Set<AuditEvent>().Where(e => e.EntityId == id).OrderBy(e => e.OccurredAtUtc).ToListAsync();

        Assert.Equal(["customers.customer_created", "customers.customer_updated", "customers.customer_deactivated"], events.Select(e => e.Action).ToArray());
        Assert.All(events, e =>
        {
            Assert.Equal("Customer", e.EntityType);
            Assert.DoesNotContain("Secret", e.Description);
            Assert.DoesNotContain("phone", e.Description);
        });
    }

    // ---- who may do what

    public enum Operation
    {
        Create,
        Search,
        Get,
        Update,
        Deactivate,
    }

    private static async Task<HttpResponseMessage> PerformAsync(HttpClient client, Operation operation, Guid customer) => operation switch
    {
        Operation.Create => await CreateAsync(client),
        Operation.Search => await client.GetAsync(Relative("/api/v1/customers")),
        Operation.Get => await client.GetAsync(Relative($"/api/v1/customers/{customer}")),
        Operation.Update => await SendAsync(client, HttpMethod.Put, $"/api/v1/customers/{customer}", new { name = "Updated" }),
        Operation.Deactivate => await PostAsync(client, $"/api/v1/customers/{customer}/deactivate", null),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    public static TheoryData<string, Operation, HttpStatusCode> Matrix()
    {
        var data = new TheoryData<string, Operation, HttpStatusCode>();
        foreach (var operation in Enum.GetValues<Operation>())
        {
            var manage = operation is Operation.Create or Operation.Update or Operation.Deactivate;
            var success = operation == Operation.Create ? HttpStatusCode.Created : HttpStatusCode.OK;
            data.Add("Anonymous", operation, HttpStatusCode.Unauthorized);
            data.Add("Owner", operation, success);
            data.Add("Manager", operation, success);
            data.Add("Staff", operation, manage ? HttpStatusCode.Forbidden : success);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryOperation_IsAllowedOrDenied_AsTheMatrixSays(string principal, Operation operation, HttpStatusCode expected)
    {
        var (owner, organization) = await OwnerAsync();
        var customer = await CreatedIdAsync(owner);
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

        var response = await PerformAsync(client, operation, customer);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ADeniedChange_ChangesNothing_AndWritesNoAuditEvent()
    {
        var (owner, organization) = await OwnerAsync();
        var id = await CreatedIdAsync(owner, "Awa Jallow");
        var staff = await SignInAsMemberAsync(_factory, _database, organization, Role.Staff);
        _clients.Add(staff);

        var update = await SendAsync(staff, HttpMethod.Put, $"/api/v1/customers/{id}", new { name = "Hacked" });
        var deactivate = await PostAsync(staff, $"/api/v1/customers/{id}/deactivate", null);

        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal("identity.forbidden", (await ReadAsync(update)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, deactivate.StatusCode);
        await using var db = _database.CreateContext(organization);
        var customer = await db.Set<Customer>().SingleAsync();
        Assert.Equal(("Awa Jallow", true), (customer.Name, customer.IsActive));
        Assert.Equal(0, await db.Set<AuditEvent>().CountAsync(e => e.Action == "customers.customer_updated" || e.Action == "customers.customer_deactivated"));
    }

    [Fact]
    public async Task ContactDetails_AreShownOnlyToThoseWhoManageCustomers()
    {
        var (owner, organization) = await OwnerAsync();
        var id = await CreatedIdAsync(owner, "Awa Jallow", "+220 555 0100", 14);
        var manager = await SignInAsMemberAsync(_factory, _database, organization, Role.Manager);
        var staff = await SignInAsMemberAsync(_factory, _database, organization, Role.Staff);
        _clients.AddRange([manager, staff]);

        var asOwner = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/customers/{id}")));
        var asManager = await ReadAsync(await manager.GetAsync(Relative($"/api/v1/customers/{id}")));
        var asStaff = await ReadAsync(await staff.GetAsync(Relative($"/api/v1/customers/{id}")));
        var listAsStaff = await ReadAsync(await staff.GetAsync(Relative("/api/v1/customers")));

        Assert.Equal("+220 555 0100", asOwner.GetProperty("contactDetails").GetString());
        Assert.Equal("+220 555 0100", asManager.GetProperty("contactDetails").GetString());
        Assert.Equal(JsonValueKind.Null, asStaff.GetProperty("contactDetails").ValueKind);
        Assert.Equal(14, asStaff.GetProperty("creditTermsDays").GetInt32()); // a cashier needs the name and the terms
        Assert.Equal("Awa Jallow", asStaff.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, listAsStaff.GetProperty("items")[0].GetProperty("contactDetails").ValueKind);
    }

    // ---- tenant isolation

    [Fact]
    public async Task AnotherOrganizationsCustomers_AreNotFound_NeverListed_AndNeverChanged()
    {
        var (owner, organization) = await OwnerAsync();
        var id = await CreatedIdAsync(owner, "Awa Jallow");
        var (other, _) = await OwnerAsync();
        await CreatedIdAsync(other, "Their customer");

        var get = await other.GetAsync(Relative($"/api/v1/customers/{id}"));
        var update = await SendAsync(other, HttpMethod.Put, $"/api/v1/customers/{id}", new { name = "Stolen" });
        var deactivate = await PostAsync(other, $"/api/v1/customers/{id}/deactivate", null);
        var theirs = await ReadAsync(await other.GetAsync(Relative("/api/v1/customers")));
        var mine = await ReadAsync(await owner.GetAsync(Relative("/api/v1/customers")));

        Assert.All([get, update, deactivate], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Equal("customer.not_found", (await ReadAsync(get)).GetProperty("code").GetString());
        Assert.Equal(["Their customer"], theirs.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(["Awa Jallow"], mine.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        await using var db = _database.CreateContext(organization);
        var customer = await db.Set<Customer>().SingleAsync();
        Assert.Equal(("Awa Jallow", true), (customer.Name, customer.IsActive));
    }

    [Fact]
    public async Task APostWithoutAnAntiforgeryToken_IsRejected()
    {
        var (owner, organization) = await OwnerAsync();

        var response = await PostAsync(owner, "/api/v1/customers", new { name = "No token" }, withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery.invalid", (await ReadAsync(response)).GetProperty("code").GetString());
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<Customer>().CountAsync());
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsTheCustomerEndpoints()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal("CreateCustomer", paths.GetProperty("/api/v1/customers").GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("SearchCustomers", paths.GetProperty("/api/v1/customers").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("GetCustomer", paths.GetProperty("/api/v1/customers/{id}").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("UpdateCustomer", paths.GetProperty("/api/v1/customers/{id}").GetProperty("put").GetProperty("operationId").GetString());
        Assert.Equal("DeactivateCustomer", paths.GetProperty("/api/v1/customers/{id}/deactivate").GetProperty("post").GetProperty("operationId").GetString());
        Assert.False(paths.GetProperty("/api/v1/customers/{id}").TryGetProperty("delete", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("CustomerDto", schemas);
        Assert.DoesNotContain("Customer", schemas);
    }
}
