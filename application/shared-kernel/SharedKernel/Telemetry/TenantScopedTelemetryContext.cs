using SharedKernel.Domain;

namespace SharedKernel.Telemetry;

public static class TenantScopedTelemetryContext
{
    public static void Set(TenantId tenantId, string? subscriptionPlan)
    {
        Activity.Current?.SetTag("tenant.id", tenantId.Value);
        Activity.Current?.SetTag("tenant.subscription_plan", subscriptionPlan);
    }
}
