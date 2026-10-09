import { t } from "@lingui/core/macro";
import { Trans } from "@lingui/react/macro";
import { Card, CardDescription, CardTitle } from "@repo/ui/components/Card";
import { Link } from "@repo/ui/components/Link";
import { FileTextIcon, Globe2Icon, LandmarkIcon, ExternalLinkIcon } from "lucide-react";

export function ComplianceResourcesSection() {
  return (
    <section className="bg-background px-4 py-16">
      <div className="mx-auto max-w-[64rem]">
        <div className="mb-12 text-center">
          <h2 className="marketing">
            <Trans>Compliance resources</Trans>
          </h2>
          <p className="text-muted-foreground">
            <Trans>For detailed compliance documents and regulations, follow the official resources below.</Trans>
          </p>
        </div>
        <div className="grid gap-4 md:grid-cols-2">
          <Link
            href="https://www.scaleway.com/en/contracts/"
            target="_blank"
            rel="noopener noreferrer"
            aria-label={t`Cloud contracts and DPA (opens in new window)`}
            underline={false}
            className="block min-w-0 rounded-xl whitespace-normal outline-ring focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2"
          >
            <Card className="h-full gap-3 px-6 py-5 transition-colors hover:bg-hover-background">
              <FileTextIcon className="size-10" />
              <CardTitle className="flex items-center gap-2">
                <Trans>Cloud contracts and DPA</Trans>
                <ExternalLinkIcon className="size-4 text-muted-foreground" />
              </CardTitle>
              <CardDescription>
                <Trans>General terms, Data Processing Agreement, and technical and organizational measures</Trans>
              </CardDescription>
            </Card>
          </Link>
          <Link
            href="https://commission.europa.eu/law/law-topic/data-protection_en"
            target="_blank"
            rel="noopener noreferrer"
            aria-label={t`EU data protection (opens in new window)`}
            underline={false}
            className="block min-w-0 rounded-xl whitespace-normal outline-ring focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2"
          >
            <Card className="h-full gap-3 px-6 py-5 transition-colors hover:bg-hover-background">
              <LandmarkIcon className="size-10" />
              <CardTitle className="flex items-center gap-2">
                <Trans>EU data protection</Trans>
                <ExternalLinkIcon className="size-4 text-muted-foreground" />
              </CardTitle>
              <CardDescription>
                <Trans>The European Commission's overview of GDPR rules and how they protect your data</Trans>
              </CardDescription>
            </Card>
          </Link>
          <Link
            href="https://eur-lex.europa.eu/eli/reg/2016/679/oj"
            target="_blank"
            rel="noopener noreferrer"
            aria-label={t`GDPR regulation text (opens in new window)`}
            underline={false}
            className="block min-w-0 rounded-xl whitespace-normal outline-ring focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2"
          >
            <Card className="h-full gap-3 px-6 py-5 transition-colors hover:bg-hover-background">
              <Globe2Icon className="size-10" />
              <CardTitle className="flex items-center gap-2">
                <Trans>GDPR regulation text</Trans>
                <ExternalLinkIcon className="size-4 text-muted-foreground" />
              </CardTitle>
              <CardDescription>
                <Trans>The official General Data Protection Regulation as published by the European Union</Trans>
              </CardDescription>
            </Card>
          </Link>
        </div>
      </div>
    </section>
  );
}
