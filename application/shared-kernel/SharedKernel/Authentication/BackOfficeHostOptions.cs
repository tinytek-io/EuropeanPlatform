using System.ComponentModel.DataAnnotations;

namespace SharedKernel.Authentication;

public sealed class BackOfficeHostOptions
{
    public const string SectionName = "BackOffice";

    [Required(AllowEmptyStrings = false)]
    public string Host { get; init; } = string.Empty;

    public string? AdminsGroupId { get; init; }
}
