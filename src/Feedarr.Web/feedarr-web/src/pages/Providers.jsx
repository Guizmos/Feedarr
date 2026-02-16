import React, { useCallback, useEffect, useMemo, useState } from "react";
import { apiDelete, apiGet, apiPost, apiPut } from "../api/client.js";
import Loader from "../ui/Loader.jsx";
import { useSubbarSetter } from "../layout/useSubbar.js";
import SubAction from "../ui/SubAction.jsx";
import Modal from "../ui/Modal.jsx";
import ToggleSwitch from "../ui/ToggleSwitch.jsx";
import ItemRow from "../ui/ItemRow.jsx";
import {
  buildProviderRows,
  labelForProviderType,
  normalizeProviderBaseUrl,
} from "./providersListModel.js";

const PROVIDER_TYPES = ["jackett", "prowlarr"];

function shouldRetryJackett(error) {
  const msg = String(error?.message || "");
  return /invalid start of a value|unexpected token\s*</i.test(msg);
}

export default function Providers() {
  const setContent = useSubbarSetter();

  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");
  const [notice, setNotice] = useState("");
  const [items, setItems] = useState([]);
  const [testingId, setTestingId] = useState(null);
  const [testStatusById, setTestStatusById] = useState({});

  // modal state
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState(null);
  const [initialEdit, setInitialEdit] = useState(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [confirmTarget, setConfirmTarget] = useState(null);
  const [saving, setSaving] = useState(false);
  const [modalTestStatus, setModalTestStatus] = useState("idle");
  const [modalTestMsg, setModalTestMsg] = useState("");

  // form fields
  const [type, setType] = useState("jackett");
  const [name, setName] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [showKey, setShowKey] = useState(false);
  const [modalEnabled, setModalEnabled] = useState(true);

  const load = useCallback(async () => {
    setErr("");
    setLoading(true);
    try {
      const data = await apiGet("/api/providers");
      setItems(Array.isArray(data) ? data : []);
    } catch (e) {
      setErr(e?.message || "Erreur chargement fournisseurs");
    } finally {
      setLoading(false);
    }
  }, []);

  const bumpLastTest = useCallback((providerId) => {
    const ts = Math.floor(Date.now() / 1000);
    setItems((prev) =>
      (Array.isArray(prev) ? prev : []).map((p) =>
        p.id === providerId ? { ...p, lastTestOkAt: ts } : p
      )
    );
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  const availableProviderTypes = useMemo(() => {
    const usedTypes = new Set(
      (Array.isArray(items) ? items : [])
        .map((p) => String(p?.type || "").toLowerCase())
        .filter(Boolean)
    );
    return PROVIDER_TYPES.filter((providerType) => !usedTypes.has(providerType));
  }, [items]);

  const hasAddableProviderType = availableProviderTypes.length > 0;

  const openAdd = useCallback(() => {
    if (!hasAddableProviderType) {
      setNotice("Tous les fournisseurs disponibles sont déjà ajoutés.");
      return;
    }

    setEditing(null);
    setInitialEdit(null);
    setType(availableProviderTypes[0] || "");
    setName("");
    setBaseUrl("");
    setApiKey("");
    setShowKey(false);
    setModalEnabled(true);
    setModalTestStatus("idle");
    setModalTestMsg("");
    setModalOpen(true);
  }, [availableProviderTypes, hasAddableProviderType]);

  function openEdit(p) {
    setEditing(p);
    setType(p?.type || "jackett");
    setName(p?.name || "");
    setBaseUrl(p?.baseUrl || "");
    setApiKey("");
    setShowKey(false);
    setModalEnabled(!!p?.enabled);
    setInitialEdit({
      type: p?.type || "jackett",
      name: p?.name || "",
      baseUrl: p?.baseUrl || "",
      enabled: !!p?.enabled,
    });
    setModalTestStatus("idle");
    setModalTestMsg("");
    setModalOpen(true);
  }

  function closeModal() {
    if (saving || modalTestStatus === "pending") return;
    setModalOpen(false);
    setModalTestStatus("idle");
    setModalTestMsg("");
  }

  function openDeleteConfirm(p) {
    setConfirmTarget(p);
    setConfirmOpen(true);
  }

  function closeDeleteConfirm() {
    setConfirmOpen(false);
    setConfirmTarget(null);
  }

  async function testProviderModal() {
    setModalTestMsg("");
    setModalTestStatus("pending");
    const normalizedBaseUrl = normalizeProviderBaseUrl(baseUrl);
    try {
      const currentType = String(type || "").toLowerCase();
      const baseChanged = initialEdit
        ? normalizeProviderBaseUrl(initialEdit.baseUrl) !== normalizedBaseUrl
        : true;
      const wantsInline = !editing?.id || apiKey.trim() || baseChanged || currentType !== String(initialEdit?.type || "").toLowerCase();

      if (wantsInline) {
        if (!normalizedBaseUrl) {
          setModalTestStatus("error");
          setModalTestMsg("Base URL requise pour tester.");
          return;
        }
        if (!apiKey.trim()) {
          setModalTestStatus("error");
          setModalTestMsg("Renseigne la clé API pour tester les nouveaux paramètres.");
          return;
        }
        const runInlineTest = async (allowRetry) => {
          try {
            return await apiPost("/api/providers/test", {
              type: currentType,
              baseUrl: normalizedBaseUrl,
              apiKey: apiKey.trim(),
            });
          } catch (err) {
            if (allowRetry && shouldRetryJackett(err)) {
              await new Promise((r) => setTimeout(r, 350));
              return runInlineTest(false);
            }
            throw err;
          }
        };
        const res = await runInlineTest(true);
        const count = res?.count ?? 0;
        setModalTestStatus("ok");
        setModalTestMsg(`Connexion OK (${count} indexeur${count > 1 ? "s" : ""})`);
        if (editing?.id) bumpLastTest(editing.id);
        return;
      }

      const runStoredTest = async (allowRetry) => {
        try {
          return await apiPost(`/api/providers/${editing.id}/test`);
        } catch (err) {
          if (allowRetry && shouldRetryJackett(err)) {
            await new Promise((r) => setTimeout(r, 350));
            return runStoredTest(false);
          }
          throw err;
        }
      };
      const res = await runStoredTest(true);
      const count = res?.count ?? 0;
      setModalTestStatus("ok");
      setModalTestMsg(`Connexion OK (${count} indexeur${count > 1 ? "s" : ""})`);
      if (editing?.id) bumpLastTest(editing.id);
    } catch (e) {
      setModalTestStatus("error");
      setModalTestMsg(e?.message || "Erreur test fournisseur");
    }
  }

  async function save(e) {
    e.preventDefault();
    if (saving) return;
    setErr("");
    setNotice("");
    setSaving(true);

    if (isEditing && !isDirty) {
      setSaving(false);
      return;
    }

    try {
      if (editing?.id) {
        const payload = {
          type,
          name: name.trim() || labelForProviderType(type),
          baseUrl: normalizedBaseUrl,
          apiKey: apiKey.trim() || null,
        };
        await apiPut(`/api/providers/${editing.id}`, payload);
        if (initialEdit && modalEnabled !== !!initialEdit.enabled) {
          const res = await apiPut(`/api/providers/${editing.id}/enabled`, { enabled: modalEnabled });
          if (res?.message) {
            setNotice(res.message);
          }
        }
      } else {
        const payload = {
          type,
          name: name.trim() || labelForProviderType(type),
          baseUrl: normalizedBaseUrl,
          apiKey: apiKey.trim() || null,
          enabled: modalEnabled,
        };
        await apiPost("/api/providers", payload);
      }
      await load();
      closeModal();
    } catch (e2) {
      setErr(e2?.message || "Erreur sauvegarde");
    } finally {
      setSaving(false);
    }
  }

  async function toggleEnabled(p) {
    if (!p?.id) return;
    setErr("");
    setNotice("");
    try {
      const res = await apiPut(`/api/providers/${p.id}/enabled`, { enabled: !p.enabled });
      if (res?.message) {
        setNotice(res.message);
      }
      await load();
    } catch (e) {
      setErr(e?.message || "Erreur enable/disable");
    }
  }

  async function testProviderRow(p) {
    if (!p?.id || !p.enabled) return;
    if (testingId === p.id) return;
    setErr("");
    setNotice("");
    const startedAt = Date.now();
    setTestingId(p.id);
    setTestStatusById((prev) => ({ ...prev, [p.id]: "pending" }));
    try {
      const runTest = async (allowRetry) => {
        try {
          return await apiPost(`/api/providers/${p.id}/test`);
        } catch (err) {
          if (allowRetry && shouldRetryJackett(err)) {
            await new Promise((r) => setTimeout(r, 350));
            return runTest(false);
          }
          throw err;
        }
      };
      const res = await runTest(true);
      if (res?.ok) {
        bumpLastTest(p.id);
      }
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(1200 - elapsed, 0);
      setTimeout(() => {
        setTestStatusById((prev) => ({ ...prev, [p.id]: res?.ok ? "ok" : "error" }));
        setTimeout(() => {
          setTestStatusById((prev) => {
            const next = { ...prev };
            delete next[p.id];
            return next;
          });
        }, 1200);
        setTestingId(null);
      }, wait);
    } catch (e) {
      setErr(e?.message || "Erreur test");
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(1200 - elapsed, 0);
      setTimeout(() => {
        setTestStatusById((prev) => ({ ...prev, [p.id]: "error" }));
        setTimeout(() => {
          setTestStatusById((prev) => {
            const next = { ...prev };
            delete next[p.id];
            return next;
          });
        }, 1200);
        setTestingId(null);
      }, wait);
    }
  }

  async function removeProvider(p) {
    if (!p?.id) return;
    setErr("");
    setNotice("");
    try {
      const res = await apiDelete(`/api/providers/${p.id}`);
      if (res?.disabledSources > 0) {
        setNotice(`${res.disabledSources} indexeur${res.disabledSources > 1 ? "s" : ""} désactivé${res.disabledSources > 1 ? "s" : ""}.`);
      }
      await load();
    } catch (e) {
      setErr(e?.message || "Erreur suppression");
    }
  }

  const rows = useMemo(() => buildProviderRows(items), [items]);

  const isEditing = !!editing;
  const isAdding = !editing;
  const normalizedBaseUrl = normalizeProviderBaseUrl(baseUrl);
  const isDirty = useMemo(() => {
    if (!isEditing || !initialEdit) return true;
    const typeDirty = String(type || "").toLowerCase() !== String(initialEdit.type || "").toLowerCase();
    const nameDirty = String(name || "") !== String(initialEdit.name || "");
    const baseDirty = normalizeProviderBaseUrl(initialEdit.baseUrl) !== normalizedBaseUrl;
    const enabledDirty = modalEnabled !== !!initialEdit.enabled;
    const apiKeyDirty = !!apiKey.trim();
    return typeDirty || nameDirty || baseDirty || enabledDirty || apiKeyDirty;
  }, [isEditing, initialEdit, type, name, normalizedBaseUrl, modalEnabled, apiKey]);

  const canSave = isAdding
    ? !!type && !!normalizedBaseUrl && !!apiKey.trim()
    : !!type && !!normalizedBaseUrl && isDirty;

  const typeOptions = useMemo(() => {
    if (isAdding) return availableProviderTypes;
    return type ? [type] : [];
  }, [availableProviderTypes, isAdding, type]);

  useEffect(() => {
    setContent(
      <>
        <SubAction icon="refresh" label="Refresh" onClick={load} />
        <SubAction
          icon="add_circle"
          label="Ajouter"
          onClick={openAdd}
          disabled={!hasAddableProviderType}
          title={
            hasAddableProviderType
              ? "Ajouter"
              : "Tous les fournisseurs disponibles sont déjà ajoutés"
          }
        />
      </>
    );
    return () => setContent(null);
  }, [setContent, load, openAdd, hasAddableProviderType]);

  return (
    <div className="page page--providers">
      <div className="pagehead">
        <div>
          <h1>Fournisseurs</h1>
          <div className="muted">Jackett / Prowlarr</div>
        </div>
      </div>

      {err && (
        <div className="errorbox">
          <div className="errorbox__title">Erreur</div>
          <div className="muted">{err}</div>
        </div>
      )}
      {notice && (
        <div className="noticebox">
          <div className="errorbox__title">Info</div>
          <div className="muted">{notice}</div>
        </div>
      )}

      {loading ? (
        <Loader label="Chargement des fournisseurs…" />
      ) : (
        <div className="indexer-list itemrow-grid">
          {rows.map((p) => {
            const isTesting = testingId === p.id;
            const statusClass = [
              testStatusById[p.id] === "ok" && "test-ok",
              testStatusById[p.id] === "error" && "test-err",
            ].filter(Boolean).join(" ");
            const meta = p._url;
            const metaSub = p._lastTest
              ? `Derniere synchro: ${p._lastTest}`
              : "Derniere synchro...";

            return (
              <ItemRow
                key={p.id}
                id={p.id}
                title={p._name}
                meta={meta}
                metaSub={metaSub}
                enabled={p.enabled}
                statusClass={statusClass}
                badges={[
                  { label: p._typeLabel, className: "pill--accent" },
                  p.linkedSources > 0
                    ? { label: `${p.linkedSources} indexeur${p.linkedSources > 1 ? "s" : ""}` }
                    : null,
                ].filter(Boolean)}
                actions={[
                  {
                    icon: "science",
                    title: isTesting ? "Test en cours..." : "Test",
                    onClick: () => testProviderRow(p),
                    disabled: isTesting || !p.enabled,
                    spinning: isTesting,
                  },
                  {
                    icon: "edit",
                    title: "Éditer",
                    onClick: () => openEdit(p),
                    disabled: isTesting || !p.enabled,
                  },
                  {
                    icon: "delete",
                    title: "Supprimer",
                    onClick: () => openDeleteConfirm(p),
                    disabled: isTesting || !p.enabled,
                    className: "iconbtn--danger",
                  },
                ]}
                showToggle
                onToggle={() => toggleEnabled(p)}
                toggleDisabled={isTesting}
              />
            );
          })}
        </div>
      )}

      {/* MODAL DELETE CONFIRM */}
      <Modal
        open={confirmOpen}
        title={confirmTarget ? `Supprimer : ${confirmTarget?.name ?? confirmTarget?.id}` : "Supprimer"}
        onClose={closeDeleteConfirm}
        width={520}
      >
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>
            Confirmer la suppression ?
          </div>
          <div className="muted">
            Cette action est irréversible.
          </div>
          {confirmTarget?.linkedSources > 0 && (
            <div className="muted" style={{ marginTop: 6 }}>
              Supprimer aussi {confirmTarget.linkedSources} indexeur{confirmTarget.linkedSources > 1 ? "s" : ""} lié{confirmTarget.linkedSources > 1 ? "s" : ""} (désactivation automatique).
            </div>
          )}
          <div className="formactions" style={{ marginTop: 16 }}>
            <button
              className="btn btn-danger"
              type="button"
              onClick={async () => {
                const target = confirmTarget;
                closeDeleteConfirm();
                if (target) await removeProvider(target);
              }}
            >
              Supprimer
            </button>
            <button className="btn" type="button" onClick={closeDeleteConfirm}>
              Annuler
            </button>
          </div>
        </div>
      </Modal>

      {/* MODAL ADD / EDIT */}
      <Modal
        open={modalOpen}
        title={editing ? `Modifier : ${editing?.name ?? editing?.id}` : "Ajouter un fournisseur"}
        onClose={closeModal}
        width={560}
      >
        <form onSubmit={save} className="formgrid formgrid--edit">
          <div className="field">
            <label className="muted">Type</label>
            <select value={type} onChange={(e) => setType(e.target.value)} disabled={!isAdding}>
              {typeOptions.map((optionType) => (
                <option key={optionType} value={optionType}>
                  {labelForProviderType(optionType)}
                </option>
              ))}
            </select>
          </div>

          <div className="field">
            <label className="muted">Nom</label>
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder={labelForProviderType(type)}
            />
          </div>

          <div className="field">
            <label className="muted">Base URL</label>
            <input
              value={baseUrl}
              onChange={(e) => setBaseUrl(e.target.value)}
              placeholder={type === "prowlarr" ? "http://192.168.1.x:9696 ou https://domaine.tld/prowlarr" : "http://192.168.1.x:9117 ou https://domaine.tld/jackett"}
              disabled={modalTestStatus === "pending"}
            />
            <span className="field-hint">IP, hostname ou URL reverse proxy (http/https)</span>
          </div>

          <div className="field">
            <label className="muted">Clé API</label>
            <div className="setup-jackett-key">
              <input
                type={showKey ? "text" : "password"}
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
                placeholder={isEditing ? "Laisse vide pour ne pas changer" : `Clé API ${labelForProviderType(type)}`}
                disabled={modalTestStatus === "pending"}
              />
              <button
                className="btn"
                type="button"
                onClick={() => setShowKey((v) => !v)}
                disabled={modalTestStatus === "pending"}
              >
                {showKey ? "Masquer" : "Afficher"}
              </button>
            </div>
          </div>

          {modalTestStatus === "pending" && (
            <div className="muted" style={{ gridColumn: "1 / -1" }}>
              <span className="spinner" style={{ marginRight: 8 }} /> Test en cours...
            </div>
          )}
          {modalTestStatus === "error" && (
            <div className="errorbox" style={{ gridColumn: "1 / -1" }}>
              <div className="errorbox__title">Erreur</div>
              <div className="muted">{modalTestMsg}</div>
            </div>
          )}
          {modalTestStatus === "ok" && (
            <div className="noticebox" style={{ gridColumn: "1 / -1" }}>
              <div className="errorbox__title">OK</div>
              <div className="muted">{modalTestMsg}</div>
            </div>
          )}

          <div className="formactions">
            <div className="formactions-row">
              <div className="formactions-left">
                <ToggleSwitch
                  checked={modalEnabled}
                  onIonChange={(e) => setModalEnabled(e.detail.checked)}
                  className="settings-toggle"
                  disabled={modalTestStatus === "pending"}
                  title={modalEnabled ? "Désactiver" : "Activer"}
                />
                <span className="muted">{modalEnabled ? "Actif" : "Inactif"}</span>
              </div>
              <div className="formactions-right">
                {canSave && modalTestStatus !== "ok" && (
                  <button
                    className="btn"
                    type="button"
                    onClick={testProviderModal}
                    disabled={modalTestStatus === "pending"}
                  >
                    {modalTestStatus === "pending" ? "Test..." : "Tester"}
                  </button>
                )}
                {canSave && modalTestStatus === "ok" && (
                  <button className="btn btn-accent" type="submit" disabled={saving}>
                    {saving ? "Enregistrement..." : "Enregistrer"}
                  </button>
                )}
                <button className="btn" type="button" onClick={closeModal} disabled={saving}>
                  Annuler
                </button>
              </div>
            </div>
          </div>
        </form>
      </Modal>
    </div>
  );
}
