import React, { useRef, useState, useSyncExternalStore } from "react";
import { Navigate, useSearchParams } from "react-router-dom";
import { appUrl, getSessionSnapshot, subscribeSession, login, safeDestination, sessionRoute } from "../auth/session.js";
import { tr } from "../app/uiText.js";
import AppIcon from "../ui/AppIcon.jsx";

export default function Login() {
  const { state } = useSyncExternalStore(subscribeSession, getSessionSnapshot, getSessionSnapshot);
  const [params] = useSearchParams();
  const submitting = useRef(false);
  const [busy, setBusy] = useState(false);
  const [visible, setVisible] = useState(false);
  const [error, setError] = useState("");
  const destination = safeDestination(params.get("returnTo"));
  if (sessionRoute(state) === "app") return <Navigate replace to={destination} />;

  async function submit(event) {
    event.preventDefault();
    if (submitting.current) return;
    submitting.current = true;
    setBusy(true); setError("");
    const form = event.currentTarget;
    const fields = new FormData(form);
    try {
      const next = await login(String(fields.get("username")), String(fields.get("password")), fields.get("rememberMe") === "on");
      window.location.replace(appUrl(next.setupCompleted ? destination : "/setup"));
    } catch (failure) {
      const message = failure.status === 401 ? tr("Identifiant ou mot de passe incorrect.", "Incorrect username or password.")
        : failure.status === 429 ? tr("Trop de tentatives. Patientez avant de réessayer.", "Too many attempts. Please wait before retrying.")
        : failure.message === "https_required" || !state?.webSessionAvailable ? tr("Une connexion HTTPS est nécessaire pour ouvrir une session sécurisée.", "HTTPS is required to create a secure session.")
        : failure.status === 403 ? tr("Connexion refusée. Actualisez la page et vérifiez la configuration de sécurité.", "Sign-in denied. Reload and check the security configuration.")
        : failure.status === 409 ? tr("La configuration a changé. Actualisez la page.", "The configuration changed. Reload this page.")
        : tr("Le serveur est injoignable. Vérifiez votre connexion puis réessayez.", "Cannot reach the server. Check your connection and retry.");
      setError(message);
      form.elements.password.value = "";
    } finally { submitting.current = false; setBusy(false); }
  }

  return <main className="auth-page">
    <section className="auth-card" aria-labelledby="login-title">
      <img className="auth-logo" src={`${import.meta.env.BASE_URL}icon-192.png?v=9d1d5be2bd5c`} alt="Feedarr" width="76" height="76" />
      <h1 id="login-title">{tr("Connexion à Feedarr", "Sign in to Feedarr")}</h1>
      <p className="auth-description">{tr("Retrouvez votre bibliothèque, en toute simplicité.", "Your library, just a sign-in away.")}</p>
      <form method="post" action={appUrl("/api/auth/login")} onSubmit={submit} aria-busy={busy}>
        <label htmlFor="login-username">{tr("Identifiant", "Username")}</label>
        <input id="login-username" name="username" autoComplete="username" autoCapitalize="none" spellCheck="false" required disabled={busy} />
        <label htmlFor="login-password">{tr("Mot de passe", "Password")}</label>
        <div className="auth-password">
          <input id="login-password" name="password" type={visible ? "text" : "password"} autoComplete="current-password" required disabled={busy} />
          <button type="button" className="auth-reveal" onClick={() => setVisible(!visible)} aria-label={visible ? tr("Masquer le mot de passe", "Hide password") : tr("Afficher le mot de passe", "Show password")} aria-pressed={visible} disabled={busy}><AppIcon name={visible ? "visibility_off" : "visibility"} /></button>
        </div>
        <label className="auth-remember"><input name="rememberMe" type="checkbox" disabled={busy} />{tr("Rester connecté", "Stay signed in")}</label>
        {!state?.webSessionAvailable && <p className="auth-error" role="alert">{tr("Cette instance nécessite HTTPS pour la connexion web. L’accès API Basic reste disponible.", "This instance requires HTTPS for web sign-in. Basic API access remains available.")}</p>}
        {error && <p className="auth-error" role="alert">{error}</p>}
        <button className="btn btn-accent auth-submit" type="submit" disabled={busy || !state?.webSessionAvailable}>{busy ? tr("Connexion…", "Signing in…") : tr("Se connecter", "Sign in")}</button>
      </form>
    </section>
  </main>;
}
