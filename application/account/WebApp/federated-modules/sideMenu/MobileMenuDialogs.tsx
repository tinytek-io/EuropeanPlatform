import { t } from "@lingui/core/macro";
import { authSyncService, type TenantSwitchedMessage } from "@repo/infrastructure/auth/AuthSyncService";
import { loggedInPath } from "@repo/infrastructure/auth/constants";
import { useUserInfo } from "@repo/infrastructure/auth/hooks";
import { trackInteraction } from "@repo/infrastructure/telemetry/TelemetryProvider";
import { useEffect, useState } from "react";

import { SupportDialog } from "../common/SupportDialog";
import { SwitchingAccountLoader } from "../common/SwitchingAccountLoader";
import { switchTenantApi, type TenantInfo } from "../common/tenantUtils";
import { TenantSwitcherDrawer } from "./TenantSwitcherDrawer";

// Mounts the SupportDialog + TenantSwitcherDrawer and their global event listeners. Rendered by both the
// mobile menu (MobileMenu) and, on desktop, the sidebar header (UserMenu) — the two surfaces where a user
// can open the support dialog or switch tenants.
export function MobileMenuDialogs() {
  const userInfo = useUserInfo();
  const [isTenantSwitcherOpen, setIsTenantSwitcherOpen] = useState(false);
  const [isSwitching, setIsSwitching] = useState(false);
  const [isSupportDialogOpen, setIsSupportDialogOpen] = useState(false);
  const [tenants, setTenants] = useState<TenantInfo[]>([]);

  const currentTenantId = userInfo?.tenantId;

  useEffect(() => {
    const handleOpenSupport = () => setIsSupportDialogOpen(true);
    const handleOpenTenantSwitcher = (event: CustomEvent<{ tenants: TenantInfo[] }>) => {
      setTenants(event.detail.tenants);
      setIsTenantSwitcherOpen(true);
    };

    window.addEventListener("open-support-dialog", handleOpenSupport);
    window.addEventListener("open-tenant-switcher", handleOpenTenantSwitcher as EventListener);
    return () => {
      window.removeEventListener("open-support-dialog", handleOpenSupport);
      window.removeEventListener("open-tenant-switcher", handleOpenTenantSwitcher as EventListener);
    };
  }, []);

  const handleTenantSwitch = async (tenant: TenantInfo) => {
    if (tenant.tenantId === currentTenantId || tenant.isNew) {
      return;
    }

    trackInteraction("Switch account", "interaction");
    setIsSwitching(true);
    setIsTenantSwitcherOpen(false);

    try {
      localStorage.setItem("preferred-tenant", tenant.tenantId);
      if (tenant.tenantName) {
        localStorage.setItem(`tenant-name-${tenant.tenantId}`, tenant.tenantName);
      }

      await switchTenantApi(tenant.tenantId);

      if (userInfo?.tenantId && userInfo?.id) {
        const message: Omit<TenantSwitchedMessage, "timestamp"> = {
          type: "TENANT_SWITCHED",
          newTenantId: tenant.tenantId,
          previousTenantId: userInfo.tenantId,
          tenantName: tenant.tenantName || t`Unnamed account`,
          userId: userInfo.id
        };
        authSyncService.broadcast(message);
      }

      if (window.location.pathname === "/") {
        window.location.href = loggedInPath;
      } else {
        window.location.reload();
      }
    } catch {
      setIsSwitching(false);
    }
  };

  return (
    <>
      <TenantSwitcherDrawer
        isOpen={isTenantSwitcherOpen}
        onOpenChange={setIsTenantSwitcherOpen}
        tenants={tenants}
        currentTenantId={currentTenantId}
        onTenantSwitch={handleTenantSwitch}
      />
      {isSwitching && <SwitchingAccountLoader />}
      <SupportDialog isOpen={isSupportDialogOpen} onOpenChange={setIsSupportDialogOpen} />
    </>
  );
}
