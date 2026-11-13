import { useCallback, useEffect, useRef, useState } from "react";
import { backupRoot, configurationRequest, configurationUpload, configurationFileError } from "../backup/configurationBackupApi.js";

const root = backupRoot(true);

export default function useSetupRestore(enabled, onApplied) {
  const [stage, setStage] = useState(1);
  const [state, setState] = useState(null);
  const [file, setFile] = useState(null);
  const [phrase, setPhrase] = useState("");
  const [bootstrap, setBootstrap] = useState("");
  const [consent, setConsent] = useState(false);
  const [securityConsent, setSecurityConsent] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [checking, setChecking] = useState(false);
  const [analysisStatus, setAnalysisStatus] = useState("none");
  const [leaveError, setLeaveError] = useState("");
  const [resumeOnEntry, setResumeOnEntry] = useState(true);
  const localOnly = useRef(false);
  const active = useRef(null);
  const sequence = useRef(0);
  const verifiedEmpty = useRef(false);
  const request = useCallback((path, options = {}) => configurationRequest(`${root}${path}`, { bootstrap, ...options }), [bootstrap]);
  const acceptState = useCallback(next => {
    localOnly.current = false;
    verifiedEmpty.current = !!next && !next.pending && !next.confirmed && !next.applied;
    setState(next);
    if (next?.pending) { setStage(next.confirmed ? 3 : 2); setAnalysisStatus("valid"); }
    else if (next?.applied) setStage(4);
    else setStage(1);
  }, []);
  useEffect(() => {
    if (!enabled || !resumeOnEntry) return;
    let alive = true;
    setChecking(true);
    const current = ++sequence.current;
    const controller = new AbortController();
    request("/state", { signal: controller.signal }).then(next => {
      if (alive && !active.current && current === sequence.current) { acceptState(next); }
    }).catch(e => { if (alive && !controller.signal.aborted && current === sequence.current) setError(e.message || "Impossible de charger la restauration."); }).finally(() => { if (alive) setChecking(false); });
    return () => { alive = false; controller.abort(); };
  }, [enabled, resumeOnEntry, request, acceptState]);
  useEffect(() => () => active.current?.abort(), []);
  const run = async (action, kind) => {
    if (active.current) return;
    const controller = new AbortController(); active.current = controller; sequence.current++;
    setBusy(true); setError(""); setNotice("");
    if (kind === "leave") setLeaveError("");
    try { await action(controller.signal); }
    catch (e) { if (!controller.signal.aborted) { setError(e.message || "La restauration a échoué."); if (kind === "analysis") setAnalysisStatus("error"); if (kind === "leave") setLeaveError(e.message || "La vérification du plan a échoué."); } }
    finally { active.current = null; setBusy(false); }
  };
  const prepare = () => run(async signal => {
    const body = configurationUpload(file, phrase); setPhrase(""); setAnalysisStatus("analyzing");
    localOnly.current = false;
    verifiedEmpty.current = false;
    const pending = await request("/prepare", { method: "POST", body, signal });
    setState({ pending, confirmed: false }); setConsent(false); setSecurityConsent(false); setStage(2); setAnalysisStatus("valid");
  }, "analysis");
  const cancel = () => run(async signal => {
    await request(`/${state.pending.operationId}`, { method: "DELETE", signal });
    verifiedEmpty.current = true;
    setState(null); setConsent(false); setSecurityConsent(false); setStage(1);
    setAnalysisStatus(file ? "selected" : "none"); setNotice("Analyse annulée. Vous pouvez sélectionner une autre archive.");
  });
  const confirm = () => run(async signal => {
    if (!consent || (state.pending.securityChangeRequiresConfirmation && !securityConsent)) throw new Error("Confirmez le remplacement de la configuration et les changements de sécurité demandés.");
    acceptState(await request(`/${state.pending.operationId}/confirm`, { method: "POST", body: { confirm: consent, securityChange: securityConsent }, signal }));
  });
  const reloadState = () => run(async signal => {
    acceptState(await request("/state", { signal }));
  });
  const checkRestart = () => run(async signal => {
    const setup = await configurationRequest("/api/setup/state", { bootstrap, signal });
    if (setup.onboardingDone) { setState({ applied: {} }); setStage(4); setNotice("Configuration appliquée. Feedarr est prêt."); return; }
    const next = await request("/state", { signal }); acceptState(next);
    if (next?.applied && !next.pending) setNotice("Configuration appliquée. La bibliothèque a été conservée.");
    else setNotice("Le redémarrage n’est pas encore terminé. Redémarrez le conteneur Feedarr puis réessayez.");
  });
  const finish = () => run(async signal => {
    const setup = await configurationRequest("/api/setup/state", { bootstrap, signal });
    if (!setup.onboardingDone && !Array.isArray(setup.restoreSteps)) throw new Error("La restauration n’est pas encore appliquée. Vérifiez le redémarrage.");
    onApplied(setup, bootstrap); setBootstrap("");
  });
  const go = next => {
    if (busy) return;
    if (next === 1 && state?.pending && !state.confirmed) { void cancel(); return; }
    if (next === 2 && state?.pending && !state.confirmed) { setStage(2); return; }
    if (next === 3 && state?.pending && (state.confirmed || consent && (!state.pending.securityChangeRequiresConfirmation || securityConsent))) setStage(3);
    if (next === 4 && state?.applied && !state.pending) setStage(4);
  };
  const canVisit = id => !busy && (id === stage || id === 1 && !state?.confirmed && !state?.applied || id === 2 && state?.pending && !state.confirmed || id === 3 && state?.confirmed || id === 4 && state?.applied && !state.pending);
  const reset = () => { localOnly.current = false; setResumeOnEntry(true); verifiedEmpty.current = false; setChecking(false); setAnalysisStatus("none"); sequence.current++; setFile(null); setPhrase(""); setBootstrap(""); setLeaveError(""); setState(null); setConsent(false); setSecurityConsent(false); setError(""); setNotice(""); setStage(1); };
  // Only an explicit choice from the welcome screen starts a known local flow.
  // Reloaded/restored routes keep the default unknown state and its server check.
  const startFresh = () => {
    if (active.current || state?.confirmed || state?.applied || state?.pending) return;
    reset();
    localOnly.current = true;
    setResumeOnEntry(false);
  };
  const selectFile = next => {
    if (active.current || state?.confirmed || state?.applied) return;
    const update = () => {
      sequence.current++; setFile(next); setPhrase(""); setConsent(false); setSecurityConsent(false);
      setState(previous => ({ ...previous, pending: null, confirmed: false })); setStage(1); setNotice("");
      const invalid = next ? configurationFileError(next) : "";
      setError(invalid);
      setAnalysisStatus(invalid ? "error" : next ? "selected" : "none");
    };
    if (state?.pending) return run(async signal => {
      await request(`/${state.pending.operationId}`, { method: "DELETE", signal });
      verifiedEmpty.current = true;
      update();
    });
    update();
  };
  const leave = (cancelPending, onLeave) => run(async signal => {
    if (state?.confirmed || state?.applied) {
      throw new Error("La restauration est déjà confirmée ou appliquée. Poursuivez ce parcours pour la finaliser.");
    }
    if (localOnly.current && !state?.pending) { reset(); onLeave(); return; }
    // An already verified empty state needs no server mutation or second read
    // to leave. Unknown/prepared plans still require authoritative verification.
    if (verifiedEmpty.current && !state?.pending) { reset(); onLeave(); return; }
    const next = await request("/state", { signal });
    if (next?.confirmed || next?.applied) {
      acceptState(next);
      throw new Error("La restauration est déjà confirmée ou appliquée. Poursuivez ce parcours pour la finaliser.");
    }
    if (next?.pending) {
      if (!cancelPending) { acceptState(next); throw new Error("Une archive est préparée. Annulez explicitement son analyse avant de revenir au choix."); }
      await request(`/${next.pending.operationId}`, { method: "DELETE", signal });
    }
    reset(); onLeave();
  }, "leave");
  const validFile = !configurationFileError(file);
  return { stage, state, file, setFile: selectFile, phrase, setPhrase, bootstrap, setBootstrap, consent, setConsent, securityConsent, setSecurityConsent,
    busy, checking, analysisStatus, error, leaveError, notice, prepare, cancel, confirm, reloadState, checkRestart, finish, go, canVisit, reset, startFresh, leave,
    canPrepare: validFile && !busy && !checking && !state?.pending && !state?.confirmed && !state?.applied && !state?.legacyRestorePending,
    locked: busy || checking || !!state?.pending || !!state?.applied,
    canContinue: stage !== 2 || consent && (!state?.pending?.securityChangeRequiresConfirmation || securityConsent) };
}
