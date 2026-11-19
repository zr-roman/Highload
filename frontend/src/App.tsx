import { appConfig } from "./config";
import { CreatePaymentPanel } from "./components/CreatePaymentPanel";
import { MerchantBalancePanel } from "./components/MerchantBalancePanel";
import { PaymentLookupPanel } from "./components/PaymentLookupPanel";
import "./App.css";

function App() {
  return (
    <main className="app-shell">
      <header className="topbar">
        <a className="brand" href="#top" aria-label="Платёжная платформа, начало">
          <span className="brand-mark" aria-hidden="true">H</span>
          <span>Highload <small>PAYMENTS</small></span>
        </a>
        <span className="environment"><span />Учебный стенд</span>
      </header>

      <section className="hero" id="top">
        <p className="eyebrow">Операционная панель</p>
        <h1>Платёжная платформа</h1>
        <p className="app-description">Создавайте тестовые платежи, проверяйте их статус и смотрите баланс продавца.</p>
      </section>

      <div className="workspace">
        <CreatePaymentPanel />
        <PaymentLookupPanel />
        <MerchantBalancePanel />
      </div>

      <footer className="footer">
        <a href="/diagnostics">Диагностика Ledger</a>
        <span>Payments API <code>{appConfig.paymentsApiUrl || `через Vite → ${appConfig.apiProxyTarget}`}</code></span>
        <span>Ledger <code>{appConfig.ledgerApiUrl || `через Vite → ${appConfig.apiProxyTarget}`}</code></span>
      </footer>
    </main>
  );
}

export default App;
