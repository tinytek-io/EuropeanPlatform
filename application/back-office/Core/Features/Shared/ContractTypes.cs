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
