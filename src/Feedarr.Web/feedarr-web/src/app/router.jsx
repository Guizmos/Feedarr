import React from "react";
import { createBrowserRouter, Navigate } from "react-router-dom";
import Shell from "../layout/Shell.jsx";
import SetupShell from "../layout/SetupShell.jsx";
import RouteErrorBoundary from "../ui/RouteErrorBoundary.jsx";
import ErrorBoundary from "../ui/ErrorBoundary.jsx";

const Library = React.lazy(() => import("../pages/Library.jsx"));
const TopReleases = React.lazy(() => import("../pages/TopReleases.jsx"));
const Activity = React.lazy(() => import("../pages/Activity.jsx"));
const History = React.lazy(() => import("../pages/History.jsx"));
const Indexers = React.lazy(() => import("../pages/Indexers.jsx"));
const System = React.lazy(() => import("../pages/System.jsx"));
const SystemStatisticsPage = React.lazy(() => import("../pages/system/SystemStatisticsPage.jsx"));
const SystemIndexers = React.lazy(() => import("../pages/system/SystemIndexers.jsx"));
const Settings = React.lazy(() => import("../pages/Settings.jsx"));
const Providers = React.lazy(() => import("../pages/Providers.jsx"));
const SetupWizard = React.lazy(() => import("../pages/SetupWizard.jsx"));

function withSuspense(element, label) {
  return (
    <ErrorBoundary label={label}>
      <React.Suspense
        fallback={
          <div className="loader">
            <div className="spinner" />
            <div className="muted">Chargement...</div>
          </div>
        }
      >
        {element}
      </React.Suspense>
    </ErrorBoundary>
  );
}

export const router = createBrowserRouter([
  {
    path: "/setup",
    element: <SetupShell />,
    errorElement: <RouteErrorBoundary />,
    children: [
      { index: true, element: withSuspense(<SetupWizard />) },
    ],
  },
  {
    path: "/",
    element: <Shell />,
    errorElement: <RouteErrorBoundary />,
    children: [
      { index: true, element: <Navigate to="/library" replace /> },
      { path: "library", element: withSuspense(<Library />) },
      { path: "library/top", element: withSuspense(<TopReleases />) },
      { path: "activity", element: withSuspense(<Activity />) },
      { path: "history", element: withSuspense(<History />) },
      { path: "indexers", element: withSuspense(<Indexers />) },
      { path: "system", element: withSuspense(<System />) },
      { path: "system/statistics", element: withSuspense(<SystemStatisticsPage />) },
      { path: "system/indexers", element: withSuspense(<SystemIndexers />) },
      { path: "system/storage", element: withSuspense(<System />) },
      { path: "system/providers", element: withSuspense(<System />) },
      { path: "settings", element: withSuspense(<Settings />) },
      { path: "settings/ui", element: withSuspense(<Settings />) },
      { path: "settings/providers", element: withSuspense(<Providers />) },
      { path: "settings/indexers", element: <Navigate to="/indexers" replace /> },
      { path: "settings/externals", element: withSuspense(<Settings />) },
      { path: "settings/applications", element: withSuspense(<Settings />) },
      { path: "settings/users", element: withSuspense(<Settings />) },
      { path: "settings/maintenance", element: withSuspense(<Settings />) },
      { path: "settings/backup", element: withSuspense(<Settings />) },
    ],
  },
]);
