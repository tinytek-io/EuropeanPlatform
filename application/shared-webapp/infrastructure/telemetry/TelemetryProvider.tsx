import { Component, type ErrorInfo, type ReactNode, useEffect, useRef } from "react";

import { trackEvent } from "./telemetryClient";

const ErrorFallback = () => <h1>Something went wrong, please try again</h1>;

interface TelemetryProviderProps {
  children: ReactNode;
}

// Catches render errors in the tree below the provider, reports them as telemetry, and shows a
// fallback page instead of a blank screen.
class TelemetryErrorBoundary extends Component<TelemetryProviderProps, { hasError: boolean }> {
  state = { hasError: false };

  static getDerivedStateFromError() {
    return { hasError: true };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    trackException(error, { componentStack: errorInfo.componentStack ?? "" });
  }

  render() {
    return this.state.hasError ? <ErrorFallback /> : this.props.children;
  }
}

export function TelemetryProvider({ children }: Readonly<TelemetryProviderProps>) {
  return <TelemetryErrorBoundary>{children}</TelemetryErrorBoundary>;
}

// Reports a client-side exception with optional properties (e.g. the React component stack).
export function trackException(error: Error, properties?: Record<string, string>): void {
  trackEvent({
    type: "exception",
    name: error.name,
    url: globalThis.location.href,
    properties,
    error: {
      name: error.name,
      message: error.message,
      stack: error.stack,
      componentStack: properties?.componentStack
    }
  });
}

export type TrackingType = "page" | "menu" | "dialog" | "sidepane" | "interaction";
export type TrackingAction = "Open" | "Close" | "Submit" | "Cancel" | "Confirm";

export function trackInteraction(
  name: string,
  type: "page" | "interaction",
  action?: string,
  extraProperties?: Record<string, string>
): void;
export function trackInteraction(
  name: string,
  type: "menu" | "dialog" | "sidepane",
  action: TrackingAction,
  extraProperties?: Record<string, string>
): void;
export function trackInteraction(
  name: string,
  type: TrackingType,
  action?: string,
  extraProperties?: Record<string, string>
): void {
  const typeSuffix = type === "interaction" ? "" : ` ${type}`;
  const displayName = action ? `${name} - ${action}${typeSuffix}` : name;
  trackEvent({
    type: "interaction",
    name: displayName,
    url: window.location.href,
    properties: { type, ...extraProperties }
  });
}

// Register on window for cross-module-federation access.
// Components in @repo/ui cannot import from @repo/infrastructure due to rootDir boundaries,
// so they call this via the window global instead.
(window as unknown as { __trackInteraction: typeof trackInteraction }).__trackInteraction = trackInteraction;

export function useTrackOpen(name: string, type: "menu" | "dialog" | "sidepane", isOpen = true, key?: string) {
  const prevOpen = useRef(false);
  const prevKey = useRef(key);
  useEffect(() => {
    const opened = isOpen && !prevOpen.current;
    const contentChanged = isOpen && prevOpen.current && key !== undefined && key !== prevKey.current;
    if (opened || contentChanged) {
      trackInteraction(name, type, "Open");
    }
    prevOpen.current = isOpen;
    prevKey.current = key;
  }, [isOpen, name, type, key]);
}

export function useTrackClose(name: string, type: "menu" | "dialog" | "sidepane", isOpen = true) {
  const prevOpen = useRef(false);
  useEffect(() => {
    if (!isOpen && prevOpen.current) {
      trackInteraction(name, type, "Close");
    }
    prevOpen.current = isOpen;
  }, [isOpen, name, type]);
}
