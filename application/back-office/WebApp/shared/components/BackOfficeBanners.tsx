import { BillingDriftBanner } from "./BillingDriftBanner";
import { MrrMismatchBanner } from "./MrrMismatchBanner";
import { UnsyncedAccountsBanner } from "./UnsyncedAccountsBanner";

// Rendered as a child of BannerContainer, which owns the fixed banner area.
// Banners are ordinary React children of that container so the container has exactly one writer.
export function BackOfficeBanners() {
  return (
    <>
      <UnsyncedAccountsBanner />
      <MrrMismatchBanner />
      <BillingDriftBanner />
    </>
  );
}
