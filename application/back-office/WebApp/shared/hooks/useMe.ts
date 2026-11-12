import { api } from "@/shared/lib/api/client";

export function useMe() {
  return api.useQuery("get", "/api/back-office/me");
}
