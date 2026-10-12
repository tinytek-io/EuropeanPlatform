namespace BackOffice.Authentication;

public sealed class BackOfficeClientPrincipal
{
    [JsonPropertyName("auth_typ")]
    public string? AuthenticationType { get; init; }

    [JsonPropertyName("name_typ")]
    public string? NameType { get; init; }

    [JsonPropertyName("role_typ")]
    public string? RoleType { get; init; }

    [JsonPropertyName("claims")]
    public BackOfficeClientPrincipalClaim[]? Claims { get; init; }
}

public sealed class BackOfficeClientPrincipalClaim
{
    [JsonPropertyName("typ")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("val")]
    public string Value { get; init; } = string.Empty;
}
