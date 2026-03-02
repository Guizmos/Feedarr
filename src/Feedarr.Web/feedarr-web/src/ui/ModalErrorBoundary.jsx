import { Component } from "react";

/**
 * Error boundary for modal content.
 * Catches render errors thrown by modal children and shows a minimal fallback
 * instead of crashing the whole application.
 * Resets automatically when `resetKey` changes (e.g., when the modal reopens).
 */
export default class ModalErrorBoundary extends Component {
  constructor(props) {
    super(props);
    this.state = { hasError: false };
  }

  static getDerivedStateFromError() {
    return { hasError: true };
  }

  componentDidUpdate(prevProps) {
    if (prevProps.resetKey !== this.props.resetKey && this.state.hasError) {
      this.setState({ hasError: false });
    }
  }

  render() {
    if (this.state.hasError) {
      return (
        <div style={{ padding: 16 }}>
          <div className="errorbox">
            <div className="errorbox__title">Erreur d'affichage</div>
            <div className="muted">Une erreur est survenue dans cette fenêtre.</div>
          </div>
        </div>
      );
    }
    return this.props.children;
  }
}
