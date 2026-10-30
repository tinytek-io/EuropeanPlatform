namespace Scaleway.Sdk;

/// <summary>
///     Shared parsing for Scaleway's UUID identifiers. The identifier types stay separate (that separation is the
///     point — see <see cref="ProjectId" />), but the validation behind them is one implementation so the two can
///     never drift on what counts as well-formed.
/// </summary>
internal static class ScalewayIdentifier
{
    /// <summary>
    ///     Accepts any format <see cref="Guid" /> understands and normalises to the hyphenated lowercase form
    ///     Scaleway uses, so a braced or upper-case UUID pasted from the console still produces the wire value.
    /// </summary>
    internal static bool TryParse(string? value, out Guid result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = Guid.Empty;
            return false;
        }

        return Guid.TryParse(value, out result);
    }
}
