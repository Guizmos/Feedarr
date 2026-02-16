import React, { useCallback, useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { apiGet, apiPost } from "../api/client.js";
import Step1Intro from "../components/setup/Step1Intro.jsx";
import Step2Providers from "../components/setup/Step2Providers.jsx";
import Step3JackettConn from "../components/setup/Step3JackettConn.jsx";
import Step31JackettIndexers from "../components/setup/Step31JackettIndexers.jsx";
import Step4ArrApps from "../components/setup/Step4ArrApps.jsx";
import Step5Summary from "../components/setup/Step5Summary.jsx";

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
const MAX_STEP = 6;

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
  const [providersValidation, setProvidersValidation] = useState({
    tmdb: null,
    tvmaze: null,
    fanart: null,
    igdb: null,
  });
  const [providersAllAdded, setProvidersAllAdded] = useState(false);
  const [jackettStatus, setJackettStatus] = useState({
    provider: "",
    ready: false,
    baseUrl: "",
    manualOnly: false,
  });
  const [jackettHasSources, setJackettHasSources] = useState(false);
  const [jackettResetToken, setJackettResetToken] = useState(0);

  useEffect(() => {
    setMaxStep((prev) => Math.max(prev, step));
    if (typeof window !== "undefined") {
      window.localStorage.setItem(STORAGE_KEY, String(step));
    }
  }, [step]);

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
          setStep(MIN_STEP);
          setMaxStep(MIN_STEP);
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
        const prevHasJackett = window.localStorage.getItem(STORAGE_HAS_JACKETT_SOURCE);
        const hasLocalConfig = JACKETT_STORAGE_KEYS.some((key) => {
          const value = window.localStorage.getItem(key);
          return value !== null && value !== "";
        });
        const hasIndexerSource = !!state?.hasJackettSource || !!state?.hasProwlarrSource;
        const shouldClear = !hasIndexerSource && (prevHasJackett === "true" || hasLocalConfig);
        window.localStorage.setItem(
          STORAGE_HAS_JACKETT_SOURCE,
          state?.hasJackettSource ? "true" : "false"
        );
        if (shouldClear) {
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
      await apiPost("/api/system/onboarding/complete");
      if (typeof window !== "undefined") {
        window.localStorage.removeItem(STORAGE_KEY);
        window.localStorage.setItem(ONBOARDING_FLAG, "true");
      }
      navigate("/library", { replace: true });
    } catch (error) {
      console.error("Failed to complete onboarding from setup wizard", error);
      setFinishing(false);
    }
  }, [finishing, navigate]);

  const steps = useMemo(() => ([
    { id: 1, title: "Intro", content: <Step1Intro /> },
    {
      id: 2,
      title: "Providers",
      content: (
        <Step2Providers
          validation={providersValidation}
          onValidationChange={setProvidersValidation}
          onAllProvidersAddedChange={setProvidersAllAdded}
        />
      ),
    },
    {
      id: 3,
      title: "Fournisseurs",
      content: (
        <Step3JackettConn
          onStatusChange={setJackettStatus}
          resetToken={jackettResetToken}
          initialStatus={jackettStatus}
        />
      ),
    },
    {
      id: 4,
      title: "Indexeurs",
      content: (
        <Step31JackettIndexers
          onHasSourcesChange={setJackettHasSources}
          onBack={() => setStep(3)}
          jackettConfig={jackettStatus}
        />
      ),
    },
    { id: 5, title: "Applications", content: <Step4ArrApps /> },
    { id: 6, title: "Résumé", content: <Step5Summary onFinish={finish} finishing={finishing} /> },
  ]), [providersValidation, jackettResetToken, jackettStatus, finish, finishing]);

  const providersOk = useMemo(
    () => Object.values(providersValidation || {}).some((v) => v === "ok"),
    [providersValidation]
  );

  const current = steps[step - 1];
  const allowSkip = step === 2 || step === 5;
  const isJackettReady = !!jackettStatus?.ready;
  const canSkip = step === 2 ? providersOk : true;
  const showSkip = allowSkip && !(step === 2 && providersAllAdded);

  function goStep(next) {
    setStep(clampStep(next));
  }

  return (
    <div className="setup-container">
      <div className="setup-stepper">
        {steps.map((s) => {
          const isActive = s.id === step;
          const isDone = s.id <= maxStep;
          const canClick = s.id <= maxStep;
          return (
            <button
              key={s.id}
              type="button"
              className={`setup-stepper-item${isActive ? " is-active" : ""}${isDone ? " is-done" : ""}`}
              onClick={() => canClick && goStep(s.id)}
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
          <div className="setup-card__content">
            {current?.content}
          </div>
        </div>

        <div className="setupWizardFooter">
          <div className="setup-actions">
            <button
              className="btn"
              type="button"
              onClick={() => goStep(step - 1)}
              disabled={step <= MIN_STEP}
            >
              Precedent
            </button>

            <div className="setup-actions-right">
              {showSkip && (
                <button
                  className="btn"
                  type="button"
                  onClick={() => goStep(step + 1)}
                  disabled={!canSkip}
                >
                  Passer
                </button>
              )}
              {step < MAX_STEP && (
                <button
                  className="btn btn-accent"
                  type="button"
                  onClick={() => goStep(step + 1)}
                  disabled={
                    (step === 2 && !providersOk) ||
                    (step === 3 && !isJackettReady) ||
                    (step === 4 && !jackettHasSources)
                  }
                >
                  Suivant
                </button>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
