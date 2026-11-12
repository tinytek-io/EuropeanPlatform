import type { ReactNode } from "react";

import { t } from "@lingui/core/macro";
import { HeroImage } from "@repo/ui/components/HeroImage";

import { LoginFooterControls } from "@/routes/-components/LoginFooterControls";

interface HorizontalHeroLayoutProps {
  children: ReactNode;
}

export function HorizontalHeroLayout({ children }: Readonly<HorizontalHeroLayoutProps>) {
  return (
    <main className="relative flex min-h-screen flex-col">
      <div className="absolute top-4 right-4 hidden lg:block">
        <LoginFooterControls />
      </div>
      <div className="flex grow flex-col gap-4 lg:flex-row">
        <div className="flex min-h-screen w-full flex-col bg-background p-6 lg:min-h-0 lg:w-1/2">
          <div style={{ flex: 1 }} className="flex flex-col items-center justify-center gap-6">
            {children}
            <div className="lg:hidden">
              <LoginFooterControls />
            </div>
          </div>
        </div>
        <div className="hidden items-center justify-center bg-input-background p-6 lg:flex lg:w-1/2 lg:px-28 lg:py-12">
          <HeroImage alt={t`Screenshots of the dashboard project with desktop and mobile versions`} />
        </div>
      </div>
    </main>
  );
}
