import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { apiGet, apiPost } from "../api/client.js";
import Modal from "../ui/Modal.jsx";
import InlineNotice from "./settings/InlineNotice.jsx";
import { tr } from "../app/uiText.js";
import Step0Language from "../components/setup/Step0Language.jsx";
import Step1Intro from "../components/setup/Step1Intro.jsx";
import Step2Security from "../components/setup/Step2Security.jsx";
import Step2Providers from "../components/setup/Step2Providers.jsx";
import Step3JackettConn from "../components/setup/Step3JackettConn.jsx";
import Step31JackettIndexers from "../components/setup/Step31JackettIndexers.jsx";
import Step4ArrApps from "../components/setup/Step4ArrApps.jsx";
import Step5Summary from "../components/setup/Step5Summary.jsx";
import StepConfigurationRestore from "../components/setup/StepConfigurationRestore.jsx";
import useSetupRestore from "../components/setup/useSetupRestore.js";
import { backupRoot, configurationRequest, resumeSetupSteps } from "../components/backup/configurationBackupApi.js";

const STORAGE_KEY = "feedarr:setupStep";
const ONBOARDING_FLAG = "feedarr:onboardingDone";
const STORAGE_HAS_JACKETT_SOURCE = "feedarr:setupHasJackettSource";
const JACKETT_STORAGE_KEYS = [
  "feedarr:jackettBaseUrl",
  "feedarr:jackettApiKey",
  "feedarr:jackettProvider",
  "feedarr:jackettIndexersCache",
  "feedarr:jackettConfigured",
  "feedarr:jackettManualOnly",
  "feedarr:prowlarrBaseUrl",
  "feedarr:prowlarrApiKey",
  "feedarr:prowlarrIndexersCache",
  "feedarr:prowlarrConfigured",
  "feedarr:prowlarrManualOnly",
];
const MIN_STEP = 1;
const MAX_STEP = 8;

function clampStep(value) {
  if (!Number.isFinite(value)) return MIN_STEP;
  return Math.min(MAX_STEP, Math.max(MIN_STEP, Math.floor(value)));
}

function readStoredStep() {
  if (typeof window === "undefined") return MIN_STEP;
  const raw = window.localStorage.getItem(STORAGE_KEY);
  const num = Number(raw);
  return clampStep(num || MIN_STEP);
}

export default function SetupWizard() {
  const navigate = useNavigate();
  const [step, setStep] = useState(readStoredStep);
  const [maxStep, setMaxStep] = useState(step);
  const [finishing, setFinishing] = useState(false);
  const [path, setPath] = useState(() => {
    if (typeof window === "undefined") return null;
    if (window.localStorage.getItem("feedarr:setupRestore") === "true") return "restore";
    return window.localStorage.getItem("feedarr:setupPath") === "install" || readStoredStep() > MIN_STEP ? "install" : null;
  });
  const restoreMode = path === "restore";
  const [leaveOpen, setLeaveOpen] = useState(false);
  const [restoreSteps, setRestoreSteps] = useState(null);
  const [restoreError, setRestoreError] = useState("");
  const [completionBootstrap, setCompletionBootstrap] = useState("");
  const [languageStepStatus, setLanguageStepStatus] = useState({
    ready: false,
    saving: false,
    error: "",
  });
  const [providersValidation, setProvidersValidation] = useState({});
  const [providersAllAdded, setProvidersAllAdded] = useState(false);
  const [securityStepStatus, setSecurityStepStatus] = useState({
    ready: true,
    saving: false,
    error: "",
    authRequired: false,
    authConfigured: true,
    authMode: "smart",
  });
  const [jackettStatus, setJackettStatus] = useState({
    provider: "",
    ready: false,
    baseUrl: "",
    manualOnly: false,
  });
  const [jackettHasSources, setJackettHasSources] = useState(false);
  const [jackettResetToken, setJackettResetToken] = useState(0);
  const securitySaveRef = useRef(null);
  const restoredStepsRef = useRef(null);

  const restored = (setup, bootstrap) => {
    const remaining = resumeSetupSteps(setup.restoreSteps);
    window.localStorage.removeItem("feedarr:setupRestore");
    if (setup.onboardingDone) { navigate("/library", { replace: true }); return; }
    if (remaining?.length) {
      restoredStepsRef.current = remaining;
      setRestoreSteps(remaining); setStep(remaining[0]); setMaxStep(MAX_STEP);
      setCompletionBootstrap(bootstrap); setPath("install");
    }
  };
  const restore = useSetupRestore(restoreMode, restored);
  const chooseRestore = value => {
    if (restore.locked) return;
    if (value) restore.startFresh();
    setPath(value ? "restore" : "install");
    if (!value) { setStep(MIN_STEP); setMaxStep(MIN_STEP); }
    window.localStorage.setItem("feedarr:setupRestore", String(value));
    window.localStorage.setItem("feedarr:setupPath", value ? "restore" : "install");
  };
  const showWelcome = () => {
    setPath(null); setLeaveOpen(false); setCompletionBootstrap(""); setRestoreError("");
    window.localStorage.removeItem("feedarr:setupRestore");
    window.localStorage.removeItem("feedarr:setupPath");
    window.localStorage.removeItem(STORAGE_KEY);
    setStep(MIN_STEP); setMaxStep(MIN_STEP);
  };
  const returnToChoice = () => {
    if (restoreMode) {
      if (restore.busy || restore.checking || restore.state?.confirmed || restore.state?.applied) return;
      if (restore.state?.pending) { setLeaveOpen(true); return; }
      void restore.leave(false, showWelcome);
    } else if (!restoreSteps && !finishing && !languageStepStatus.saving && !securityStepStatus.saving) showWelcome();
  };

  const returnBlockReason = restoreMode
    ? restore.state?.confirmed || restore.state?.applied
      ? "La restauration est déjà confirmée ou appliquée. Poursuivez ce parcours pour la finaliser ; le retour au choix est indisponible."
      : restore.busy ? "Attendez la fin de l’opération avant de revenir au choix."
      : restore.checking ? "Vérification du plan de restauration en cours. Le retour sera disponible après cette vérification." : ""
    : restoreSteps ? "La configuration restaurée est déjà appliquée. Terminez les étapes manquantes avant de quitter ce parcours."
      : finishing || languageStepStatus.saving || securityStepStatus.saving ? "Attendez la fin de l’enregistrement avant de revenir au choix." : "";
  const returnFeedback = returnBlockReason || (restoreMode && restore.leaveError
    ? `Retour au choix impossible : ${restore.leaveError} Vérifiez l’accès au serveur et le secret bootstrap, puis réessayez.` : "");

  useEffect(() => {
    if (!path) return;
    setMaxStep((prev) => Math.max(prev, step));
    if (typeof window !== "undefined") {
      window.localStorage.setItem(STORAGE_KEY, String(step));
    }
  }, [step, path]);

  useEffect(() => {
    let active = true;
    apiGet("/api/system/onboarding")
      .then((status) => {
        if (!active || typeof window === "undefined") return;
        const prevDone = window.localStorage.getItem(ONBOARDING_FLAG) === "true";
        if (status?.onboardingDone) {
          window.localStorage.setItem(ONBOARDING_FLAG, "true");
          navigate("/library", { replace: true });
          return;
        }
        if (prevDone) {
          window.localStorage.setItem(ONBOARDING_FLAG, "false");
          window.localStorage.removeItem(STORAGE_KEY);
          JACKETT_STORAGE_KEYS.forEach((key) => window.localStorage.removeItem(key));
          if (!restoredStepsRef.current) { setStep(MIN_STEP); setMaxStep(MIN_STEP); }
        }
      })
      .catch((error) => {
        console.error("Failed to load onboarding status in setup wizard", error);
      });
    return () => {
      active = false;
    };
  }, [navigate]);

  useEffect(() => {
    let active = true;
    apiGet("/api/setup/state")
      .then((state) => {
        if (!active || typeof window === "undefined") return;
        const remaining = resumeSetupSteps(state?.restoreSteps);
        restoredStepsRef.current = remaining?.length ? remaining : null;
        if (remaining?.length) {
          setRestoreSteps(remaining); setStep(remaining[0]); setMaxStep(MAX_STEP); setPath("install");
          window.localStorage.removeItem("feedarr:setupRestore");
        }
        const prevHasJackett = window.localStorage.getItem(STORAGE_HAS_JACKETT_SOURCE);
        const hasLocalConfig = JACKETT_STORAGE_KEYS.some((key) => {
          const value = window.localStorage.getItem(key);
          return value !== null && value !== "";
        });
        const hasIndexerSource = !!state?.hasJackettSource || !!state?.hasProwlarrSource;
        const authRequired = !!state?.authRequired;
        const authConfigured = !!state?.authConfigured;
        const shouldClear = !hasIndexerSource && (prevHasJackett === "true" || hasLocalConfig);
        window.localStorage.setItem(
          STORAGE_HAS_JACKETT_SOURCE,
          state?.hasJackettSource ? "true" : "false"
        );
        setSecurityStepStatus((prev) => ({
          ...prev,
          authRequired,
          authConfigured,
          ready: !authRequired || authConfigured || prev.authMode === "open",
        }));
        if (shouldClear && !remaining?.length) {
          JACKETT_STORAGE_KEYS.forEach((key) => window.localStorage.removeItem(key));
          window.localStorage.removeItem(STORAGE_KEY);
          setJackettStatus({ provider: "", ready: false, baseUrl: "", manualOnly: false });
          setJackettHasSources(false);
          setStep(MIN_STEP);
          setMaxStep(MIN_STEP);
          setJackettResetToken(Date.now());
        }
      })
      .catch((error) => {
        console.error("Failed to load setup state in setup wizard", error);
      });
    return () => {
      active = false;
    };
  }, []);

  const finish = useCallback(async () => {
    if (finishing) return;
    setFinishing(true);
    try {
      if (restoreSteps) {
        await configurationRequest(`${backupRoot(true)}/complete`, { method: "POST", bootstrap: completionBootstrap });
        setCompletionBootstrap("");
      } else await apiPost("/api/system/onboarding/complete");
      if (typeof window !== "undefined") {
        window.localStorage.removeItem(STORAGE_KEY);
        window.localStorage.removeItem("feedarr:setupPath");
        window.localStorage.removeItem("feedarr:setupRestore");
        window.localStorage.setItem(ONBOARDING_FLAG, "true");
      }
      navigate("/library", { replace: true });
    } catch (error) {
      setRestoreError(error?.message || "Impossible de terminer la configuration.");
      setFinishing(false);
    }
  }, [finishing, navigate, restoreSteps, completionBootstrap]);

  const steps = [
    {
      id: 1,
      title: tr("Langue", "Language"),
      content: <Step0Language onStatusChange={setLanguageStepStatus} />,
    },
    { id: 2, title: tr("Intro", "Intro"), content: <Step1Intro /> },
    {
      id: 3,
      title: tr("Securite", "Security"),
      content: (
        <Step2Security
          required={securityStepStatus.authRequired && !securityStepStatus.authConfigured}
          onStatusChange={setSecurityStepStatus}
          saveRef={securitySaveRef}
        />
      ),
    },
    {
      id: 4,
      title: tr("Metadonnees", "Metadata"),
      content: (
        <Step2Providers
          validation={providersValidation}
          onValidationChange={setProvidersValidation}
          onAllProvidersAddedChange={setProvidersAllAdded}
        />
      ),
    },
    {
      id: 5,
      title: tr("Fournisseurs RSS", "RSS providers"),
      content: (
        <Step3JackettConn
          onStatusChange={setJackettStatus}
          resetToken={jackettResetToken}
          initialStatus={jackettStatus}
        />
      ),
    },
    {
      id: 6,
      title: tr("Indexeurs", "Indexers"),
      content: (
        <Step31JackettIndexers
          onHasSourcesChange={setJackettHasSources}
          onBack={() => goStep(5)}
          jackettConfig={jackettStatus}
        />
      ),
    },
    { id: 7, title: tr("Applications", "Applications"), content: <Step4ArrApps /> },
    { id: 8, title: tr("Resume", "Summary"), content: <Step5Summary onFinish={finish} finishing={finishing} /> },
  ];

  const providersOk = useMemo(
    () => Object.values(providersValidation || {}).some((v) => v === "ok"),
    [providersValidation]
  );

  const visibleSteps = restoreSteps ? steps.filter(s => restoreSteps.includes(s.id)) : steps;
  const restoreStages = ["Importer", "Vérifier", "Restaurer", "Finaliser"].map((title, i) => ({ id: i + 1, title }));
  const current = steps.find(s => s.id === step);
  const allowSkip = step === 4 || step === 7;
  const isJackettReady = !!jackettStatus?.ready;
  const canSkip = step === 4 ? providersOk : true;
  const showSkip = allowSkip && !(step === 4 && providersAllAdded);

  function goStep(next) {
    if (!restoreSteps) { setStep(clampStep(next)); return; }
    const candidates = visibleSteps.filter(s => next < step ? s.id <= next : s.id >= next);
    setStep((next < step ? candidates.at(-1) : candidates[0])?.id ?? step);
  }

  if (!path) return <div className="setup-container">
    <div className="setup-card setupWizardFrame"><div className="setupWizardBody"><div className="setup-card__content setup-step-group">
      <div className="setup-step"><h2>Bienvenue dans Feedarr</h2><p className="muted">Choisissez comment configurer votre instance.</p></div>
      <div className="indexer-list">
        <button className="indexer-card setup-choice" type="button" onClick={() => chooseRestore(false)} aria-labelledby="setup-new-title" aria-describedby="setup-new-description">
          <span className="indexer-title" id="setup-new-title">Nouvelle installation</span>
          <span className="settings-help" id="setup-new-description">Configurez Feedarr pas à pas, en commençant par la langue.</span>
        </button>
        <button className="indexer-card setup-choice" type="button" onClick={() => chooseRestore(true)} aria-labelledby="setup-restore-title" aria-describedby="setup-restore-description">
          <span className="indexer-title" id="setup-restore-title">Restaurer une configuration</span>
          <span className="settings-help" id="setup-restore-description">Importez une sauvegarde, vérifiez son contenu, puis restaurez votre configuration.</span>
        </button>
      </div>
    </div></div></div>
  </div>;

  return (
    <div className="setup-container">
      <div className="setup-stepper">
        {(restoreMode ? restoreStages : visibleSteps).map((s) => {
          const isActive = s.id === (restoreMode ? restore.stage : step);
          const isDone = restoreMode ? s.id < restore.stage : s.id <= maxStep;
          const canClick = restoreMode ? restore.canVisit(s.id) : s.id <= maxStep;
          return (
            <button
              key={s.id}
              type="button"
              className={`setup-stepper-item${isActive ? " is-active" : ""}${isDone ? " is-done" : ""}`}
              onClick={() => canClick && (restoreMode ? restore.go(s.id) : goStep(s.id))}
              disabled={!canClick}
              aria-current={isActive ? "step" : undefined}
            >
              <span className="setup-stepper-num">{s.id}</span>
              <span className="setup-stepper-label">{s.title}</span>
            </button>
          );
        })}
      </div>

      <div className="setup-card setupWizardFrame">
        <div className="setupWizardBody">
          <div className="setup-card__content setup-step-group">
            {restoreMode ? <StepConfigurationRestore restore={restore} /> : current?.content}
            {!!restoreSteps && <InlineNotice variant="info" message="La configuration restaurée est déjà appliquée. Terminez uniquement les étapes manquantes ; le retour au choix est indisponible." />}
            {restoreError && <div className="onboarding__error" role="alert">{restoreError}</div>}
            {!!restoreSteps && step === MAX_STEP && <div className="indexer-card"><div className="indexer-row indexer-row--settings">
              <label className="indexer-title" htmlFor="completion-bootstrap">Secret bootstrap</label>
              <div className="indexer-actions"><div className="fieldWrap"><input id="completion-bootstrap" type="password" autoComplete="off" value={completionBootstrap} onChange={e => setCompletionBootstrap(e.target.value)} disabled={finishing} /></div></div>
            </div><div className="settings-help">Uniquement si votre accès distant le demande pour terminer la restauration.</div></div>}
          </div>
        </div>

        {restoreMode ? <div className="setupWizardFooter">
          {returnFeedback && <div className="setup-return-feedback" id="setup-return-status"><InlineNotice variant={returnBlockReason ? "info" : "error"} message={returnFeedback} /></div>}
          <div className="setup-actions settings-card__actions">
            <div className="settings-card__actions">
              <button className="btn" type="button" disabled={restore.busy || restore.checking || !!restore.state?.confirmed || !!restore.state?.applied} aria-describedby={returnFeedback ? "setup-return-status" : undefined} onClick={returnToChoice}>Retour au choix</button>
            {restore.stage === 2 && <button className="btn" type="button" disabled={restore.busy} onClick={restore.cancel}>Précédent</button>}
            {restore.stage === 3 && !restore.state?.confirmed && <button className="btn" type="button" disabled={restore.busy} onClick={() => restore.go(2)}>Précédent</button>}
            </div>
            <div className="setup-actions-right" style={{ marginLeft: "auto" }}>
              {restore.stage === 1 && <button className="btn btn-accent" type="button" disabled={!restore.canPrepare} onClick={restore.prepare}>{restore.busy ? "Analyse en cours…" : "Analyser la sauvegarde"}</button>}
              {restore.stage === 2 && <button className="btn btn-accent" type="button" disabled={restore.busy || !restore.canContinue} onClick={() => restore.go(3)}>Suivant</button>}
              {restore.stage === 3 && <button className="btn btn-accent" type="button" disabled={restore.busy} onClick={restore.state?.confirmed ? restore.checkRestart : restore.confirm}>{restore.busy ? "Opération en cours…" : restore.state?.confirmed ? "Vérifier après redémarrage" : "Confirmer la restauration"}</button>}
              {restore.stage === 4 && <button className="btn btn-accent" type="button" disabled={restore.busy} onClick={restore.finish}>{restore.busy ? "Vérification…" : "Continuer vers Feedarr"}</button>}
            </div>
          </div>
        </div> : <div className="setupWizardFooter">
          {returnFeedback && <div className="setup-return-feedback" id="setup-return-status"><InlineNotice message={returnFeedback} /></div>}
          <div className="setup-actions settings-card__actions">
            <div className="settings-card__actions">
              <button className="btn" type="button" disabled={!!restoreSteps || finishing || languageStepStatus.saving || securityStepStatus.saving} aria-describedby={returnFeedback ? "setup-return-status" : undefined} onClick={returnToChoice}>Retour au choix</button>
            {step > visibleSteps[0]?.id && (
              <button
                className="btn"
                type="button"
                onClick={() => goStep(step - 1)}
              >
                {tr("Precedent", "Previous")}
              </button>
            )}

            </div>
            <div className="setup-actions-right" style={{ marginLeft: "auto" }}>
              {showSkip && (
                <button
                  className="btn"
                  type="button"
                  onClick={() => goStep(step + 1)}
                  disabled={!canSkip}
                >
                  {tr("Passer", "Skip")}
                </button>
              )}
              {step < MAX_STEP && step !== 3 && (
                <button
                  className="btn btn-accent"
                  type="button"
                  onClick={() => goStep(step + 1)}
                  disabled={
                    (step === 1 && (!languageStepStatus.ready || languageStepStatus.saving)) ||
                    (step === 4 && !providersOk) ||
                    (step === 5 && !isJackettReady) ||
                    (step === 6 && !jackettHasSources)
                  }
                >
                  {tr("Suivant", "Next")}
                </button>
              )}
              {step === 3 && (
                securityStepStatus.saved ? (
                  <button
                    className="btn btn-accent"
                    type="button"
                    onClick={() => goStep(step + 1)}
                  >
                    {tr("Suivant", "Next")}
                  </button>
                ) : (
                  <button
                    className="btn btn-accent"
                    type="button"
                    onClick={() => securitySaveRef.current?.()}
                    disabled={securityStepStatus.saving}
                  >
                    {securityStepStatus.saving
                      ? tr("Enregistrement...", "Saving...")
                      : tr("Enregistrer", "Save")}
                  </button>
                )
              )}
            </div>
          </div>
        </div>}
      </div>
      <Modal open={leaveOpen} title="Annuler l’analyse et revenir au choix ?" onClose={() => setLeaveOpen(false)} closeDisabled={restore.busy}>
        <p className="settings-help">Le plan de restauration préparé sera annulé. Aucune configuration ne sera appliquée et les phrases secrètes seront effacées.</p>
        <div className="formactions"><button className="btn" type="button" disabled={restore.busy} onClick={() => setLeaveOpen(false)}>Rester dans la restauration</button><button className="btn btn-danger" type="button" disabled={restore.busy} onClick={() => restore.leave(true, showWelcome)}>Annuler le plan et revenir au choix</button></div>
        {restore.error && <InlineNotice variant="error" message={restore.error} />}
      </Modal>
    </div>
  );
}
