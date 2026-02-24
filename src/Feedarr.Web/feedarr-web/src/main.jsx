import React from "react";
import ReactDOM from "react-dom/client";
import { RouterProvider } from "react-router-dom";
import { router } from "./app/router.jsx";
import { applyUiLanguage, getStoredUiLanguage } from "./app/locale.js";
import { initRuntimeTranslation } from "./app/runtimeTranslation.js";
import "./styles/tokens.css";
import "./styles/styles.css";

applyUiLanguage(getStoredUiLanguage());
initRuntimeTranslation();

ReactDOM.createRoot(document.getElementById("root")).render(
  <React.StrictMode>
    <RouterProvider router={router} />
  </React.StrictMode>
);

if (import.meta.env.PROD && "serviceWorker" in navigator) {
  const baseUrl = import.meta.env.BASE_URL || "/";
  const normalizedBase = baseUrl.endsWith("/") ? baseUrl : `${baseUrl}/`;
  const swUrl = `${normalizedBase}service-worker.js`;

  window.addEventListener("load", () => {
    navigator.serviceWorker
      .register(swUrl, { scope: normalizedBase })
      .catch((error) => {
        console.error("Failed to register service worker", error);
      });
  });
}
