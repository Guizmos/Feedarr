import React, { useEffect } from "react";
import { useSubbarSetter } from "../../layout/useSubbar.js";
import SubAction from "../../ui/SubAction.jsx";
import Loader from "../../ui/Loader.jsx";
import SettingsUsers from "./SettingsUsers.jsx";
import useSecuritySettings from "./hooks/useSecuritySettings.js";

export default function SettingsSecurityPage() {
  const settings = useSecuritySettings();
  const setContent = useSubbarSetter();
  const { load, save, loading, canSave } = settings;
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    setContent(<div className="settings-subbar-content">
      <SubAction icon="refresh" label="Rafraîchir" onClick={load} disabled={loading} />
      <SubAction icon="save" label="Enregistrer" onClick={save} disabled={!canSave} />
    </div>);
    return () => setContent(null);
  }, [setContent, load, save, loading, canSave]);
  return <div className="page page--settings"><div className="pagehead"><h1>Sécurité</h1></div><div className="pagehead__divider" />
    {settings.error && <div className="errorbox" role="alert">{settings.error}</div>}
    {loading ? <Loader label="Chargement…" /> : <div className="settings-grid"><SettingsUsers security={settings.security} setSecurity={settings.setSecurity} /></div>}
  </div>;
}
