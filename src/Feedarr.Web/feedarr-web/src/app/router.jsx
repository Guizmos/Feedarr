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

const rawBasename = import.meta.env.BASE_URL || "/";
const basename = rawBasename !== "/" && rawBasename.endsWith("/")
  ? rawBasename.slice(0, -1)
  : rawBasename;

export const router = createBrowserRouter([
  {
    path: "/setup",
    element: <SetupShell />,
    errorElement: <RouteErrorBoundary />,
    children: [
      { index: true, element: withSuspense(<SetupWizard />, "Assistant de configuration") },
    ],
  },
  {
    path: "/",
    element: <Shell />,
    errorElement: <RouteErrorBoundary />,
    children: [
      { index: true, element: <Navigate to="/library" replace /> },
      { path: "library", element: withSuspense(<Library />, "Bibliotheque") },
      { path: "library/top", element: withSuspense(<TopReleases />, "Top releases") },
      { path: "activity", element: withSuspense(<Activity />, "Activite") },
      { path: "history", element: withSuspense(<History />, "Historique") },
      { path: "indexers", element: withSuspense(<Indexers />, "Indexeurs") },
      { path: "system", element: withSuspense(<System />, "Systeme") },
      { path: "system/statistics", element: withSuspense(<SystemStatisticsPage />, "Statistiques") },
      { path: "system/indexers", element: withSuspense(<SystemIndexers />, "Indexeurs systeme") },
      { path: "system/storage", element: withSuspense(<System />, "Stockage") },
      { path: "system/providers", element: withSuspense(<System />, "Providers systeme") },
      { path: "system/updates", element: withSuspense(<System />, "Mises a jour") },
      { path: "settings", element: withSuspense(<Settings />, "Parametres") },
      { path: "settings/ui", element: withSuspense(<Settings />, "Interface") },
      { path: "settings/providers", element: withSuspense(<Providers />, "Providers") },
      { path: "settings/indexers", element: <Navigate to="/indexers" replace /> },
      { path: "settings/externals", element: withSuspense(<Settings />, "Providers externes") },
      { path: "settings/applications", element: withSuspense(<Settings />, "Applications") },
      { path: "settings/users", element: withSuspense(<Settings />, "Utilisateurs") },
      { path: "settings/maintenance", element: withSuspense(<Settings />, "Maintenance") },
      { path: "settings/backup", element: withSuspense(<Settings />, "Sauvegardes") },
      { path: "settings/about", element: <Navigate to="/system/updates" replace /> },
    ],
  },
], { basename });
