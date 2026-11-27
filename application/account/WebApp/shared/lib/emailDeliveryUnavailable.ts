import type { HttpError } from "@repo/infrastructure/http/errorHandler";

// Matches EmailRecipientDomainRefusal.ProblemReason on the backend.
const emailDeliveryUnavailableReason = "email-recipient-domain-unavailable";

export interface EmailDeliveryUnavailable {
  recipientDomain: string;
  statusUrl: string;
}

/**
 * Returns the details when the email provider temporarily refused the recipient's domain (a 503 the backend marks
 * with a reason), so it is told apart from a 503 the gateway returns when a service is down.
 */
export function getEmailDeliveryUnavailable(error: unknown): EmailDeliveryUnavailable | null {
  const httpError = error as HttpError | null | undefined;
  if (httpError?.kind !== "server" || httpError.status !== 503) {
    return null;
  }

  const problem = httpError.problemDetails as Record<string, unknown> | undefined;
  if (problem?.reason !== emailDeliveryUnavailableReason) {
    return null;
  }

  return {
    recipientDomain: typeof problem.recipientDomain === "string" ? problem.recipientDomain : "",
    statusUrl: typeof problem.statusUrl === "string" ? problem.statusUrl : "https://status.scaleway.com"
  };
}

/**
 * Mutation onError for flows that send email: the page shows EmailDeliveryUnavailableAlert inline, so the global
 * toast is skipped for that error only. Every other error keeps the global handling.
 */
export function handleEmailMutationError(error: unknown) {
  if (getEmailDeliveryUnavailable(error) || (error as HttpError | null)?.kind === "validation") {
    return;
  }
  // Same as the default mutation onError: an unhandled rejection reaches the global toast handler.
  Promise.reject(error);
}
