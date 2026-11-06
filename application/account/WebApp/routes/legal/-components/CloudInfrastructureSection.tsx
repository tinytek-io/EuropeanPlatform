import { t } from "@lingui/core/macro";
import { Trans } from "@lingui/react/macro";
import { Card, CardDescription, CardTitle } from "@repo/ui/components/Card";
import { Link } from "@repo/ui/components/Link";
import platformPlatformLogoMark from "@repo/ui/images/platformplatform-logo-mark-192.png";
import {
  DatabaseIcon,
  ExternalLinkIcon,
  FingerprintIcon,
  Globe2Icon,
  KeyIcon,
  LayersIcon,
  ServerIcon,
  ShieldCheckIcon
} from "lucide-react";

import gdprBadge from "@/shared/images/compliance/gdpr.png";

export function CloudInfrastructureSection() {
  return (
    <section className="bg-input-background px-4 py-16">
      <div className="mx-auto max-w-[64rem]">
        <div className="mb-12 text-center">
          <h2 className="marketing">
            <Trans>Enterprise-grade European cloud infrastructure</Trans>
          </h2>
          <p className="text-muted-foreground">
            <Trans>Managed Kubernetes and managed PostgreSQL on European cloud infrastructure.</Trans>
          </p>
        </div>

        <ComplianceBadges />
        <SecurityFeaturesGrid />
        <PlatformPlatformCredit />
      </div>
    </section>
  );
}

function ComplianceBadges() {
  return (
    <div className="mb-12 flex flex-wrap items-center justify-center gap-6 sm:gap-8 md:gap-12">
      <div className="flex flex-col items-center gap-3">
        <div className="flex size-24 items-center justify-center">
          <img src={gdprBadge} alt={t`GDPR compliant`} className="h-20 w-auto object-contain" />
        </div>
        <span className="font-semibold text-foreground">
          <Trans>GDPR Compliant</Trans>
        </span>
      </div>
      <div className="flex flex-col items-center gap-3">
        <div className="flex size-24 items-center justify-center rounded-full bg-primary/10">
          <Globe2Icon className="size-14 text-primary" aria-label={t`EU data residency`} />
        </div>
        <span className="font-semibold text-foreground">
          <Trans>EU Data Residency</Trans>
        </span>
      </div>
    </div>
  );
}

function SecurityFeaturesGrid() {
  return (
    <div className="mb-12 grid gap-6 md:grid-cols-2 lg:grid-cols-3">
      <Card className="px-6 transition-colors hover:bg-hover-background">
        <div className="mb-3 flex size-10 items-center justify-center rounded-lg bg-primary/10">
          <ServerIcon className="size-6 text-primary" />
        </div>
        <CardTitle>
          <Trans>Fully managed infrastructure</Trans>
        </CardTitle>
        <CardDescription>
          <Trans>The managed platform patches and secures the underlying infrastructure automatically</Trans>
        </CardDescription>
      </Card>
      <Card className="px-6 transition-colors hover:bg-hover-background">
        <div className="mb-3 flex size-10 items-center justify-center rounded-lg bg-primary/10">
          <FingerprintIcon className="size-6 text-primary" />
        </div>
        <CardTitle>
          <Trans>Least-privilege access</Trans>
        </CardTitle>
        <CardDescription>
          <Trans>Every service runs with its own scoped credentials, and only trusted personnel can access data</Trans>
        </CardDescription>
      </Card>
      <Card className="px-6 transition-colors hover:bg-hover-background">
        <div className="mb-3 flex size-10 items-center justify-center rounded-lg bg-primary/10">
          <LayersIcon className="size-6 text-primary" />
        </div>
        <CardTitle>
          <Trans>Environment isolation</Trans>
        </CardTitle>
        <CardDescription>
          <Trans>Strict separation prevents production data from leaking to other environments</Trans>
        </CardDescription>
      </Card>
      <Card className="px-6 transition-colors hover:bg-hover-background">
        <div className="mb-3 flex size-10 items-center justify-center rounded-lg bg-primary/10">
          <KeyIcon className="size-6 text-primary" />
        </div>
        <CardTitle>
          <Trans>Scripted deployments</Trans>
        </CardTitle>
        <CardDescription>
          <Trans>
            Deployments run through a scripted pipeline with scoped, revocable credentials — no passwords in the
            codebase
          </Trans>
        </CardDescription>
      </Card>
      <Card className="px-6 transition-colors hover:bg-hover-background">
        <div className="mb-3 flex size-10 items-center justify-center rounded-lg bg-primary/10">
          <DatabaseIcon className="size-6 text-primary" />
        </div>
        <CardTitle>
          <Trans>Data residency</Trans>
        </CardTitle>
        <CardDescription>
          <Trans>Your data stays in the European region your environment runs in</Trans>
        </CardDescription>
      </Card>
      <Card className="px-6 transition-colors hover:bg-hover-background">
        <div className="mb-3 flex size-10 items-center justify-center rounded-lg bg-primary/10">
          <ShieldCheckIcon className="size-6 text-primary" />
        </div>
        <CardTitle>
          <Trans>Web application firewall</Trans>
        </CardTitle>
        <CardDescription>
          <Trans>All traffic passes through a managed web application firewall, with TLS everywhere</Trans>
        </CardDescription>
      </Card>
    </div>
  );
}

function PlatformPlatformCredit() {
  return (
    <div className="flex items-center justify-center gap-3">
      <img src={platformPlatformLogoMark} alt={t`PlatformPlatform logo`} className="size-10" loading="lazy" />
      <p className="text-sm text-muted-foreground">
        <Trans>
          Built on{" "}
          <Link
            href="https://github.com/platformplatform/PlatformPlatform"
            target="_blank"
            rel="noopener noreferrer"
            aria-label={t`PlatformPlatform on GitHub (opens in new window)`}
            className="inline-flex items-center gap-1 font-medium text-primary"
          >
            PlatformPlatform
            <ExternalLinkIcon className="size-3" />
          </Link>{" "}
          - an open-source platform by industry experts showcasing how to build enterprise-grade B2B SaaS products
        </Trans>
      </p>
    </div>
  );
}
