using System.ComponentModel.DataAnnotations;

namespace Soforr.Infrastructure.Persistence;

public sealed class ConnectionStringsOptions
{
    public const string SectionName = "ConnectionStrings";

    [Required]
    public string Database { get; set; } = string.Empty;
}
