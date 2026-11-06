import { AuthenticationProvider } from "@repo/infrastructure/auth/AuthenticationProvider";
import { AuthSyncModal } from "@repo/infrastructure/auth/AuthSyncModal";
import { productName, showAddToHomescreen, themeColor } from "@repo/infrastructure/branding";
import { useErrorTrigger } from "@repo/infrastructure/development/useErrorTrigger";
import { OnboardingGuard } from "@repo/infrastructure/onboarding/OnboardingGuard";
import { PageTracker } from "@repo/infrastructure/telemetry/PageTracker";
import { useInitializeLocale } from "@repo/infrastructure/translations/useInitializeLocale";
import { AddToHomescreen } from "@repo/ui/components/AddToHomescreen";
import { BannerContainer } from "@repo/ui/components/BannerContainer";
import { ThemeModeProvider } from "@repo/ui/theme/mode/ThemeMode";
import { QueryClientProvider } from "@tanstack/react-query";
import { createRootRoute, Outlet } from "@tanstack/react-router";
import { lazy } from "react";

import { queryClient } from "@/shared/lib/api/client";

const FederatedAuthSyncModal = lazy(() => import("account/AuthSyncModal"));
const FederatedBanners = lazy(() => import("account/Banners"));
const ErrorPage = lazy(() => import("account/ErrorPage"));
const NotFoundPage = lazy(() => import("account/NotFoundPage"));

export const Route = createRootRoute({
  component: Root,
  errorComponent: ErrorPage,
  notFoundComponent: NotFoundPage
});

function Root() {
  useInitializeLocale();
  useErrorTrigger();

  return (
    <QueryClientProvider client={queryClient}>
      <ThemeModeProvider themeColor={themeColor}>
        <AuthenticationProvider>
          <BannerContainer>
            <FederatedBanners />
          </BannerContainer>
          {showAddToHomescreen && <AddToHomescreen productName={productName} />}
          <PageTracker />
          <Outlet />
          <AuthSyncModal modalComponent={FederatedAuthSyncModal} />
          <OnboardingGuard />
        </AuthenticationProvider>
      </ThemeModeProvider>
    </QueryClientProvider>
  );
}
