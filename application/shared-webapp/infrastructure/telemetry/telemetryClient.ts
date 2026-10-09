// The web application's own telemetry client: a tiny batched fetch sender to /api/track, which
// the APIs turn into structured logs (flowing through OpenTelemetry to the dashboard/Cockpit).
// Telemetry must never break the application: every failure is swallowed.

export type TrackEventType = "pageview" | "pageviewPerformance" | "interaction" | "exception";

export interface TrackEvent {
  type: TrackEventType;
  name: string;
  url?: string;
  properties?: Record<string, string>;
  measurements?: Record<string, number>;
  error?: {
    name: string;
    message: string;
    stack?: string;
    componentStack?: string;
  };
}

// Batch a few events per request and cap the queue so a runaway loop cannot grow it unbounded.
const FLUSH_INTERVAL_MILLISECONDS = 1_000;
const MAX_QUEUED_EVENTS = 100;
const EVENTS_PER_REQUEST = 25;

const queue: TrackEvent[] = [];
let flushTimer: ReturnType<typeof setTimeout> | undefined;

export function trackEvent(event: TrackEvent): void {
  if (queue.length >= MAX_QUEUED_EVENTS) {
    queue.shift();
  }
  queue.push(event);

  if (flushTimer === undefined) {
    flushTimer = setTimeout(flush, FLUSH_INTERVAL_MILLISECONDS);
  }
}

function flush(): void {
  flushTimer = undefined;

  // Chunk the queue: keepalive requests have a small body limit, and smaller payloads are
  // friendlier to the WAF inspecting the endpoint.
  while (queue.length > 0) {
    const events = queue.splice(0, EVENTS_PER_REQUEST);

    void fetch("/api/track", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(events),
      keepalive: true
    }).catch(() => {
      // Telemetry failures are deliberately ignored.
    });
  }
}

// Send whatever is queued before the page goes away; keepalive lets this request outlive it.
window.addEventListener("pagehide", flush);
