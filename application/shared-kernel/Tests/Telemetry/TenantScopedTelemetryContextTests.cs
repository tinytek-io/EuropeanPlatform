using FluentAssertions;
using SharedKernel.Domain;
using SharedKernel.Telemetry;
using Xunit;

namespace SharedKernel.Tests.Telemetry;

public sealed class TenantScopedTelemetryContextTests
{
    private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> options)
    {
        return ActivitySamplingResult.AllData;
    }

    [Fact]
    public void Set_WhenCalledWithTenantData_ShouldSetTelemetryProperties()
    {
        // Arrange
        var tenantId = new TenantId(99999);

        using var activitySource = new ActivitySource("TestSource");
        var listener = new ActivityListener();
        listener.ShouldListenTo = _ => true;
        listener.Sample = SampleAllData;
        using (listener)
        {
            ActivitySource.AddActivityListener(listener);

            using var activity = activitySource.StartActivity();
            activity.Should().NotBeNull();

            // Act
            TenantScopedTelemetryContext.Set(tenantId, "Premium");

            // Assert - OpenTelemetry Activity tags
            var tenantIdTag = activity.TagObjects.FirstOrDefault(t => t.Key == "tenant.id");
            tenantIdTag.Value.Should().Be(99999L);

            var subscriptionPlanTag = activity.Tags.FirstOrDefault(t => t.Key == "tenant.subscription_plan");
            subscriptionPlanTag.Value.Should().Be("Premium");
        }
    }
}
