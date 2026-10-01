using Soforr.Application.Common;

namespace Soforr.Infrastructure.Common;

/// <summary>Generates time-ordered (version 7) UUIDs, which keep primary-key indexes compact.</summary>
internal sealed class GuidV7IdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();
}
