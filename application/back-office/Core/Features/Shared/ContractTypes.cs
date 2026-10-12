using JetBrains.Annotations;
using SharedKernel.StronglyTypedIds;

namespace BackOffice.Features.Shared;

// Mirrored contract types of account's internal API: the back-office owns the staff-facing
// contract, account owns the data, and these types keep the two sides' wire format identical.
// Every type here MUST stay in sync with account's equivalent (name, members, and serialization)
// — the shared string-enum converter is what carries enum values across the boundary.
// Strongly-typed IDs derive the same SharedKernel bases account's do, so they serialize
// identically and render as plain strings in the OpenAPI document the SPA client generates from.

/// <summary>Mirrors account's SubscriptionPlan (string-serialized on the wire).</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubscriptionPlan
{
    Basis = 0,
    Standard = 1,
    Premium = 2
}

/// <summary>Mirrors account's LoginMethod.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoginMethod
{
    OneTimePassword,
    Google
}

/// <summary>Mirrors account's PaymentTransactionStatus.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentTransactionStatus
{
    Succeeded,
    Failed,
    Pending,
    Refunded,
    Cancelled
}

/// <summary>Mirrors account's BillingEventType.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BillingEventType
{
    SubscriptionCreated,
    SubscriptionRenewed,
    SubscriptionUpgraded,
    SubscriptionDowngradeScheduled,
    SubscriptionDowngradeCancelled,
    SubscriptionDowngraded,
    SubscriptionCancelled,
    SubscriptionReactivated,
    SubscriptionExpired,
    SubscriptionImmediatelyCancelled,
    SubscriptionSuspended,
    SubscriptionPastDue,
    PaymentFailed,
    PaymentRecovered,
    PaymentRefund,
    BillingInfoAdded,
    BillingInfoUpdated,
    PaymentMethodUpdated,
    NoOp,
    Unclassified
}

/// <summary>Mirrors account's PaymentTransactionId (a ULID).</summary>
[PublicAPI]
public sealed record PaymentTransactionId(string Value) : StronglyTypedUlid<PaymentTransactionId>(Value);

/// <summary>Mirrors account's BillingEventId (a ULID).</summary>
[PublicAPI]
public sealed record BillingEventId(string Value) : StronglyTypedUlid<BillingEventId>(Value);

/// <summary>Mirrors account's UserRole.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserRole
{
    Member,
    Admin,
    Owner
}

/// <summary>Mirrors account's PlannedSubscriptionChange.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlannedSubscriptionChange
{
    Cancellation,
    ScheduledPlanChange
}

/// <summary>Mirrors account's DeviceType.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceType
{
    Unknown,
    Desktop,
    Mobile,
    Tablet
}

/// <summary>Mirrors account's SessionRevokedReason.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionRevokedReason
{
    LoggedOut,
    Revoked,
    ReplayAttackDetected,
    SwitchTenant
}

/// <summary>Mirrors account's ExternalProviderType.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExternalProviderType
{
    Google
}

/// <summary>Mirrors account's LoginEventKind.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoginEventKind
{
    Email,
    External
}

/// <summary>Mirrors account's LoginEventOutcome.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoginEventOutcome
{
    Pending,
    Succeeded,
    Failed
}

/// <summary>Mirrors account's TenantState.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TenantState
{
    Active,
    Suspended
}

/// <summary>Mirrors account's SuspensionReason.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuspensionReason
{
    PaymentFailed,
    CustomerDeleted
}

/// <summary>Mirrors account's DriftDiscrepancyKind.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftDiscrepancyKind
{
    MissingEvent,
    ExtraEvent,
    FieldDisagree,
    SubscriptionStateMismatch,
    UnclassifiedStripeEvent,
    UnsupportedStripeApiVersion,
    StripeEventPayloadDivergence,
    BillingEventDenormalizationStale,
    AmountExcludingTaxClamped,
    ScheduledPriceMissing
}

/// <summary>Mirrors account's DriftSeverity.</summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftSeverity
{
    Warning,
    Critical
}

/// <summary>Mirrors account's DriftDiscrepancy (the drift banner's row shape).</summary>
[PublicAPI]
public sealed record DriftDiscrepancy(
    DriftDiscrepancyKind Kind,
    string Description,
    DriftSeverity Severity,
    BillingEventType? ExpectedEventType = null,
    string? ExpectedValue = null,
    string? ActualValue = null,
    DateTimeOffset? OccurredAt = null
);

/// <summary>
///     Mirrors account's FeatureFlagSource: the snake-case wire vocabulary the frontend filters on
///     travels through JsonStringEnumMemberName, so both the member names and the wire names must
///     stay in sync.
/// </summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeatureFlagSource
{
    [JsonStringEnumMemberName("manual_override")]
    Manual,

    [JsonStringEnumMemberName("plan")] Plan,

    [JsonStringEnumMemberName("ab_rollout")]
    AbRollout,

    [JsonStringEnumMemberName("default")] Default
}
