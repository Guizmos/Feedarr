import { useCallback, useEffect, useRef, useState } from "react";
import { apiGet } from "../../api/client.js";
import { backupRoot, configurationRequest, configurationUpload } from "../backup/configurationBackupApi.js";

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
  const active = useRef(null);
  const sequence = useRef(0);
  const request = useCallback((path, options = {}) => configurationRequest(`${root}${path}`, { bootstrap, ...options }), [bootstrap]);
  const acceptState = useCallback(next => {
    setState(next);
    if (next?.pending) setStage(next.confirmed ? 3 : 2);
    else if (next?.applied) setStage(4);
  }, []);
  useEffect(() => {
    if (!enabled) return;
    let alive = true;
    const current = ++sequence.current;
    const controller = new AbortController();
    request("/state", { signal: controller.signal }).then(next => {
      if (alive && !active.current && current === sequence.current) { acceptState(next); setError(""); }
    }).catch(e => { if (alive && !controller.signal.aborted && current === sequence.current) setError(e.message || "Impossible de charger la restauration."); });
    return () => { alive = false; controller.abort(); };
  }, [enabled, request, acceptState]);
  useEffect(() => () => active.current?.abort(), []);
  const run = async action => {
    if (active.current) return;
    const controller = new AbortController(); active.current = controller; sequence.current++;
    setBusy(true); setError(""); setNotice("");
    try { await action(controller.signal); }
    catch (e) { if (!controller.signal.aborted) setError(e.message || "La restauration a échoué."); }
    finally { active.current = null; setBusy(false); }
  };
  const prepare = () => run(async signal => {
    const body = configurationUpload(file, phrase); setPhrase("");
    const pending = await request("/prepare", { method: "POST", body, signal });
    setState({ pending, confirmed: false }); setConsent(false); setSecurityConsent(false); setStage(2);
  });
  const cancel = () => run(async signal => {
    await request(`/${state.pending.operationId}`, { method: "DELETE", signal });
    setState(null); setConsent(false); setSecurityConsent(false); setStage(1);
    setNotice("Analyse annulée. Vous pouvez sélectionner une autre archive.");
  });
  const confirm = () => run(async signal => {
    if (!consent || (state.pending.securityChangeRequiresConfirmation && !securityConsent)) throw new Error("Confirmez le remplacement de la configuration et les changements de sécurité demandés.");
    acceptState(await request(`/${state.pending.operationId}/confirm`, { method: "POST", body: { confirm: consent, securityChange: securityConsent }, signal }));
  });
  const checkRestart = () => run(async signal => {
    const setup = await apiGet("/api/setup/state", { signal });
    if (setup.onboardingDone) { setState({ applied: {} }); setStage(4); setNotice("Configuration appliquée. Feedarr est prêt."); return; }
    const next = await request("/state", { signal }); acceptState(next);
    if (next?.applied && !next.pending) setNotice("Configuration appliquée. La bibliothèque a été conservée.");
    else setNotice("Le redémarrage n’est pas encore terminé. Redémarrez le conteneur Feedarr puis réessayez.");
  });
  const finish = () => run(async signal => {
    const setup = await apiGet("/api/setup/state", { signal });
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
  const reset = () => { sequence.current++; setFile(null); setPhrase(""); setBootstrap(""); setState(null); setConsent(false); setSecurityConsent(false); setError(""); setNotice(""); setStage(1); };
  return { stage, state, file, setFile, phrase, setPhrase, bootstrap, setBootstrap, consent, setConsent, securityConsent, setSecurityConsent,
    busy, error, notice, prepare, cancel, confirm, checkRestart, finish, go, canVisit, reset,
    locked: busy || !!state?.pending || !!state?.applied,
    canContinue: stage !== 2 || consent && (!state?.pending?.securityChangeRequiresConfirmation || securityConsent) };
}
