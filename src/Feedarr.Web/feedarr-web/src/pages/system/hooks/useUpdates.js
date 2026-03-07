import { useBadgeStoreContext } from "../../../badges/useBadgeStore.js";

export default function useUpdates() {
  const badges = useBadgeStoreContext();
  return badges.updates;
}
