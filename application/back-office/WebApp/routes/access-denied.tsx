import { createFileRoute } from "@tanstack/react-router";

import { AccessDeniedPage } from "@/shared/components/errorPages/AccessDeniedPage";

export const Route = createFileRoute("/access-denied")({
  staticData: { trackingTitle: "Access denied" },
  component: AccessDeniedPage
});
