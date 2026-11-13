using SharedKernel.Authentication;
using SharedKernel.Domain;
using SharedKernel.ExecutionContext;

namespace SharedKernel.Telemetry;

/// <summary>
///     Attributes telemetry to an explicit tenant when the work is not done on behalf of the injected
///     execution context's tenant: a worker has no tenant at all, and a request can process another
///     tenant's subscription. The override flows with the async control flow, so it applies to the rest
///     of the calling operation and is picked up by <see cref="TelemetryClient" /> even when no Activity
///     exists.
/// </summary>
public static class TenantScopedTelemetryContext
{
    private static readonly AsyncLocal<IExecutionContext?> ExecutionContextOverride = new();

    internal static IExecutionContext? Current => ExecutionContextOverride.Value;

    public static void Set(TenantId tenantId, string? subscriptionPlan)
    {
        var userInfo = new UserInfo { IsAuthenticated = false, Locale = "en-US", SubscriptionPlan = subscriptionPlan };
        ExecutionContextOverride.Value = new BackgroundWorkerExecutionContext(tenantId, userInfo);

        Activity.Current?.SetTag("tenant.id", tenantId.Value);
        Activity.Current?.SetTag("tenant.subscription_plan", subscriptionPlan);
    }
}
