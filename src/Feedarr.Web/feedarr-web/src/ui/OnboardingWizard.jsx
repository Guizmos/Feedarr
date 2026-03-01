import React, { useEffect, useState } from "react";
import Modal from "./Modal.jsx";
import { apiPatch, apiPost, apiPut } from "../api/client.js";
import CategoryMappingBoard from "../components/shared/CategoryMappingBoard.jsx";
import {
  buildCategoryMappingsPatchDto,
  buildMappingsPayload,
  mapFromCapsAssignments,
  normalizeCategoryGroupKey,
} from "../domain/categories/index.js";
import { useMetadataProviders, toggleProviderInstance } from "../domain/providers/index.js";

const INDEXER_OPTIONS = ["C411", "YGEGE", "LA-CALE", "TOS"];

export default function OnboardingWizard({ open, status, onClose, onComplete }) {
  const providers = useMetadataProviders();

  const [idxName, setIdxName] = useState("");
  const [idxUrl, setIdxUrl] = useState("");
  const [idxKey, setIdxKey] = useState("");
  const [idxAuthMode, setIdxAuthMode] = useState("query");
  const [idxErr, setIdxErr] = useState("");
  const [idxOk, setIdxOk] = useState("");
  const [idxSaving, setIdxSaving] = useState(false);
  const [idxTesting, setIdxTesting] = useState(false);
  const [idxTested, setIdxTested] = useState(false);

  // Category states
  const [capsCategories, setCapsCategories] = useState([]);
  const [categoryMappings, setCategoryMappings] = useState(() => new Map());


  const [provTmdb, setProvTmdb] = useState("");
  const [provTvmaze, setProvTvmaze] = useState("");
  const [provFanart, setProvFanart] = useState("");
  const [provIgdbId, setProvIgdbId] = useState("");
  const [provIgdbSecret, setProvIgdbSecret] = useState("");
  const [provErr, setProvErr] = useState("");
  const [provOk, setProvOk] = useState("");
  const [provSaving, setProvSaving] = useState(false);
  const [provTesting, setProvTesting] = useState(false);
  const [provTested, setProvTested] = useState(false);
  const [provTestResults, setProvTestResults] = useState({ tmdb: null, tvmaze: null, fanart: null, igdb: null });
  const [testedInstanceIds, setTestedInstanceIds] = useState({});

  // Test indexer function
  async function testIndexer() {
    setIdxErr("");
    setIdxOk("");

    if (!idxName.trim() || !idxUrl.trim() || !idxKey.trim()) {
      setIdxErr("Nom, URL et clé API sont requis.");
      return;
    }

    setIdxTesting(true);
    try {
      const res = await apiPost("/api/sources/test", {
        torznabUrl: idxUrl.trim(),
        apiKey: idxKey.trim(),
        authMode: idxAuthMode,
        rssLimit: 50,
      });

      if (!res?.ok) {
        setIdxErr(res?.error || "Test invalide");
        return;
      }

      // Récupérer les catégories brutes depuis le backend (liste plate).
      let capsRes;
      try {
        capsRes = await apiPost("/api/categories/caps", {
          torznabUrl:   idxUrl.trim(),
          apiKey:       idxKey.trim(),
          authMode:     idxAuthMode,
          indexerName:  idxName.trim(),
          includeStandardCatalog: true,
          includeSpecific: true,
        });
      } catch (capsErr) {
        setIdxErr("Connexion réussie, mais la récupération des catégories a échoué : " + (capsErr?.message || "erreur inconnue"));
        return;
      }

      const categories = Array.isArray(capsRes?.categories) ? capsRes.categories : [];

      if (categories.length === 0) {
        setIdxErr("Connexion réussie, mais aucune catégorie compatible n'a été détectée. Vérifiez l'URL Torznab.");
        return;
      }

      setCapsCategories(categories);
      setCategoryMappings(mapFromCapsAssignments(categories));
      setIdxTested(true);
      setIdxOk("Connexion réussie ! Passez à l'étape suivante.");
    } catch (e) {
      setIdxErr(e?.message || "Erreur test indexeur");
    } finally {
      setIdxTesting(false);
    }
  }

  // Save indexer with categories
  async function saveIndexer() {
    setIdxErr("");
    setIdxOk("");

    setIdxSaving(true);
    try {
      const res = await apiPost("/api/sources", {
        name: idxName.trim(),
        torznabUrl: idxUrl.trim(),
        authMode: idxAuthMode,
        apiKey: idxKey.trim(),
      });

      // Activer l'indexeur
      if (res?.id) {
        await apiPut(`/api/sources/${res.id}/enabled`, { enabled: true });
        const mappingsPayload = buildMappingsPayload(categoryMappings);
        const selectedCategoryIds = [...categoryMappings.keys()]
          .map((catId) => Number(catId))
          .filter((catId) => Number.isFinite(catId) && catId > 0)
          .sort((a, b) => a - b);
        await apiPatch(
          `/api/sources/${res.id}/category-mappings`,
          buildCategoryMappingsPatchDto({
            mappings: mappingsPayload,
            selectedCategoryIds,
          })
        );
      }

      setIdxOk("Indexeur ajouté et activé !");
      if (typeof window !== "undefined") {
        window.dispatchEvent(new Event("onboarding:refresh"));
      }

      // Passer à l'étape suivante
      next();
    } catch (e) {
      const msg = e?.message || "";
      if (msg.toLowerCase().includes("already exists") || msg.includes("409")) {
        setIdxErr("Cet indexeur est déjà ajouté (URL Torznab déjà utilisée).");
      } else {
        setIdxErr(msg || "Erreur ajout indexeur");
      }
    } finally {
      setIdxSaving(false);
    }
  }

  const canTestIndexer = idxName.trim() && idxUrl.trim() && idxKey.trim();
  const selectedCount = categoryMappings.size;

  // Test providers function — uses domain hook (/api/providers/external)
  async function testProviders() {
    setProvErr("");
    setProvOk("");

    const hasTmdb = !!provTmdb.trim();
    const hasFanart = !!provFanart.trim();
    const hasIgdb = !!provIgdbId.trim() && !!provIgdbSecret.trim();

    // TVmaze: always attempt (may not require a key depending on backend definition)
    const providerInputs = [
      { key: "tmdb",   active: hasTmdb,   auth: { apiKey: provTmdb.trim() } },
      { key: "tvmaze", active: true,       auth: provTvmaze.trim() ? { apiKey: provTvmaze.trim() } : {} },
      { key: "fanart", active: hasFanart,  auth: { apiKey: provFanart.trim() } },
      { key: "igdb",   active: hasIgdb,    auth: { clientId: provIgdbId.trim(), clientSecret: provIgdbSecret.trim() } },
    ];

    const hasAnyInput = hasTmdb || hasFanart || hasIgdb;
    if (!hasAnyInput) {
      setProvErr("Ajoute au moins une clé provider complète.");
      return;
    }

    setProvTesting(true);
    const results = { tmdb: null, tvmaze: null, fanart: null, igdb: null };
    const newInstanceIds = {};

    try {
      for (const { key, active, auth } of providerInputs) {
        if (!active) continue;

        // Create or update instance as disabled, then test
        const upsertResult = await providers.upsertDisabled(key, auth);
        if (!upsertResult.ok || !upsertResult.instanceId) {
          console.error(`[OnboardingWizard] upsertDisabled failed for ${key}`, upsertResult.errorMessage);
          results[key] = "error";
          continue;
        }

        newInstanceIds[key] = upsertResult.instanceId;
        const testResult = await providers.test(upsertResult.instanceId);
        results[key] = testResult.ok ? "ok" : "error";
      }

      setTestedInstanceIds(newInstanceIds);
      setProvTestResults(results);

      const hasSuccess = Object.values(results).some((r) => r === "ok");
      const hasError = Object.values(results).some((r) => r === "error");

      if (hasSuccess) {
        setProvTested(true);
        if (hasError) {
          setProvOk("Certains providers ont été testés avec succès. Vous pouvez enregistrer.");
        } else {
          setProvOk("Tous les providers ont été testés avec succès !");
        }
      } else {
        setProvErr("Aucun provider n'a pu être testé avec succès.");
      }
    } catch (e) {
      console.error("[OnboardingWizard] testProviders failed", e);
      setProvErr(e?.message || "Erreur test providers");
    } finally {
      setProvTesting(false);
    }
  }

  // Save and activate providers — enables only the instances that passed the test
  async function saveProviders() {
    setProvErr("");
    setProvOk("");
    setProvSaving(true);

    try {
      const toEnable = Object.entries(testedInstanceIds).filter(
        ([key]) => provTestResults[key] === "ok"
      );

      if (toEnable.length === 0) {
        setProvErr("Aucun provider valide à activer.");
        return;
      }

      // Activate all tested-OK instances in parallel
      const enableResults = await Promise.all(
        toEnable.map(([, instanceId]) => toggleProviderInstance(instanceId, true))
      );

      const failures = enableResults.filter((r) => !r.ok);
      if (failures.length > 0) {
        const msgs = failures.map((r) => r.errorMessage).filter(Boolean).join(". ");
        setProvErr(msgs || "Certains providers n'ont pas pu être activés.");
        return;
      }

      // Reload domain state after batch enable
      await providers.loadAll();

      setProvOk("Providers enregistrés et activés !");
      if (typeof window !== "undefined") {
        window.dispatchEvent(new Event("onboarding:refresh"));
      }

      next();
    } catch (e) {
      console.error("[OnboardingWizard] saveProviders failed", e);
      setProvErr(e?.message || "Erreur sauvegarde providers");
    } finally {
      setProvSaving(false);
    }
  }

  const canTestProviders = true;

  const steps = [
    {
      title: "Bienvenue sur Feedarr",
      content: (
        <>
          <p className="onboarding__lead">
            On va configurer l'essentiel en quelques étapes.
          </p>
          <ul className="onboarding__list">
            <li>Ajout et test des indexeurs (Torznab)</li>
            <li>Sélection des catégories</li>
            <li>Configuration et test des providers de posters</li>
            <li>Validation et premier sync</li>
          </ul>
        </>
      ),
    },
    {
      title: "Ajouter un indexeur",
      content: (
        <>
          <p>
            Configure ton indexeur Torznab et teste la connexion.
          </p>
          <div className="onboarding__inline">
            <div className="formgrid">
              <div className="field">
                <label>Nom</label>
                <input
                  list="indexer-suggestions"
                  value={idxName}
                  onChange={(e) => setIdxName(e.target.value)}
                  placeholder="Ex: C411, YGEGE, mon-indexeur…"
                  disabled={idxTested}
                />
                <datalist id="indexer-suggestions">
                  {INDEXER_OPTIONS.map((opt) => (
                    <option key={opt} value={opt} />
                  ))}
                </datalist>
              </div>
              <div className="field" style={{ gridColumn: "1 / -1" }}>
                <label>URL Torznab</label>
                <input
                  value={idxUrl}
                  onChange={(e) => setIdxUrl(e.target.value)}
                  placeholder="https://.../api"
                  disabled={idxTested}
                />
              </div>
              <div className="field" style={{ gridColumn: "1 / -1" }}>
                <label>Clé API</label>
                <input
                  value={idxKey}
                  onChange={(e) => setIdxKey(e.target.value)}
                  placeholder="Votre clé"
                  disabled={idxTested}
                />
              </div>
            </div>

            {idxErr ? <div className="onboarding__error">{idxErr}</div> : null}
            {idxOk ? <div className="onboarding__ok">{idxOk}</div> : null}

            <div className="onboarding__actions-inline">
              {!idxTested ? (
                <button
                  className="btn btn-accent"
                  type="button"
                  disabled={!canTestIndexer || idxTesting}
                  onClick={testIndexer}
                >
                  {idxTesting ? "Test en cours..." : "Tester la connexion"}
                </button>
              ) : (
                <button
                  className="btn"
                  type="button"
                  onClick={() => {
                    setIdxTested(false);
                    setCapsCategories([]);
                    setCategoryMappings(new Map());
                    setIdxOk("");
                  }}
                >
                  Modifier
                </button>
              )}
            </div>
          </div>
        </>
      ),
    },
    {
      title: "Sélection des catégories",
      content: (
        <>
          <p>
            Sélectionne les catégories à synchroniser depuis cet indexeur.
          </p>
          <div className="onboarding__inline">
            {capsCategories.length > 0 ? (
              <>
                <CategoryMappingBoard
                  variant="wizard"
                  categories={capsCategories}
                  mappings={categoryMappings}
                  previewCredentials={idxTested ? {
                    torznabUrl: idxUrl.trim(),
                    authMode: idxAuthMode,
                    apiKey: idxKey.trim(),
                    sourceName: idxName.trim(),
                  } : null}
                  onChangeMapping={(catId, groupKey) => {
                    const normalized = normalizeCategoryGroupKey(groupKey);
                    setCategoryMappings((prev) => {
                      const next = new Map(prev);
                      if (!normalized) next.delete(catId);
                      else next.set(catId, normalized);
                      return next;
                    });
                  }}
                />
                <div className="muted" style={{ marginTop: 8 }}>
                  {selectedCount} catégorie{selectedCount > 1 ? "s" : ""} assignée{selectedCount > 1 ? "s" : ""}
                </div>
              </>
            ) : (
              <div className="onboarding__error">
                Aucune catégorie disponible. Retournez à l'étape précédente pour tester l'indexeur.
              </div>
            )}

            {idxErr ? <div className="onboarding__error">{idxErr}</div> : null}
            {idxOk ? <div className="onboarding__ok">{idxOk}</div> : null}

            <div className="onboarding__actions-inline">
              <button
                className="btn btn-accent"
                type="button"
                disabled={idxSaving}
                onClick={saveIndexer}
              >
                {idxSaving ? "Enregistrement..." : "Enregistrer l'indexeur"}
              </button>
            </div>
          </div>
        </>
      ),
    },
    {
      title: "Configurer les providers de posters",
      content: (
        <>
          <p>
            Ajoute tes clés TMDB, Fanart et/ou IGDB pour enrichir les posters et métadonnées.
          </p>
          <div className="onboarding__inline">
            <div className="formgrid">
              <div className="field">
                <label>
                  TMDB API Key
                  {provTestResults.tmdb === "ok" && <span className="provider-status ok"> ✓</span>}
                  {provTestResults.tmdb === "error" && <span className="provider-status error"> ✗</span>}
                </label>
                <input
                  value={provTmdb}
                  onChange={(e) => {
                    setProvTmdb(e.target.value);
                    setProvTested(false);
                    setProvTestResults((r) => ({ ...r, tmdb: null }));
                  }}
                  placeholder="TMDB key"
                  disabled={provTested || provTesting}
                />
              </div>
              <div className="field">
                <label>
                  TVmaze API Key
                  {provTestResults.tvmaze === "ok" && <span className="provider-status ok"> âœ“</span>}
                  {provTestResults.tvmaze === "error" && <span className="provider-status error"> âœ—</span>}
                </label>
                <input
                  value={provTvmaze}
                  onChange={(e) => {
                    setProvTvmaze(e.target.value);
                    setProvTested(false);
                    setProvTestResults((r) => ({ ...r, tvmaze: null }));
                  }}
                  placeholder="TVmaze key"
                  disabled={provTested || provTesting}
                />
              </div>
              <div className="field">
                <label>
                  Fanart API Key
                  {provTestResults.fanart === "ok" && <span className="provider-status ok"> ✓</span>}
                  {provTestResults.fanart === "error" && <span className="provider-status error"> ✗</span>}
                </label>
                <input
                  value={provFanart}
                  onChange={(e) => {
                    setProvFanart(e.target.value);
                    setProvTested(false);
                    setProvTestResults((r) => ({ ...r, fanart: null }));
                  }}
                  placeholder="Fanart key"
                  disabled={provTested || provTesting}
                />
              </div>
              <div className="field">
                <label>
                  IGDB Client ID
                  {provTestResults.igdb === "ok" && <span className="provider-status ok"> ✓</span>}
                  {provTestResults.igdb === "error" && <span className="provider-status error"> ✗</span>}
                </label>
                <input
                  value={provIgdbId}
                  onChange={(e) => {
                    setProvIgdbId(e.target.value);
                    setProvTested(false);
                    setProvTestResults((r) => ({ ...r, igdb: null }));
                  }}
                  placeholder="IGDB client id"
                  disabled={provTested || provTesting}
                />
              </div>
              <div className="field">
                <label>IGDB Client Secret</label>
                <input
                  value={provIgdbSecret}
                  onChange={(e) => {
                    setProvIgdbSecret(e.target.value);
                    setProvTested(false);
                    setProvTestResults((r) => ({ ...r, igdb: null }));
                  }}
                  placeholder="IGDB client secret"
                  disabled={provTested || provTesting}
                />
              </div>
            </div>

            {provErr ? <div className="onboarding__error">{provErr}</div> : null}
            {provOk ? <div className="onboarding__ok">{provOk}</div> : null}

            <div className="onboarding__actions-inline">
              {!provTested ? (
                <button
                  className="btn btn-accent"
                  type="button"
                  disabled={!canTestProviders || provTesting}
                  onClick={testProviders}
                >
                  {provTesting ? "Test en cours..." : "Tester les providers"}
                </button>
              ) : (
                <>
                  <button
                    className="btn btn-accent"
                    type="button"
                    disabled={provSaving}
                    onClick={saveProviders}
                  >
                    {provSaving ? "Enregistrement..." : "Enregistrer les providers"}
                  </button>
                  <button
                    className="btn"
                    type="button"
                    onClick={() => {
                      setProvTested(false);
                      setProvTestResults({ tmdb: null, tvmaze: null, fanart: null, igdb: null });
                      setProvOk("");
                    }}
                  >
                    Modifier
                  </button>
                </>
              )}
            </div>
          </div>
        </>
      ),
    },
    {
      title: "Terminer",
      content: (
        <>
          <p>
            Une fois tes sources et providers configurés, lance une synchro pour remplir ta bibliothèque.
          </p>
          <div className="onboarding__hint">
            Astuce: tu peux ajuster la fréquence dans <b>Paramètres</b> &gt; <b>Indexeurs</b>.
          </div>
        </>
      ),
    },
  ];

  const [step, setStep] = useState(0);
  const last = steps.length - 1;

  useEffect(() => {
    if (open) {
      setStep(0);
      // Reset indexer states
      setIdxName("");
      setIdxUrl("");
      setIdxKey("");
      setIdxAuthMode("query");
      setIdxErr("");
      setIdxOk("");
      setIdxSaving(false);
      setIdxTesting(false);
      setIdxTested(false);
      setCapsCategories([]);
      setCategoryMappings(new Map());
      // Reset provider states
      setProvTmdb("");
      setProvTvmaze("");
      setProvFanart("");
      setProvIgdbId("");
      setProvIgdbSecret("");
      setProvErr("");
      setProvOk("");
      setProvSaving(false);
      setProvTesting(false);
      setProvTested(false);
      setProvTestResults({ tmdb: null, tvmaze: null, fanart: null, igdb: null });
      setTestedInstanceIds({});
      // Load provider definitions (needed by upsertDisabled)
      providers.loadAll();
    }
    // providers.loadAll is stable (useCallback with [] deps)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  function next() {
    setStep((s) => Math.min(s + 1, last));
  }

  function back() {
    setStep((s) => Math.max(s - 1, 0));
  }

  function finish() {
    onComplete?.();
  }

  if (!open) return null;

  const current = steps[step];
  const indexersOk = !!status?.hasSources || !!idxOk;
  const providersOk = !!status?.hasExternalProviders || !!provOk;

  const titleNode = (
    <div className="onboarding__titlebar">
      <img className="onboarding__logo" src={`${import.meta.env.BASE_URL}feedarr-logo.png`} alt="Feedarr" />
      <div className="onboarding__titletext">{current?.title || "Onboarding"}</div>
      <span className="onboarding__titlespacer" />
    </div>
  );

  return (
    <Modal
      open={open}
      title={titleNode}
      titleClassName="onboarding__title"
      onClose={onClose}
      width={800}
    >
      <div className="onboarding">
        <div className="onboarding__stepper">
          {steps.map((_, i) => (
            <div
              key={i}
              className={`onboarding__stepper-item ${i <= step ? "is-active" : ""}`}
            />
          ))}
        </div>

        {status ? (
          <div className="onboarding__meta">
            <span className={`settings-badge ${indexersOk ? "ok" : "warn"}`}>
              Indexeurs: {indexersOk ? "OK" : "à configurer"}
            </span>
            <span className={`settings-badge ${providersOk ? "ok" : "warn"}`}>
              Providers: {providersOk ? "OK" : "à configurer"}
            </span>
          </div>
        ) : null}

        <div className="onboarding__content">{current?.content}</div>

        <div className="onboarding__actions">
          <button className="btn" type="button" onClick={onClose}>
            Plus tard
          </button>

          <div className="onboarding__nav">
            {step > 0 && (
              <button
                className="btn"
                type="button"
                onClick={() => {
                  // Reset indexer states when going back from categories step
                  if (step === 2) {
                    setIdxErr("");
                    setIdxOk("");
                  }
                  back();
                }}
              >
                Précédent
              </button>
            )}
            {step < last ? (
              <button
                className="btn btn-accent"
                type="button"
                onClick={next}
                disabled={
                  (step === 1 && !idxTested) || // Must test indexer before proceeding
                  (step === 2) || // Must use "Enregistrer l'indexeur" button
                  (step === 3) // Must use "Enregistrer les providers" button
                }
                title={
                  step === 1 && !idxTested
                    ? "Testez d'abord la connexion"
                    : step === 2
                    ? "Cliquez sur Enregistrer l'indexeur"
                    : step === 3
                    ? "Cliquez sur Enregistrer les providers"
                    : undefined
                }
              >
                Suivant
              </button>
            ) : (
              <button className="btn btn-accent" type="button" onClick={finish}>
                Terminer
              </button>
            )}
          </div>
        </div>
      </div>
    </Modal>
  );
}
