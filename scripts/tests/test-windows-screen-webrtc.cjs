const { chromium } = require(process.env.MIGHTY_PLAYWRIGHT_MODULE || 'playwright-core');
(async () => {
 const browser = await chromium.launch({executablePath:process.env.MIGHTY_CHROMIUM_EXECUTABLE || (process.platform === 'darwin' ? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome' : undefined),headless:true,args:['--no-first-run','--no-default-browser-check']});
 try {
 const page = await browser.newPage();
 await page.goto('about:blank');
 await page.evaluate(() => {
   window.messages=[]; window.listener=null; window.phone=new RTCPeerConnection({iceServers:[]}); window.receivedStreams=[];window.receivedData=[]; window.phoneIce=[];window.hostIce=[];
   window.command=(type,fields={})=>window.listener({data:{type,id:Math.floor(Math.random()*1000000)+1,...fields}});
   window.phone.ontrack=e=>window.receivedStreams.push(e.streams[0].id);
   window.phone.ondatachannel=e=>{window.phoneChannel=e.channel;e.channel.onmessage=m=>window.receivedData.push(JSON.parse(m.data));};
   window.phone.onicecandidate=e=>window.command('signal',{sessionId:'fixture',signal:{type:'screen-ice',...(e.candidate?e.candidate.toJSON():{candidate:''})}});
   window.chrome={webview:{addEventListener:(name,fn)=>window.listener=fn,postMessage:async value=>{
     window.messages.push(value);
     if(value.type==='signal'&&value.signal.type==='screen-offer'){
       await window.phone.setRemoteDescription({type:'offer',sdp:value.signal.sdp});
       const answer=await window.phone.createAnswer(); await window.phone.setLocalDescription(answer);
       await window.command('signal',{sessionId:'fixture',signal:{type:'screen-answer',sdp:answer.sdp}});
       for(const ice of window.hostIce.splice(0))await window.phone.addIceCandidate(ice);
     }else if(value.type==='signal'&&value.signal.type==='screen-ice'){
       const s=value.signal;const ice=s.candidate?{candidate:s.candidate,sdpMid:s.sdpMid,sdpMLineIndex:s.sdpMLineIndex}:null;
       if(window.phone.remoteDescription)await window.phone.addIceCandidate(ice); else window.hostIce.push(ice);
     }
   }}};
 });
 await page.addScriptTag({path:require('path').resolve(__dirname, '../../native/windows/MightyClaude.WinUI/Assets/ScreenShare/screen.js')});
 await page.evaluate(()=>window.command('start',{session:{sessionId:'fixture',deviceId:'phone',mode:'control',displayId:1,codec:'H264',quality:{width:128,height:72,fps:15,maxBitrateKbps:1000}},iceServers:[]}));
 await page.waitForFunction(()=>window.phone.connectionState==='connected'&&window.phoneChannel?.readyState==='open',null,{timeout:15000});
 const streams=await page.evaluate(()=>window.receivedStreams.sort()); if(JSON.stringify(streams)!==JSON.stringify(['overview','screen']))throw Error('track identity mismatch '+JSON.stringify(streams));
 await page.evaluate(async()=>{
  const canvas=document.createElement('canvas');canvas.width=160;canvas.height=90;canvas.getContext('2d').fillRect(0,0,160,90);const data=canvas.toDataURL('image/jpeg').split(',')[1];
  await window.command('frame',{frame:{data,mime:'image/jpeg',width:160,height:90,overviewData:data}});
  await window.command('data',{sessionId:'fixture',message:{t:'scene',phase:'motion'}});
  window.phoneChannel.send(JSON.stringify({t:'text',text:'한글 😀'}));
 });
 await page.waitForFunction(()=>window.receivedData.some(d=>d.t==='scene')&&window.messages.some(m=>m.type==='data'&&m.data.includes('한글')),null,{timeout:5000});
 const rejected=await page.evaluate(()=>window.messages.filter(m=>m.type==='ack'&&!m.ok));if(rejected.length)throw Error('bridge commands rejected '+JSON.stringify(rejected));
 let videoSizes=[];
 for(let attempt=0;attempt<100;attempt++) {
  videoSizes=await page.evaluate(async()=>[...(await window.phone.getStats()).values()].filter(r=>r.type==='inbound-rtp'&&r.kind==='video'&&r.framesDecoded>0).map(r=>[r.frameWidth,r.frameHeight]).sort((a,b)=>a[0]-b[0]));
  if(videoSizes.length===2)break;
  await new Promise(resolve=>setTimeout(resolve,100));
 }
 if(JSON.stringify(videoSizes)!==JSON.stringify([[128,72],[160,90]]))throw Error('per-peer resolution ceiling failed '+JSON.stringify(videoSizes));
 await page.evaluate(()=>window.command('halt'));
 await page.waitForFunction(()=>window.phoneChannel.readyState==='closed',null,{timeout:5000});
 console.log(JSON.stringify({h264Negotiated:true,screenAndOverviewStreamIds:streams,reliableDataChannel:true,unicodeControlFrame:true,syntheticFramesDecodedOnBothTracks:true,perPeerResolutionLimit:true,haltAcknowledged:true}));
 } finally { await browser.close(); }
})().catch(e=>{console.error(e);process.exit(1);});
