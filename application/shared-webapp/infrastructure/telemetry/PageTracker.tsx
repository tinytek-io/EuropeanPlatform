import { useRouter } from "@tanstack/react-router";
import { useEffect, useRef } from "react";

import { trackEvent } from "./telemetryClient";

interface PageStaticData {
  trackingTitle?: string;
}

function getStaticData(matches: { staticData: unknown }[]): PageStaticData | undefined {
  for (let i = matches.length - 1; i >= 0; i--) {
    const data = matches[i].staticData as PageStaticData;
    if (data.trackingTitle) {
      return data;
    }
  }
  return undefined;
}

// One page-load performance report per navigation, from the browser's Navigation Timing entry.
// Navigation Timing describes the initial document load only; client-side route changes have no
// navigation entry, so the timings are sent once, under the first page's name.
let documentLoadTracked = false;

function trackPagePerformance(name: string, uri: string) {
  if (documentLoadTracked) {
    return;
  }
  documentLoadTracked = true;

  // The navigation entry exists before the document finishes loading, but domComplete stays zero
  // until then, so the snapshot waits for the load event.
  if (document.readyState === "complete") {
    sendPagePerformance(name, uri);
  } else {
    window.addEventListener("load", () => sendPagePerformance(name, uri), { once: true });
  }
}

function sendPagePerformance(name: string, uri: string) {
  const entry = performance.getEntriesByType("navigation")[0] as PerformanceNavigationTiming | undefined;
  if (!entry) {
    return;
  }

  trackEvent({
    type: "pageviewPerformance",
    name,
    url: uri,
    measurements: {
      networkConnect: entry.connectEnd - entry.connectStart,
      sentRequest: entry.responseStart - entry.requestStart,
      receivedResponse: entry.responseEnd - entry.responseStart,
      domProcessing: entry.domComplete - entry.domInteractive,
      perfTotal: entry.domComplete - entry.startTime
    }
  });
}

export function PageTracker() {
  const router = useRouter();
  const lastTrackedPathname = useRef<string>("");

  useEffect(() => {
    function trackPage(pathname: string, uri: string) {
      if (pathname === lastTrackedPathname.current) {
        return;
      }
      lastTrackedPathname.current = pathname;

      const data = getStaticData(router.state.matches);
      if (!data?.trackingTitle) {
        return;
      }

      trackEvent({ type: "pageview", name: data.trackingTitle, url: uri, properties: { type: "page" } });
      trackPagePerformance(data.trackingTitle, uri);
    }

    trackPage(router.state.location.pathname, window.location.href);

    const unsubscribe = router.subscribe("onLoad", ({ toLocation }) => {
      trackPage(toLocation.pathname, toLocation.href);
    });

    return unsubscribe;
  }, [router]);

  return null;
}
