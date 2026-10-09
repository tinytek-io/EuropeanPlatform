import { t } from "@lingui/core/macro";
import { Trans } from "@lingui/react/macro";
import { useUserInfo } from "@repo/infrastructure/auth/hooks";
import { productName } from "@repo/infrastructure/branding";
import { trackInteraction, useTrackOpen } from "@repo/infrastructure/telemetry/TelemetryProvider";
import { Button } from "@repo/ui/components/Button";
import {
  overlayContext,
  SidebarContent,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarMenu,
  SidebarMenuItem
} from "@repo/ui/components/Sidebar";
import { TenantLogo } from "@repo/ui/components/TenantLogo";
import { Tooltip, TooltipContent, TooltipTrigger } from "@repo/ui/components/Tooltip";
import { LayoutDashboardIcon, MessageCircleQuestion } from "lucide-react";
import { useContext, useEffect, useState } from "react";

import { useMainNavigation } from "@/shared/hooks/useMainNavigation";
import { withAccountTranslations } from "@/shared/translations/withAccountTranslations";

import { fetchTenants, type TenantInfo } from "../common/tenantUtils";
import { MobileMenuContent } from "./MobileMenuContent";
import { MobileMenuDialogs } from "./MobileMenuDialogs";

// Rich mobile navigation surface rendered inside the Sidebar's mobile Sheet. Shows tenant info,
// user actions, tenant switcher, navigation links, and a support button. Federated so both the
// Main and Account apps can reuse it inside their mobile sidebars.
function MobileMenu() {
  const userInfo = useUserInfo();
  const overlayCtx = useContext(overlayContext);
  const { navigateToMain } = useMainNavigation();
  const [tenants, setTenants] = useState<TenantInfo[]>([]);

  useTrackOpen("Mobile menu", "menu");

  useEffect(() => {
    if (userInfo?.isAuthenticated) {
      fetchTenants()
        .then((response) => setTenants(response.tenants || []))
        .catch(() => setTenants([]));
    }
  }, [userInfo?.isAuthenticated]);

  const closeMenu = () => {
    if (overlayCtx?.isOpen) {
      overlayCtx.close();
    }
  };

  const handleOpenSupportDialog = () => {
    window.dispatchEvent(new CustomEvent("open-support-dialog"));
    setTimeout(closeMenu, 100);
  };

  const handleOpenTenantSwitcher = () => {
    trackInteraction("Switch account", "menu", "Open");
    window.dispatchEvent(new CustomEvent("open-tenant-switcher", { detail: { tenants } }));
    setTimeout(closeMenu, 100);
  };

  const currentTenant = tenants.find((tenant) => tenant.tenantId === userInfo?.tenantId);
  const currentTenantName = currentTenant?.tenantName || userInfo?.tenantName || productName;
  const currentTenantNameForLogo = currentTenant?.tenantName || userInfo?.tenantName || "";
  const currentTenantLogoUrl = currentTenant ? currentTenant.logoUrl : userInfo?.tenantLogoUrl;

  return (
    <div className="flex h-full flex-col">
      {userInfo?.isAuthenticated && (
        <div className="mb-2 flex items-center justify-center gap-3 bg-muted px-3 py-2.5 dark:bg-transparent">
          <TenantLogo logoUrl={currentTenantLogoUrl} tenantName={currentTenantNameForLogo} size="sm" />
          <h5 className="min-w-0 overflow-hidden font-normal text-ellipsis whitespace-nowrap">{currentTenantName}</h5>
        </div>
      )}
      <SidebarContent>
        <SidebarGroup>
          <SidebarGroupContent>
            {/* mx-1 mirrors SidebarMenuItem so user/account buttons share the 12px offset used by
                Dashboard below (SidebarGroup p-2 + mx-1 = 12px). */}
            <div className="mx-1">
              <MobileMenuContent tenants={tenants} onOpenTenantSwitcher={handleOpenTenantSwitcher} />
            </div>
          </SidebarGroupContent>
        </SidebarGroup>

        <SidebarGroup>
          <SidebarGroupLabel>
            <Trans>Navigation</Trans>
          </SidebarGroupLabel>
          <SidebarGroupContent>
            <SidebarMenu>
              <SidebarMenuItem>
                {/* Uses a plain <Button> rather than <SidebarMenuButton> because @repo/ui isn't shared
                    across module federation, so the federated MobileMenu can't reach the host's
                    SidebarProvider context. */}
                <Button
                  variant="ghost"
                  onClick={() => {
                    navigateToMain("/dashboard");
                    closeMenu();
                  }}
                  className="flex h-[var(--control-height)] w-full items-center justify-start gap-4 rounded-md pr-3 pl-[1.125rem] text-left text-sm font-normal text-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-accent-foreground active:bg-sidebar-accent active:text-sidebar-accent-foreground"
                  aria-label={t`Dashboard`}
                >
                  <LayoutDashboardIcon className="size-5 shrink-0" />
                  <Trans>Dashboard</Trans>
                </Button>
              </SidebarMenuItem>
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>
      </SidebarContent>

      <div className="absolute bottom-3 left-3 z-10 supports-[bottom:max(0px)]:bottom-[max(0.5rem,calc(env(safe-area-inset-bottom)-0.5rem))]">
        <Tooltip>
          <TooltipTrigger
            render={
              <Button
                variant="ghost"
                size="icon"
                aria-label={t`Contact support`}
                className="size-14 rounded-full border border-border bg-background/80 shadow-lg backdrop-blur-sm hover:bg-background/90 active:bg-muted"
                onClick={handleOpenSupportDialog}
              />
            }
          >
            <MessageCircleQuestion className="size-7 text-foreground" />
          </TooltipTrigger>
          <TooltipContent side="right">
            <Trans>Contact support</Trans>
          </TooltipContent>
        </Tooltip>
      </div>
      {/* Mounts the SupportDialog + TenantSwitcherDrawer event listeners. On desktop these mount
          inside UserMenu's sidebar header, which the mobile overlay omits, so we mount them here too. */}
      <MobileMenuDialogs />
    </div>
  );
}

export default withAccountTranslations(MobileMenu);
