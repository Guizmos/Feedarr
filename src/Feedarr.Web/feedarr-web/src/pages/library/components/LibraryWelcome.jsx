import React from "react";
import { Link } from "react-router-dom";

export default function LibraryWelcome() {
  return <section className="settings-card library-welcome">
    <h2>Bienvenue dans Feedarr</h2>
    <p className="muted">Aucune source n’est configurée. Ajoutez une source ou restaurez votre configuration pour remplir la bibliothèque.</p>
    <div className="settings-card__actions">
      <Link className="btn btn-accent" to="/indexers">Ajouter une source</Link>
      <Link className="btn" to="/settings/backup">Restaurer une sauvegarde</Link>
    </div>
  </section>;
}
