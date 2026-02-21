import React, { useMemo, useState } from "react";
import Modal from "../../ui/Modal.jsx";
import ItemRow from "../../ui/ItemRow.jsx";
import ToggleSwitch from "../../ui/ToggleSwitch.jsx";
import { tr } from "../../app/uiText.js";

function toHasFlagName(fieldKey) {
  if (!fieldKey) return "";
  const trimmed = String(fieldKey).trim();
  if (!trimmed) return "";
  return `has${trimmed.charAt(0).toUpperCase()}${trimmed.slice(1)}`;
}

export default function ExternalProviderInstancesSection({
  controller,
  showInlineAdd = false,
}) {
  const [inlineAddKey, setInlineAddKey] = useState("");
  const {
    definitions,
    availableExternalDefinitions,
    instances,
    externalLoading,
    externalError,
    providerStats,
    testingExternal,
    testStatusByExternal,
    isInstanceConfigured,
    testExternal,
    openExternalModalAdd,
    openExternalModalEdit,
    openExternalDelete,
    openExternalToggle,
    externalModalOpen,
    externalModalMode,
    externalModalStep,
    externalModalProviderKey,
    selectExternalModalProvider,
    externalModalDefinition,
    externalModalInstance,
    externalModalDisplayName,
    setExternalModalDisplayName,
    externalModalEnabled,
    setExternalModalEnabled,
    externalModalBaseUrl,
    setExternalModalBaseUrl,
    externalModalAuth,
    setExternalModalAuthField,
    externalModalSaving,
    externalModalError,
    canSaveExternalModal,
    closeExternalModal,
    goExternalModalStep,
    saveExternalModal,
    externalDeleteOpen,
    externalDeleteInstance,
    externalDeleteLoading,
    closeExternalDelete,
    confirmExternalDelete,
    externalToggleOpen,
    externalToggleInstance,
    closeExternalToggle,
    confirmExternalToggle,
  } = controller;

  const addableDefinitions = availableExternalDefinitions || [];
  const hasAddableDefinitions = addableDefinitions.length > 0;

  const definitionByProviderKey = useMemo(() => {
    const map = new Map();
    (definitions || []).forEach((definition) => {
      map.set(String(definition.providerKey || "").toLowerCase(), definition);
    });
    return map;
  }, [definitions]);

  function handleInlineAddChange(e) {
    const key = e.target.value;
    setInlineAddKey(key);
    if (!key) return;
    openExternalModalAdd(key);
    setInlineAddKey("");
  }

  return (
    <>
      {showInlineAdd && (
        <div className="setup-providers__add settings-row settings-row--ui-select">
          <label>{tr("Provider", "Provider")}</label>
          <select
            className="settings-field"
            value={inlineAddKey}
            onChange={handleInlineAddChange}
            disabled={!hasAddableDefinitions}
          >
            <option value="" disabled>
              {hasAddableDefinitions
                ? tr("Selectionner...", "Select...")
                : tr("Tous les providers sont deja ajoutes", "All providers are already added")}
            </option>
            {addableDefinitions.map((definition) => (
              <option key={definition.providerKey} value={definition.providerKey}>
                {definition.displayName}
              </option>
            ))}
          </select>
        </div>
      )}

      {externalError && <div className="onboarding__error">{externalError}</div>}

      <div className="indexer-list itemrow-grid">
        {externalLoading ? (
          <div className="indexer-card">
            <div className="indexer-row">
              <span className="indexer-url muted">{tr("Chargement...", "Loading...")}</span>
            </div>
          </div>
        ) : (instances || []).length === 0 ? (
          <div className="indexer-card">
            <div className="indexer-row">
              <span className="indexer-url muted">{tr("Aucun provider metadata configure", "No configured metadata provider")}</span>
            </div>
          </div>
        ) : (
          (instances || []).map((instance, idx) => {
            const providerKey = String(instance.providerKey || "").toLowerCase();
            const definition = definitionByProviderKey.get(providerKey);
            const statusOk = testStatusByExternal[instance.instanceId] === "ok";
            const statusErr = testStatusByExternal[instance.instanceId] === "error";
            const isTesting = testingExternal === instance.instanceId;
            const isConfigured = isInstanceConfigured(instance);
            const stats = providerStats?.[providerKey] || { calls: 0, failures: 0 };

            const statusClass = [
              statusOk && "test-ok",
              statusErr && "test-err",
            ].filter(Boolean).join(" ");

            return (
              <ItemRow
                key={instance.instanceId}
                id={idx + 1}
                title={instance.displayName || definition?.displayName || providerKey.toUpperCase()}
                meta={`Appels: ${Number(stats.calls || 0)} | Echecs: ${Number(stats.failures || 0)}`}
                enabled={instance.enabled !== false}
                statusClass={statusClass}
                badges={[
                  {
                    label: isConfigured ? "OK" : "NO",
                    className: isConfigured ? "pill-ok" : "pill-warn",
                  },
                ]}
                actions={[
                  {
                    icon: "science",
                    title: isTesting
                      ? tr("Test en cours...", "Test in progress...")
                      : instance.enabled === false
                        ? tr("Activez d'abord le provider", "Enable provider first")
                        : tr("Tester", "Test"),
                    onClick: () => testExternal(instance.instanceId),
                    disabled: isTesting || instance.enabled === false,
                    spinning: isTesting,
                  },
                  {
                    icon: "edit",
                    title: instance.enabled === false ? tr("Activez d'abord le provider", "Enable provider first") : tr("Modifier", "Edit"),
                    onClick: () => openExternalModalEdit(instance),
                    disabled: isTesting || instance.enabled === false,
                  },
                  {
                    icon: "delete",
                    title: tr("Supprimer", "Delete"),
                    onClick: () => openExternalDelete(instance),
                    disabled: isTesting,
                    className: "iconbtn--danger",
                  },
                ]}
                showToggle
                onToggle={() => openExternalToggle(instance)}
                toggleDisabled={isTesting}
              />
            );
          })
        )}
      </div>

      <Modal
        open={externalModalOpen}
        title={
          externalModalMode === "add"
            ? (externalModalStep === 1
              ? tr("Ajouter un provider metadata", "Add a metadata provider")
              : `${tr("Configurer", "Configure")}: ${externalModalDefinition?.displayName || tr("Provider", "Provider")}`)
            : `${tr("Modifier", "Edit")}: ${externalModalInstance?.displayName || externalModalDefinition?.displayName || tr("Provider", "Provider")}`
        }
        onClose={closeExternalModal}
        width={560}
      >
        <div style={{ padding: 12 }}>
          {externalModalMode === "add" && externalModalStep === 1 && (
            <div className="field" style={{ marginBottom: 12 }}>
              <label className="muted">{tr("Provider", "Provider")}</label>
              <select
                value={externalModalProviderKey}
                onChange={(e) => selectExternalModalProvider(e.target.value)}
                disabled={externalModalSaving || !hasAddableDefinitions}
              >
                <option value="" disabled>
                  {hasAddableDefinitions
                    ? tr("Selectionner...", "Select...")
                    : tr("Tous les providers sont deja ajoutes", "All providers are already added")}
                </option>
                {addableDefinitions.map((definition) => (
                  <option key={definition.providerKey} value={definition.providerKey}>
                    {definition.displayName}
                  </option>
                ))}
              </select>
              {!hasAddableDefinitions && (
                <div className="muted" style={{ marginTop: 8 }}>
                  {tr("Tous les providers sont deja ajoutes", "All providers are already added")}
                </div>
              )}
            </div>
          )}

          {(externalModalMode === "edit" || externalModalStep === 2) && (
            <>
              <div className="field" style={{ marginBottom: 12 }}>
                <label className="muted">{tr("Nom (optionnel)", "Name (optional)")}</label>
                <input
                  type="text"
                  value={externalModalDisplayName}
                  onChange={(e) => setExternalModalDisplayName(e.target.value)}
                  placeholder={externalModalDefinition?.displayName || "Nom du provider"}
                  disabled={externalModalSaving}
                />
              </div>

              <div className="field" style={{ marginBottom: 12 }}>
                <label className="muted">{tr("Base URL (optionnel)", "Base URL (optional)")}</label>
                <input
                  type="text"
                  value={externalModalBaseUrl}
                  onChange={(e) => setExternalModalBaseUrl(e.target.value)}
                  placeholder={externalModalDefinition?.defaultBaseUrl || "https://"}
                  disabled={externalModalSaving}
                />
              </div>

              <div className="indexer-card" style={{ marginBottom: 12 }}>
                <div className="indexer-row indexer-row--settings">
                  <span className="indexer-title">{tr("Actif", "Active")}</span>
                  <div className="indexer-actions">
                    <ToggleSwitch
                      checked={externalModalEnabled}
                      onIonChange={(event) => setExternalModalEnabled(event.detail.checked)}
                      className="settings-toggle"
                      disabled={externalModalSaving}
                    />
                  </div>
                </div>
              </div>

              {(externalModalDefinition?.fieldsSchema || []).map((field) => {
                const hasFlag = !!externalModalInstance?.authFlags?.[toHasFlagName(field.key)];
                const value = externalModalAuth[field.key] || "";
                const placeholder = (
                  value
                  || (externalModalMode === "edit" && hasFlag && field.secret)
                    ? "•••••••• (laisser vide pour conserver)"
                    : (field.placeholder || "")
                );
                return (
                  <div key={field.key} className="field" style={{ marginBottom: 12 }}>
                    <label className="muted">
                      {field.label}
                      {field.required ? " *" : ""}
                    </label>
                    <input
                      type={field.secret ? "password" : (field.type || "text")}
                      value={value}
                      onChange={(e) => setExternalModalAuthField(field.key, e.target.value)}
                      placeholder={placeholder}
                      disabled={externalModalSaving}
                    />
                  </div>
                );
              })}
            </>
          )}

          {externalModalError && (
            <div className="onboarding__error" style={{ marginBottom: 12 }}>
              {externalModalError}
            </div>
          )}

          <div className="formactions">
            {externalModalMode === "add" && externalModalStep === 1 ? (
              <button
                className="btn btn-accent"
                type="button"
                onClick={() => goExternalModalStep(2)}
                disabled={!externalModalProviderKey || externalModalSaving}
              >
                {tr("Suivant", "Next")}
              </button>
            ) : (
              <button
                className="btn btn-accent"
                type="button"
                onClick={saveExternalModal}
                disabled={!canSaveExternalModal || externalModalSaving}
              >
                {externalModalSaving ? tr("Enregistrement...", "Saving...") : tr("Enregistrer", "Save")}
              </button>
            )}
            <button
              className="btn"
              type="button"
              onClick={closeExternalModal}
              disabled={externalModalSaving}
            >
              {tr("Annuler", "Cancel")}
            </button>
          </div>
        </div>
      </Modal>

      <Modal
        open={externalDeleteOpen}
        title={tr("Supprimer le provider metadata", "Delete metadata provider")}
        onClose={closeExternalDelete}
        width={520}
      >
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>
            {tr("Confirmer la suppression ?", "Confirm deletion?")}
          </div>
          <div className="muted" style={{ marginBottom: 12 }}>
            {tr("Cette action va supprimer", "This action will delete")}{" "}
            <strong>{externalDeleteInstance?.displayName || externalDeleteInstance?.providerKey || "-"}</strong>.
            <br />
            {tr("Cette action est definitive.", "This action is permanent.")}
          </div>
          <div className="formactions">
            <button
              className="btn btn-danger"
              type="button"
              onClick={confirmExternalDelete}
              disabled={externalDeleteLoading}
            >
              {externalDeleteLoading ? tr("Suppression...", "Deleting...") : tr("Supprimer", "Delete")}
            </button>
            <button
              className="btn"
              type="button"
              onClick={closeExternalDelete}
              disabled={externalDeleteLoading}
            >
              {tr("Annuler", "Cancel")}
            </button>
          </div>
        </div>
      </Modal>

      <Modal
        open={externalToggleOpen}
        title={
          externalToggleInstance
            ? `${externalToggleInstance.enabled ? tr("Desactiver", "Disable") : tr("Activer", "Enable")} : ${externalToggleInstance.displayName || externalToggleInstance.providerKey}`
            : tr("Activer/Desactiver", "Enable/Disable")
        }
        onClose={closeExternalToggle}
        width={520}
      >
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>
            {tr("Confirmer l'action ?", "Confirm action?")}
          </div>
          <div className="muted" style={{ marginBottom: 12 }}>
            {externalToggleInstance?.enabled
              ? tr("Cette action va desactiver le provider.", "This action will disable the provider.")
              : tr("Cette action va activer le provider.", "This action will enable the provider.")}
          </div>
          <div className="formactions">
            <button className="btn" type="button" onClick={confirmExternalToggle}>
              {externalToggleInstance?.enabled ? tr("Desactiver", "Disable") : tr("Activer", "Enable")}
            </button>
            <button className="btn" type="button" onClick={closeExternalToggle}>
              {tr("Annuler", "Cancel")}
            </button>
          </div>
        </div>
      </Modal>
    </>
  );
}
