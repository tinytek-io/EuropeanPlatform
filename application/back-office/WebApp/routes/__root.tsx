import { AuthenticationProvider } from "@repo/infrastructure/auth/AuthenticationProvider";
import { themeColor } from "@repo/infrastructure/branding";
import { useErrorTrigger } from "@repo/infrastructure/development/useErrorTrigger";
import { PageTracker } from "@repo/infrastructure/telemetry/PageTracker";
import { useInitializeLocale } from "@repo/infrastructure/translations/useInitializeLocale";
import { BannerContainer } from "@repo/ui/components/BannerContainer";
import { ThemeModeProvider } from "@repo/ui/theme/mode/ThemeMode";
import { QueryClientProvider } from "@tanstack/react-query";
import { createRootRoute, Outlet } from "@tanstack/react-router";

import { BackOfficeBanners } from "@/shared/components/BackOfficeBanners";
import { ErrorPage } from "@/shared/components/errorPages/ErrorPage";
import { NotFoundPage } from "@/shared/components/errorPages/NotFoundPage";
import { queryClient } from "@/shared/lib/api/client";

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
            <BackOfficeBanners />
          </BannerContainer>
          <PageTracker />
          <Outlet />
        </AuthenticationProvider>
      </ThemeModeProvider>
    </QueryClientProvider>
  );
}
