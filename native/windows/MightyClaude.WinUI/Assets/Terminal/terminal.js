'use strict';
const terminal = new Terminal({fontFamily:'Cascadia Mono, Consolas, monospace',fontSize:13,scrollback:10000,convertEol:false,allowProposedApi:false,theme:{background:'#181a1f',foreground:'#e2e5eb'},windowsPty:{backend:'conpty',buildNumber:19041}});
const fit = new FitAddon.FitAddon();
terminal.loadAddon(fit);
terminal.open(document.getElementById('terminal'));
const send = message => window.chrome.webview.postMessage(message);
terminal.onData(text => send({type:'input',text}));
terminal.onResize(size => send({type:'resize',columns:size.cols,rows:size.rows}));
terminal.attachCustomKeyEventHandler(event => {
  if (!event.ctrlKey || event.altKey || event.type !== 'keydown' || event.isComposing) return true;
  // Physical codes also work while the Korean keyboard layout is selected.
  if(event.code === 'KeyC' && (event.shiftKey || terminal.hasSelection())) {
    if(terminal.hasSelection()) send({type:'copy',text:terminal.getSelection()});
    event.preventDefault(); return false;
  }
  if(event.code === 'KeyV') { send({type:'paste'}); event.preventDefault(); return false; }
  return true;
});
window.chrome.webview.addEventListener('message',event => {
  const msg=event.data;
  if(msg.type==='output') terminal.write(msg.text);
  if(msg.type==='paste') terminal.paste(msg.text);
  if(msg.type==='focus') terminal.focus();
  if(msg.type==='theme') {
    terminal.options.theme = msg.light ? {background:'#f5f6f9',foreground:'#17191d'} : {background:'#181a1f',foreground:'#e2e5eb'};
    document.body.style.background=terminal.options.theme.background;
  }
});
new ResizeObserver(()=>{if(document.body.clientWidth>20 && document.body.clientHeight>20) fit.fit();}).observe(document.body);
fit.fit();send({type:'ready',columns:terminal.cols,rows:terminal.rows});
