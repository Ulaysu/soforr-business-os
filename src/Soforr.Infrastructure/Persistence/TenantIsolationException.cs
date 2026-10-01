namespace Soforr.Infrastructure.Persistence;

/// <summary>
/// A save would have written a tenant-owned row of another organization. It is a defect (a handler took an id from
/// the caller without resolving it through the filtered queryable), never an expected failure, so it is an
/// exception and surfaces as a 500 rather than as a client error.
/// </summary>
public sealed class TenantIsolationException(string message) : InvalidOperationException(message);
