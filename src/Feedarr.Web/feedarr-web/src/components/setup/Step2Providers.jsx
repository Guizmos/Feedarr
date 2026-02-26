import React, { useEffect } from "react";
import useExternalProviderInstances from "../../pages/settings/hooks/useExternalProviderInstances.js";
import ExternalProviderInstancesSection from "../../pages/settings/ExternalProviderInstancesSection.jsx";
import { tr } from "../../app/uiText.js";

const DEFAULT_VALIDATION = {};

export default function Step2Providers({ validation, onValidationChange, onAllProvidersAddedChange }) {
  const controller = useExternalProviderInstances();

  useEffect(() => {
    controller.loadExternalProviders();
    controller.loadProviderStats();
  }, [controller.loadExternalProviders, controller.loadProviderStats]);

  useEffect(() => {
    if (!onValidationChange) return;
    onValidationChange((prev) => ({
      ...(prev || DEFAULT_VALIDATION),
      ...controller.externalValidationByProvider,
    }));
  }, [controller.externalValidationByProvider, onValidationChange]);

  useEffect(() => {
    onAllProvidersAddedChange?.(controller.allProvidersAdded);
  }, [controller.allProvidersAdded, onAllProvidersAddedChange]);

  const hasConfiguredProvider = Object.values(validation || controller.externalValidationByProvider || {})
    .some((value) => value === "ok");

  return (
    <div className="setup-step setup-providers">
      <h2>{tr("Metadonnees", "Metadata")}</h2>
      <p>{tr("Ajoute au moins un provider metadata configure et actif.", "Add at least one configured and enabled metadata provider.")}</p>

      {!hasConfiguredProvider && (
        <div className="onboarding__error">
          {tr("Configure au moins un provider metadata pour continuer.", "Configure at least one metadata provider to continue.")}
        </div>
      )}

      <ExternalProviderInstancesSection controller={controller} showInlineAdd />
    </div>
  );
}
