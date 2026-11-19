import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import LedgerDiagnostics from './LedgerDiagnostics'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {window.location.pathname.replace(/\/$/, '') === '/diagnostics' ? <LedgerDiagnostics /> : <App />}
  </StrictMode>,
)
