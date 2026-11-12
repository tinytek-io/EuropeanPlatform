// The web application's own telemetry client: a tiny batched fetch sender to /api/track, which
// the APIs turn into OpenTelemetry events (log records with an event.name attribute) flowing to
// the dashboard/Cockpit. Telemetry must never break the application: every failure is swallowed.

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

// Cap the queue so a runaway loop cannot grow it unbounded.
const FLUSH_INTERVAL_MILLISECONDS = 1_000;
const MAX_QUEUED_EVENTS = 100;
// The API processes at most 25 events per request.
const MAX_EVENTS_PER_REQUEST = 25;
// Batch by serialized size too: request bodies stay far below the API's 256 KiB limit, and smaller
// payloads are friendlier to the WAF inspecting the endpoint.
const MAX_REQUEST_BYTES = 32 * 1024;
// Browsers reject a keepalive request once the bodies of all in-flight keepalive requests would
// exceed 64 KiB, so keepalive is reserved for the page-hide flush and budgeted against that quota.
const KEEPALIVE_QUOTA_BYTES = 64 * 1024;
// The JSON array's enclosing brackets.
const ARRAY_OVERHEAD_BYTES = 2;

interface TrackRequestBatch {
  body: string;
  bytes: number;
}

const queue: TrackEvent[] = [];
const textEncoder = new TextEncoder();
let flushTimer: ReturnType<typeof setTimeout> | undefined;
let inFlightKeepaliveBytes = 0;

export function trackEvent(event: TrackEvent): void {
  if (queue.length >= MAX_QUEUED_EVENTS) {
    queue.shift();
  }
  queue.push(event);

  if (flushTimer === undefined) {
    flushTimer = setTimeout(flush, FLUSH_INTERVAL_MILLISECONDS);
  }
}

// Periodic flushes use ordinary requests, which have no in-flight body quota.
function flush(): void {
  flushTimer = undefined;
  for (const batch of takeQueuedBatches()) {
    send(batch, false);
  }
}

// Send whatever is queued before the page goes away. Keepalive lets a request outlive the page,
// but only within the shared quota; a batch that does not fit is sent as an ordinary request.
function flushOnPageHide(): void {
  clearTimeout(flushTimer);
  flushTimer = undefined;
  for (const batch of takeQueuedBatches()) {
    send(batch, inFlightKeepaliveBytes + batch.bytes <= KEEPALIVE_QUOTA_BYTES);
  }
}

// Empties the queue into batches of at most MAX_EVENTS_PER_REQUEST events and MAX_REQUEST_BYTES of
// UTF-8 body each; an event larger than that on its own travels alone.
function takeQueuedBatches(): TrackRequestBatch[] {
  const batches: TrackRequestBatch[] = [];
  let serializedEvents: string[] = [];
  let batchBytes = ARRAY_OVERHEAD_BYTES;

  for (const event of queue.splice(0)) {
    const serializedEvent = JSON.stringify(event);
    // Plus one byte for the separating comma (one too many per batch, which keeps the count safe).
    const eventBytes = textEncoder.encode(serializedEvent).length + 1;
    const batchIsFull =
      serializedEvents.length >= MAX_EVENTS_PER_REQUEST || batchBytes + eventBytes > MAX_REQUEST_BYTES;
    if (serializedEvents.length > 0 && batchIsFull) {
      batches.push({ body: `[${serializedEvents.join(",")}]`, bytes: batchBytes });
      serializedEvents = [];
      batchBytes = ARRAY_OVERHEAD_BYTES;
    }
    serializedEvents.push(serializedEvent);
    batchBytes += eventBytes;
  }

  if (serializedEvents.length > 0) {
    batches.push({ body: `[${serializedEvents.join(",")}]`, bytes: batchBytes });
  }
  return batches;
}

function send(batch: TrackRequestBatch, keepalive: boolean): void {
  if (keepalive) {
    inFlightKeepaliveBytes += batch.bytes;
  }

  void fetch("/api/track", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: batch.body,
    keepalive
  })
    .catch(() => {
      // Telemetry failures are deliberately ignored.
    })
    .finally(() => {
      if (keepalive) {
        inFlightKeepaliveBytes -= batch.bytes;
      }
    });
}

window.addEventListener("pagehide", flushOnPageHide);
