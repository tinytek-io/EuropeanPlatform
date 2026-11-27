import { Trans } from "@lingui/react/macro";
import { Alert, AlertDescription, AlertTitle } from "@repo/ui/components/Alert";
import { Link } from "@repo/ui/components/Link";
import { AlertTriangleIcon } from "lucide-react";

import { getEmailDeliveryUnavailable } from "@/shared/lib/emailDeliveryUnavailable";

interface EmailDeliveryUnavailableAlertProps {
  error: unknown;
  suggestGoogle?: boolean;
}

export function EmailDeliveryUnavailableAlert({
  error,
  suggestGoogle = false
}: Readonly<EmailDeliveryUnavailableAlertProps>) {
  const unavailable = getEmailDeliveryUnavailable(error);
  if (!unavailable) {
    return null;
  }

  const { recipientDomain, statusUrl } = unavailable;

  return (
    <Alert variant="warning" className="text-left">
      <AlertTriangleIcon aria-hidden="true" />
      <AlertTitle>
        <Trans>We can't deliver email to {recipientDomain} right now</Trans>
      </AlertTitle>
      <AlertDescription className="flex flex-col gap-1 text-foreground">
        <span>
          <Trans>There is an issue between our email provider and yours. Please try again later.</Trans>
        </span>
        {suggestGoogle && (
          <span>
            <Trans>You can also continue with Google instead.</Trans>
          </span>
        )}
        <Link href={statusUrl} className="text-sm">
          <Trans>See the email provider's status</Trans>
        </Link>
      </AlertDescription>
    </Alert>
  );
}
