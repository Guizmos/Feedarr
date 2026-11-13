import React, { useRef, useState, useSyncExternalStore } from "react";
import { appUrl, getSessionSnapshot, subscribeSession, login, createInitialAdmin } from "../auth/session.js";
import { tr } from "../app/uiText.js";
import HttpTransportNotice from "../layout/HttpTransportNotice.jsx";
import AppIcon from "../ui/AppIcon.jsx";

export default function Login({ createAdmin = false }) {
  const { state } = useSyncExternalStore(subscribeSession, getSessionSnapshot, getSessionSnapshot);
  const submitting = useRef(false);
  const [busy, setBusy] = useState(false);
  const [visible, setVisible] = useState(false);
  const [error, setError] = useState("");

  async function submit(event) {
    event.preventDefault();
    if (submitting.current) return;
    submitting.current = true;
    setBusy(true); setError("");
    const form = event.currentTarget;
    const fields = new FormData(form);
    try {
      if (createAdmin) {
        await createInitialAdmin(String(fields.get("username")), String(fields.get("password")), String(fields.get("confirmation")));
      } else await login(String(fields.get("username")), String(fields.get("password")), fields.get("rememberMe") === "on");
    } catch (failure) {
      const message = failure.code === "administrator_already_created" ? "Un administrateur existe déjà. Actualisez pour vous connecter."
        : failure.code === "restore_restart_required" ? "Une restauration est en attente. Redémarrez Feedarr avant de créer le compte."
        : failure.code === "password_complexity_required" ? "Utilisez au moins 12 caractères avec majuscule, minuscule, chiffre et symbole."
        : failure.code === "invalid_administrator_credentials" ? "Vérifiez l’identifiant et la confirmation du mot de passe."
        : failure.status === 401 ? tr("Identifiant ou mot de passe incorrect.", "Incorrect username or password.")
        : failure.status === 429 ? tr("Trop de tentatives. Patientez avant de réessayer.", "Too many attempts. Please wait before retrying.")
        : failure.code === "https_required" || failure.message === "https_required" ? tr("Cette instance impose HTTPS. Utilisez HTTPS ou la récupération sur le serveur.", "This instance requires HTTPS. Use HTTPS or server recovery.")
        : failure.status === 403 ? tr("Connexion refusée. Actualisez la page et vérifiez la configuration de sécurité.", "Sign-in denied. Reload and check the security configuration.")
        : failure.status === 409 ? tr("La configuration a changé. Actualisez la page.", "The configuration changed. Reload this page.")
        : tr("Le serveur est injoignable. Vérifiez votre connexion puis réessayez.", "Cannot reach the server. Check your connection and retry.");
      setError(message);
      form.elements.password.value = "";
    } finally { submitting.current = false; setBusy(false); }
  }

  const available = createAdmin || state?.webSessionAvailable;
  return <><HttpTransportNotice /><main className="auth-page">
    <section className="auth-card" aria-labelledby="login-title">
      <img className="auth-logo" src={appUrl("/icon-192.png?v=9d1d5be2bd5c")} alt="Feedarr" width="76" height="76" />
      <h1 id="login-title">{createAdmin ? tr("Créer votre compte administrateur", "Create your administrator account") : tr("Connexion à Feedarr", "Sign in to Feedarr")}</h1>
      <p className="auth-description">{createAdmin ? "Un compte pour accéder à Feedarr. Configurez ensuite vos sources et services dans l’ordre de votre choix." : tr("Retrouvez votre bibliothèque, en toute simplicité.", "Your library, just a sign-in away.")}</p>
      <form method="post" action={appUrl(createAdmin ? "/api/auth/create-admin" : "/api/auth/login")} onSubmit={submit} aria-busy={busy}>
        <label htmlFor="login-username">{tr("Identifiant", "Username")}</label>
        <input id="login-username" name="username" autoComplete="username" autoCapitalize="none" spellCheck="false" required disabled={busy} />
        <label htmlFor="login-password">{tr("Mot de passe", "Password")}</label>
        <div className="auth-password">
          <input id="login-password" name="password" type={visible ? "text" : "password"} autoComplete={createAdmin ? "new-password" : "current-password"} required disabled={busy} />
          <button type="button" className="auth-reveal" onClick={() => setVisible(!visible)} aria-label={visible ? tr("Masquer le mot de passe", "Hide password") : tr("Afficher le mot de passe", "Show password")} aria-pressed={visible} disabled={busy}><AppIcon name={visible ? "visibility_off" : "visibility"} /></button>
        </div>
        {createAdmin && <>
          <label htmlFor="admin-confirmation">Confirmer le mot de passe</label>
          <input id="admin-confirmation" name="confirmation" type="password" autoComplete="new-password" required disabled={busy} />
          <p className="muted">Au moins 12 caractères, avec majuscule, minuscule, chiffre et symbole.</p>
        </>}
        {!createAdmin && <label className="auth-remember"><input name="rememberMe" type="checkbox" disabled={busy} />{tr("Rester connecté", "Stay signed in")}</label>}
        {!available && <p className="auth-error" role="alert">{createAdmin
          ? "Cette instance impose HTTPS pour créer le compte. Utilisez HTTPS ou la récupération sur le serveur."
          : !state?.authConfigured ? "Les identifiants de cette instance sont absents ou incomplets. Rétablissez une sauvegarde valide ou utilisez la récupération administrateur locale."
          : tr("Cette instance impose HTTPS pour les accès privés. Utilisez HTTPS ou la récupération sur le serveur.", "This instance requires HTTPS for private access. Use HTTPS or server recovery.")}</p>}
        {error && <p className="auth-error" role="alert">{error}</p>}
        <button className="btn btn-accent auth-submit" type="submit" disabled={busy || !available}>{busy ? tr("Connexion…", "Signing in…") : createAdmin ? "Créer le compte" : tr("Se connecter", "Sign in")}</button>
      </form>
    </section>
  </main></>;
}
