'use strict';
// The Mac's terminal surface (native/macos/Sources/MightyClaude/AppStore+Terminals.swift:63-74): Ghostty's
// Afterglow on #202020 with #DEDCD8 text and an #E1AB91 cursor by night, its Alabaster on #FAF9F6 by day,
// 12pt mono, 10 of padding (terminal.css).
const themes = {
  dark: {background:'#202020',foreground:'#dedcd8',cursor:'#e1ab91',cursorAccent:'#202020',selectionBackground:'#303030',
    black:'#151515',red:'#ac4142',green:'#7e8e50',yellow:'#e5b567',blue:'#6c99bb',magenta:'#9f4e85',cyan:'#7dd6cf',white:'#d0d0d0',
    brightBlack:'#505050',brightRed:'#ac4142',brightGreen:'#7e8e50',brightYellow:'#e5b567',brightBlue:'#6c99bb',brightMagenta:'#9f4e85',brightCyan:'#7dd6cf',brightWhite:'#f5f5f5'},
  light: {background:'#faf9f6',foreground:'#000000',cursor:'#007acc',cursorAccent:'#faf9f6',selectionBackground:'#bfdbfe',
    black:'#000000',red:'#aa3731',green:'#448c27',yellow:'#cb9000',blue:'#325cc0',magenta:'#7a3e9d',cyan:'#0083b2',white:'#f7f7f7',
    brightBlack:'#777777',brightRed:'#f05050',brightGreen:'#60cb00',brightYellow:'#ffbc5d',brightBlue:'#007acc',brightMagenta:'#e64ce6',brightCyan:'#00aacb',brightWhite:'#f7f7f7'}
};
const terminal = new Terminal({fontFamily:'Cascadia Mono, Consolas, monospace',fontSize:12,scrollback:10000,convertEol:false,allowProposedApi:false,theme:themes.dark,windowsPty:{backend:'conpty',buildNumber:19041}});
const fit = new FitAddon.FitAddon();
terminal.loadAddon(fit);
terminal.open(document.getElementById('terminal'));
const send = message => window.chrome.webview.postMessage(message);
terminal.onData(text => send({type:'input',text}));
terminal.onResize(size => send({type:'resize',columns:size.cols,rows:size.rows}));
terminal.onTitleChange(title => send({type:'title',title:title.slice(0,200)}));
terminal.parser.registerOscHandler(7,url => { if(url.length<=8192) send({type:'directory',url}); return true; });
// A press here makes this pane the active one, as a press anywhere else in a pane does.
document.addEventListener('pointerdown',() => send({type:'pressed'}),true);
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
  if(msg.type==='reset') { terminal.reset(); fit.fit(); }
  if(msg.type==='theme') {
    terminal.options.theme = msg.light ? themes.light : themes.dark;
    // The padding round the terminal is the page's own, so every layer under it takes the surface colour.
    for (const layer of [document.documentElement, document.body, document.getElementById('terminal')]) layer.style.background=terminal.options.theme.background;
  }
});
new ResizeObserver(()=>{if(document.body.clientWidth>20 && document.body.clientHeight>20) fit.fit();}).observe(document.body);
fit.fit();send({type:'ready',columns:terminal.cols,rows:terminal.rows});
