import "@repo/ui/theme.css";
import "@repo/ui/tailwind.css";
import { setupGlobalErrorHandlers } from "@repo/infrastructure/http/globalErrorHandlers";
import { TelemetryProvider } from "@repo/infrastructure/telemetry/TelemetryProvider";
import { RepoUiTranslation } from "@repo/infrastructure/translations/RepoUiTranslation";
import { Translation } from "@repo/infrastructure/translations/Translation";
import { Toaster } from "@repo/ui/components/Sonner";
import { RouterProvider } from "@tanstack/react-router";
import React from "react";
import reactDom from "react-dom/client";

import { router } from "@/shared/lib/router/router";

const { TranslationProvider } = await Translation.create(
  (locale) => import(`@/shared/translations/locale/${locale}.ts`)
);

const rootElement = document.getElementById("root");

if (!rootElement) {
  throw new Error("Root element not found.");
}

setupGlobalErrorHandlers();

reactDom.createRoot(rootElement).render(
  <React.StrictMode>
    <TranslationProvider>
      <RepoUiTranslation>
        <TelemetryProvider>
          <RouterProvider router={router} />
          <Toaster position="top-center" closeButton={true} style={{ zIndex: 60 }} />
        </TelemetryProvider>
      </RepoUiTranslation>
    </TranslationProvider>
  </React.StrictMode>
);
