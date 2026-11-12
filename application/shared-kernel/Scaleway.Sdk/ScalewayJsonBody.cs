using System.Text.Json;

namespace Scaleway.Sdk;

/// <summary>Request bodies the generated clients send that are not a generated record.</summary>
internal static class ScalewayJsonBody
{
    /// <summary>The <c>{}</c> a trigger-style action POSTs: the action takes no input, but a POST still carries a JSON body.</summary>
    public static readonly JsonElement EmptyObject = JsonElement.Parse("{}");
}
