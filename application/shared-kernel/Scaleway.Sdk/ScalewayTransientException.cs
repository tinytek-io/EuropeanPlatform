namespace Scaleway.Sdk;

/// <summary>
///     A retryable Scaleway transport failure — a transient server error (5xx / 408 / 429) or a lost response. The
///     deploy step retries the enclosing find-or-create on this exception; that retry is idempotent because the unit
///     re-checks existence (find) before creating, so a create that actually persisted is found, not duplicated.
///     Not sealed: the deterministic simulator derives its own fault from it so the same retry catches both.
/// </summary>
public class ScalewayTransientException(string message) : Exception(message);
