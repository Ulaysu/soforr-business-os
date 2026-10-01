using Soforr.Application.Common;

namespace Soforr.Application.Identity.GetCurrentUser;

public sealed record GetCurrentUserQuery;

public sealed class GetCurrentUserHandler(ICurrentUser currentUser) : IQueryHandler<GetCurrentUserQuery, AccountDto>
{
    public Task<Result<AccountDto>> HandleAsync(GetCurrentUserQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Task.FromResult(currentUser.UserId is { } userId
            ? Result.Success(new AccountDto(userId, currentUser.OrganizationId, currentUser.Role))
            : Result.Failure<AccountDto>(IdentityErrors.NotAuthenticated));
    }
}
