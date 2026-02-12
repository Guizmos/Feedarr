import React, { useEffect, useState } from "react";
import { useSubbarSetter } from "../../layout/useSubbar.js";
import SubAction from "../../ui/SubAction.jsx";
import SystemStatistics from "./SystemStatistics.jsx";

export default function SystemStatisticsPage() {
  const setContent = useSubbarSetter();
  const [refreshTick, setRefreshTick] = useState(0);

  useEffect(() => {
    const refresh = () => setRefreshTick((t) => t + 1);
    setContent(<SubAction icon="refresh" label="Rafraîchir" onClick={refresh} />);
    return () => setContent(null);
  }, [setContent]);

  return (
    <div className="page page--system">
      <div className="pagehead">
        <div>
          <h1>Statistiques</h1>
          <div className="muted">Configuration de l'application</div>
        </div>
      </div>
      <div className="pagehead__divider" />
      <SystemStatistics refreshKey={refreshTick} />
    </div>
  );
}
