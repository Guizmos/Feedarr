import { useBadgeStoreContext } from "../badges/useBadgeStore.js";

export {
  computeReleasesBadgeValue,
  createBadgeSseRefreshScheduler,
  runSummaryRefreshWithFallback,
  hydrateSeenState,
  persistSeenEntry,
  mergeSeenEntry,
  mergeSeenState,
  computeBadgeSnapshot,
  normalizePath,
} from "../badges/useBadgeStore.js";

export default function useBadges() {
  return useBadgeStoreContext();
}
