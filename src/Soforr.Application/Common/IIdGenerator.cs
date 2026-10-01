namespace Soforr.Application.Common;

/// <summary>Generates primary keys. Identifiers are created in application code, not by the database.</summary>
public interface IIdGenerator
{
    Guid NewId();
}
