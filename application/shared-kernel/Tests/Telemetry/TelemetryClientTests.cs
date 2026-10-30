using FluentAssertions;
using NSubstitute;
using OpenTelemetry;
using OpenTelemetry.Logs;
using SharedKernel.Authentication;
using SharedKernel.Domain;
using SharedKernel.ExecutionContext;
using SharedKernel.Telemetry;
using Xunit;

namespace SharedKernel.Tests.Telemetry;

public sealed class TelemetryClientTests
{
    [Fact]
    public void TrackEvent_ShouldEmitOtelEventWithFlatAttributesAndExecutionContext()
    {
        // Arrange
        var userId = UserId.NewId();
        var tenantId = new TenantId(12345);
        var userInfo = new UserInfo { IsAuthenticated = true, Id = userId, TenantId = tenantId, Role = "Admin", Locale = "en-US" };
        var executionContext = Substitute.For<IExecutionContext>();
        executionContext.UserInfo.Returns(userInfo);
        executionContext.TenantId.Returns(tenantId);

        var logger = new ListLogger();
        var telemetryClient = new TelemetryClient(logger, executionContext);

        // Act
        telemetryClient.TrackEvent("LoginCompleted", new Dictionary<string, string>
        {
            ["event.user_id"] = userId.Value,
            ["event.login_time_in_seconds"] = "3"
        });

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Be("LoginCompleted");

        // The event name follows the attribute naming rules: snake_case on the wire.
        entry.State.Should().Contain(kv => kv.Key == "event.name" && Equals(kv.Value, "login_completed"));

        // Every property is a flat attribute — never a nested dictionary — so the OTLP
        // exporters can index each value individually in Cockpit.
        entry.State.Should().Contain(kv => kv.Key == "event.user_id" && Equals(kv.Value, userId.Value));
        entry.State.Should().Contain(kv => kv.Key == "event.login_time_in_seconds" && Equals(kv.Value, "3"));
        entry.State.Should().Contain(kv => kv.Key == "tenant.id" && Equals(kv.Value, tenantId.Value));
        entry.State.Should().Contain(kv => kv.Key == "user.id" && Equals(kv.Value, userId.Value));
        entry.State.Should().Contain(kv => kv.Key == "user.role" && Equals(kv.Value, "Admin"));

        // No SDK noise: the state carries no {OriginalFormat} entry.
        entry.State.Should().NotContain(kv => kv.Key == "{OriginalFormat}");
    }

    [Fact]
    public void TrackEvent_WhenNoTenantOrUser_ShouldOmitContextAttributes()
    {
        // Arrange — a background worker: the system user has no id and there is no tenant.
        var executionContext = Substitute.For<IExecutionContext>();
        executionContext.UserInfo.Returns(UserInfo.System);
        executionContext.TenantId.Returns((TenantId?)null);

        var logger = new ListLogger();
        var telemetryClient = new TelemetryClient(logger, executionContext);

        // Act
        telemetryClient.TrackEvent("SubscriptionCreated", new Dictionary<string, string> { ["event.tenant_id"] = "12345" });

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.State.Should().Contain(kv => kv.Key == "event.name" && Equals(kv.Value, "subscription_created"));
        entry.State.Should().Contain(kv => kv.Key == "event.tenant_id" && Equals(kv.Value, "12345"));
        entry.State.Should().NotContain(kv => kv.Key == "tenant.id");
        entry.State.Should().NotContain(kv => kv.Key == "user.id");
        entry.State.Should().NotContain(kv => kv.Key == "user.role");
    }

    [Fact]
    public void TrackPageView_ShouldEmitPageViewEventWithNameUrlAndProperties()
    {
        // Arrange
        var userId = UserId.NewId();
        var userInfo = new UserInfo { IsAuthenticated = true, Id = userId, Role = "Member", Locale = "en-US" };
        var executionContext = Substitute.For<IExecutionContext>();
        executionContext.UserInfo.Returns(userInfo);
        executionContext.TenantId.Returns((TenantId?)null);

        var logger = new ListLogger();
        var telemetryClient = new TelemetryClient(logger, executionContext);

        // Act
        telemetryClient.TrackPageView("dashboard", "https://app.staging.europeanplatform.net/dashboard", new Dictionary<string, string> { ["type"] = "page" });

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Be("page_view: dashboard");
        entry.State.Should().Contain(kv => kv.Key == "event.name" && Equals(kv.Value, "page_view"));
        entry.State.Should().Contain(kv => kv.Key == "page.name" && Equals(kv.Value, "dashboard"));
        entry.State.Should().Contain(kv => kv.Key == "url.full" && Equals(kv.Value, "https://app.staging.europeanplatform.net/dashboard"));
        entry.State.Should().Contain(kv => kv.Key == "type" && Equals(kv.Value, "page"));
        entry.State.Should().Contain(kv => kv.Key == "user.id" && Equals(kv.Value, userId.Value));
        entry.State.Should().NotContain(kv => kv.Key == "tenant.id");
    }

    [Fact]
    public void TrackPageViewPerformance_ShouldEmitMeasurementsAsFlatAttributes()
    {
        // Arrange
        var executionContext = Substitute.For<IExecutionContext>();
        executionContext.UserInfo.Returns(UserInfo.System);
        executionContext.TenantId.Returns((TenantId?)null);

        var logger = new ListLogger();
        var telemetryClient = new TelemetryClient(logger, executionContext);

        // Act
        telemetryClient.TrackPageViewPerformance("dashboard", "https://app.staging.europeanplatform.net/dashboard", new Dictionary<string, double>
        {
            ["networkConnect"] = 12.5,
            ["perfTotal"] = 1234.5
        });

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Be("page_view_performance: dashboard");
        entry.State.Should().Contain(kv => kv.Key == "event.name" && Equals(kv.Value, "page_view_performance"));
        entry.State.Should().Contain(kv => kv.Key == "page.name" && Equals(kv.Value, "dashboard"));
        entry.State.Should().Contain(kv => kv.Key == "url.full" && Equals(kv.Value, "https://app.staging.europeanplatform.net/dashboard"));

        // Measurements are flat numeric attributes, not a nested dictionary.
        entry.State.Should().Contain(kv => kv.Key == "networkConnect" && Equals(kv.Value, 12.5));
        entry.State.Should().Contain(kv => kv.Key == "perfTotal" && Equals(kv.Value, 1234.5));
    }

    [Fact]
    public void TrackInteraction_ShouldEmitInteractionEvent()
    {
        // Arrange
        var executionContext = Substitute.For<IExecutionContext>();
        executionContext.UserInfo.Returns(UserInfo.System);
        executionContext.TenantId.Returns((TenantId?)null);

        var logger = new ListLogger();
        var telemetryClient = new TelemetryClient(logger, executionContext);

        // Act
        telemetryClient.TrackInteraction("Invite member - Submit dialog", "https://app.staging.europeanplatform.net/tenants", new Dictionary<string, string> { ["type"] = "dialog" });

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Be("interaction: Invite member - Submit dialog");
        entry.State.Should().Contain(kv => kv.Key == "event.name" && Equals(kv.Value, "interaction"));
        entry.State.Should().Contain(kv => kv.Key == "interaction.name" && Equals(kv.Value, "Invite member - Submit dialog"));
        entry.State.Should().Contain(kv => kv.Key == "url.full" && Equals(kv.Value, "https://app.staging.europeanplatform.net/tenants"));
        entry.State.Should().Contain(kv => kv.Key == "type" && Equals(kv.Value, "dialog"));
    }

    [Fact]
    public void TrackClientException_ShouldEmitErrorEventWithOtelExceptionAttributes()
    {
        // Arrange
        var executionContext = Substitute.For<IExecutionContext>();
        executionContext.UserInfo.Returns(UserInfo.System);
        executionContext.TenantId.Returns((TenantId?)null);

        var logger = new ListLogger();
        var telemetryClient = new TelemetryClient(logger, executionContext);

        // Act
        telemetryClient.TrackClientException("TypeError", "Cannot read properties of undefined", "Error: stack", "component stack", "https://app.staging.europeanplatform.net/dashboard");

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Message.Should().Be("TypeError: Cannot read properties of undefined");

        // The exception.* attribute names follow the OpenTelemetry log conventions for exceptions.
        entry.State.Should().Contain(kv => kv.Key == "event.name" && Equals(kv.Value, "exception"));
        entry.State.Should().Contain(kv => kv.Key == "exception.type" && Equals(kv.Value, "TypeError"));
        entry.State.Should().Contain(kv => kv.Key == "exception.message" && Equals(kv.Value, "Cannot read properties of undefined"));
        entry.State.Should().Contain(kv => kv.Key == "exception.stacktrace" && Equals(kv.Value, "Error: stack"));
        entry.State.Should().Contain(kv => kv.Key == "component_stack" && Equals(kv.Value, "component stack"));
        entry.State.Should().Contain(kv => kv.Key == "url.full" && Equals(kv.Value, "https://app.staging.europeanplatform.net/dashboard"));
    }

    [Fact]
    public void TrackEvent_ShouldExportFlatAttributesThroughTheOpenTelemetryBridge()
    {
        // Arrange — the real OTel logging bridge with a capturing exporter: exactly what
        // AddSharedTelemetry wires in production (the Aspire dashboard locally, Cockpit deployed).
        var exporter = new CapturingExporter();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options => options.AddProcessor(new SimpleLogRecordExportProcessor(exporter))));
        var executionContext = Substitute.For<IExecutionContext>();
        executionContext.UserInfo.Returns(UserInfo.System);
        executionContext.TenantId.Returns((TenantId?)null);

        var telemetryClient = new TelemetryClient(loggerFactory.CreateLogger<TelemetryClient>(), executionContext);

        // Act — the simple export processor ships each record synchronously, so it is captured
        // before the asserts below run.
        telemetryClient.TrackEvent("LoginCompleted", new Dictionary<string, string> { ["event.user_id"] = "user-1" });

        // Assert — one exported log record: flat attributes (event.name + properties), a readable
        // body, and no SDK noise like a {OriginalFormat} or nested @Properties attribute.
        var record = exporter.Records.Should().ContainSingle().Subject;
        record.Body.Should().Be("LoginCompleted");
        record.Attributes.Should().Contain("event.name", "login_completed");
        record.Attributes.Should().Contain("event.user_id", "user-1");
        record.Attributes.Should().NotContainKey("{OriginalFormat}");
        record.Attributes.Should().NotContainKey("@Properties");
    }

    // The OTel SDK pools and recycles LogRecord instances, so the exporter snapshots each record
    // at export time instead of holding the reference.
    private sealed record CapturedRecord(object? Body, Dictionary<string, object?> Attributes);

    private sealed class CapturingExporter : BaseExporter<LogRecord>
    {
        public List<CapturedRecord> Records { get; } = [];

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                var attributes = record.Attributes?.ToDictionary(attribute => attribute.Key, attribute => attribute.Value) ?? [];
                Records.Add(new CapturedRecord(record.Body, attributes));
            }

            return ExportResult.Success;
        }
    }

    private sealed class ListLogger : ILogger<TelemetryClient>
    {
        public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
        }
    }
}
