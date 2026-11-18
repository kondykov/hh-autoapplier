// Тема панели (#123, концепт docs/design/concepts/extension.html): тёмный фон с точечной сеткой, акцент — розовый.
// Все правила — внутри .ap-root: панель на странице сайта живёт в shadow root и стилей страницы не видит,
// а боковая панель и страница настроек подключают те же переменные

export const THEME_VARS = `
--ap-bg:#0d1117;--ap-card:#161b22;--ap-deep:#0a0e14;--ap-line:#263040;--ap-grid:#1a222d;
--ap-text:#e6edf3;--ap-muted:#7d8a99;--ap-accent:#f0a3a8;--ap-accent-ink:#2a0f12;--ap-ok:#7ee2a8;--ap-warn:#f2c66d;--ap-err:#ff7b72;
--ap-mono:"Cascadia Mono",Consolas,"SF Mono",Menlo,monospace;--ap-sans:"Segoe UI",system-ui,-apple-system,sans-serif`;

export const PANEL_CSS = `
.ap-root{${THEME_VARS};box-sizing:border-box;color:var(--ap-text);font:13px/1.45 var(--ap-sans);background:var(--ap-bg);
  background-image:radial-gradient(var(--ap-grid) 1.2px,transparent 1.2px);background-size:18px 18px;display:flex;flex-direction:column;min-height:0}
.ap-root *{box-sizing:border-box}
.ap-root [hidden]{display:none!important}
.ap-hd{display:flex;align-items:center;gap:8px;padding:10px 12px;border-bottom:1px solid var(--ap-line)}
.ap-mark{font:700 13px var(--ap-mono);background:var(--ap-bg);border:1px solid var(--ap-line);border-radius:6px;padding:2px 6px;white-space:nowrap}
.ap-mark b{color:var(--ap-accent)}
.ap-title{font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.ap-ver{font:11px var(--ap-mono);color:var(--ap-muted)}
.ap-spacer{flex:1}
.ap-link{display:inline-flex;white-space:nowrap;flex:none;align-items:center;gap:6px;background:none;border:0;color:var(--ap-muted);font:12px var(--ap-mono);cursor:pointer;padding:2px 4px;border-radius:6px}
.ap-link:hover{color:var(--ap-text);background:var(--ap-card)}
.ap-dot{width:8px;height:8px;border-radius:50%;background:var(--ap-muted);display:inline-block;flex:none}
.ap-dot.ok{background:var(--ap-ok)}.ap-dot.warn{background:var(--ap-warn)}.ap-dot.error{background:var(--ap-err)}.ap-dot.checking{background:var(--ap-muted);animation:ap-blink 1s infinite}
.ap-modes{display:flex;gap:4px;padding:8px 12px;border-bottom:1px solid var(--ap-line)}
.ap-modes button{flex:1;background:none;border:1px solid transparent;color:var(--ap-muted);border-radius:8px;padding:4px 6px;font:12px var(--ap-sans);cursor:pointer}
.ap-modes button:hover{color:var(--ap-text)}
.ap-modes button[aria-pressed="true"]{background:var(--ap-card);border-color:var(--ap-line);color:var(--ap-text)}
.ap-body{overflow-y:auto;flex:1;min-height:0}
.ap-sec{padding:12px;border-bottom:1px solid var(--ap-line)}
.ap-sec h4{margin:0 0 8px;font:600 11px var(--ap-mono);color:var(--ap-muted);letter-spacing:.06em;text-transform:uppercase}
.ap-hint{color:var(--ap-muted);font-size:12px;margin-top:6px}
.ap-btn{display:flex;align-items:center;justify-content:center;width:100%;min-height:36px;padding:6px 12px;border-radius:8px;border:1px solid var(--ap-line);
  background:var(--ap-card);color:var(--ap-text);font:600 13px var(--ap-sans);cursor:pointer;text-align:center}
.ap-btn:hover{border-color:var(--ap-muted)}
.ap-btn:disabled{opacity:.45;cursor:not-allowed}
.ap-btn.primary{background:var(--ap-accent);border-color:var(--ap-accent);color:var(--ap-accent-ink)}
.ap-btn.primary:hover{filter:brightness(1.05)}
.ap-btn.warn{background:none;border-color:var(--ap-warn);color:var(--ap-warn)}
.ap-row{display:flex;gap:8px;margin-top:8px}.ap-row .ap-btn{flex:1}
.ap-stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(68px,1fr));gap:8px;margin-bottom:10px}
.ap-stat{background:var(--ap-card);border:1px solid var(--ap-line);border-radius:8px;padding:6px 8px;min-width:0}
.ap-stat b{display:block;font:700 17px var(--ap-mono);white-space:nowrap}
.ap-stat span{font-size:11px;color:var(--ap-muted)}
.ap-check{display:flex;align-items:center;gap:8px;font-size:12px;color:var(--ap-muted);margin-top:8px;cursor:pointer}
.ap-check input{accent-color:var(--ap-accent);width:14px;height:14px;margin:0}
.ap-banner{margin:10px 12px 0;padding:8px 10px;border-radius:8px;border:1px solid var(--ap-accent);color:var(--ap-accent);font-weight:600;text-align:center;animation:ap-blink 1.2s infinite}
.ap-confirm{margin:10px 12px 0;padding:10px;border-radius:8px;border:1px solid var(--ap-warn);background:var(--ap-card)}
.ap-step{padding:8px 12px;color:var(--ap-muted);font-size:12px;white-space:pre-line;border-bottom:1px solid var(--ap-line)}
.ap-step:empty{display:none}
.ap-old summary,.ap-log summary{cursor:pointer;color:var(--ap-muted);font:600 11px var(--ap-mono);letter-spacing:.06em;text-transform:uppercase;list-style-position:inside}
.ap-old[open] summary{margin-bottom:8px}
.ap-log summary{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.ap-log-last{font:12px var(--ap-mono);text-transform:none;letter-spacing:0;margin-left:6px}
.ap-log[open] .ap-log-last{display:none}
.ap-log{background:var(--ap-deep);padding:8px 12px;border-top:1px solid var(--ap-line)}
.ap-log-lines{font:12px/1.55 var(--ap-mono);color:var(--ap-muted);max-height:180px;overflow-y:auto;margin-top:6px;word-break:break-word}
.ap-log-lines .info{color:var(--ap-muted)}.ap-log-lines .success{color:var(--ap-ok)}.ap-log-lines .warn{color:var(--ap-warn)}
.ap-log-lines .error{color:var(--ap-err)}.ap-log-lines .step{color:var(--ap-accent)}
.ap-log-lines time{color:#56606d;margin-right:6px}
@keyframes ap-blink{50%{opacity:.55}}
`;
