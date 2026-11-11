import { t } from "@lingui/core/macro";
import { HeroImage as ThemedHeroImage } from "@repo/ui/components/HeroImage";

export function HeroImage() {
  return <ThemedHeroImage alt={t`Screenshots of the dashboard project with desktop and mobile versions`} />;
}
