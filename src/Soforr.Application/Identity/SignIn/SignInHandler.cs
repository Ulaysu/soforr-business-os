using Soforr.Application.Common;

namespace Soforr.Application.Identity.SignIn;

public sealed record SignInCommand(string? Email, string? Password);

public sealed class SignInHandler(ISessionService sessions) : ICommandHandler<SignInCommand, AccountDto>
{
    public Task<Result<AccountDto>> HandleAsync(SignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return sessions.SignInAsync(command.Email, command.Password, cancellationToken);
    }
}
