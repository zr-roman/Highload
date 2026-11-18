import { appConfig } from "./config";
import "./App.css";

function App() {
  return (
    <main className="app-shell">
      <p className="eyebrow">Highload · учебный стенд</p>
      <h1>Платёжная платформа</h1>
      <p className="app-description">
        Основа интерфейса готова. Сценарии первой версии описаны в документации.
      </p>
      <dl className="api-list">
        <div>
          <dt>Payments API</dt>
          <dd>{appConfig.paymentsApiUrl}</dd>
        </div>
        <div>
          <dt>Ledger API</dt>
          <dd>{appConfig.ledgerApiUrl}</dd>
        </div>
      </dl>
    </main>
  );
}

export default App;
