import React from "react";
import { Link } from "react-router-dom";
import AppIcon from "../../../ui/AppIcon.jsx";

export default function LibraryWelcome() {
  return <section className="settings-card library-welcome" aria-labelledby="library-welcome-title">
    <span className="library-welcome__icon" aria-hidden="true"><AppIcon name="video_library" /></span>
    <div className="library-welcome__content">
      <h2 id="library-welcome-title">Bienvenue dans Feedarr</h2>
      <p className="muted">Aucune source n’est configurée. Ajoutez une source ou restaurez votre configuration pour remplir la bibliothèque.</p>
      <div className="settings-card__actions library-welcome__actions">
        <Link className="btn btn-accent" to="/indexers">Ajouter une source</Link>
        <Link className="btn" to="/settings/backup">Restaurer une sauvegarde</Link>
      </div>
    </div>
  </section>;
}
