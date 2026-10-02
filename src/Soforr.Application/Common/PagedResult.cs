namespace Soforr.Application.Common;

/// <summary>One page of a list. <see cref="TotalCount"/> is the number of items across all pages.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
