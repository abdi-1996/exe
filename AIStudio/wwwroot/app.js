const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];
const state = {
  token: '', workflows: [], current: null, jobs: [], references: [], settings: null,
  nodes: null, models: [], activeJob: null, enhancedReady: false,
  graph: null, selectedNode: null, connectionStart: null, zoom: 1, undo: [], redo: [], reconnectTimer: null
};

const queryToken = new URLSearchParams(location.search).get('token');
if (queryToken) {
  localStorage.setItem('aistudio.token', queryToken);
  history.replaceState({}, '', location.pathname + location.hash);
}
state.token = queryToken || localStorage.getItem('aistudio.token') || '';

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set('X-AIStudio-Token', state.token);
  if (options.body && !(options.body instanceof FormData)) headers.set('Content-Type', 'application/json');
  const response = await fetch(path, { ...options, headers, body: options.body && !(options.body instanceof FormData) ? JSON.stringify(options.body) : options.body });
  const text = await response.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  if (!response.ok) {
    const message = body?.error?.message || body?.error || body?.title || (typeof body === 'string' ? body : `HTTP ${response.status}`);
    const error = new Error(message); error.status = response.status; error.body = body; throw error;
  }
  return body;
}

function toast(message, error = false) {
  const box = $('#toast'); box.textContent = message; box.classList.toggle('error', error); box.classList.add('show');
  clearTimeout(box.timer); box.timer = setTimeout(() => box.classList.remove('show'), 3500);
}
function escapeHtml(value = '') { return String(value).replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c])); }
function formatBytes(value) { if (!Number.isFinite(value)) return '—'; const units=['Б','КБ','МБ','ГБ','ТБ']; let i=0; while(value>=1024&&i<units.length-1){value/=1024;i++;} return `${value.toFixed(i?1:0)} ${units[i]}`; }
function withToken(path) { const url = new URL(path, location.origin); url.searchParams.set('token', state.token); return url.pathname + url.search; }

async function authenticate(token) {
  state.token = token;
  try { await api('/api/auth/check'); localStorage.setItem('aistudio.token', token); $('#authGate').classList.add('hidden'); await initialize(); }
  catch { $('#authGate').classList.remove('hidden'); $('#authError').textContent = 'Неверный токен или сервер недоступен.'; }
}

const pageMeta = {
  generation:['Генерация','Параметры текущего workflow'], workflow:['Workflow','Редактор текущего графа'], references:['Референсы','Изображения, видео и аудио'], gallery:['Галерея','Финальные результаты и история'], assistant:['ИИ-ассистент','Промты, сцены и диагностика'], models:['Модели и LoRA','Содержимое подключённого движка'], nodes:['Ноды','Доступные классы ComfyUI'], optimization:['Оптимизация','Профили памяти и GPU'], settings:['Настройки','Движок, сервер и провайдеры']
};
function openPage(page) {
  $$('.page').forEach(x => x.classList.remove('active')); $(`#page-${page}`).classList.add('active');
  $$('#nav button').forEach(x => x.classList.toggle('active', x.dataset.page === page));
  [$('#pageTitle').textContent, $('#pageSubtitle').textContent] = pageMeta[page]; $('.sidebar').classList.remove('open');
  if (page === 'workflow') renderGraph();
  if (page === 'references') loadReferences();
  if (page === 'gallery') renderGallery();
  if (page === 'nodes') loadNodes();
  if (page === 'models') { loadModels(); loadFiles(); }
}

async function initialize() {
  bindEvents();
  await Promise.allSettled([loadSettings(), loadWorkflows(), loadReferences(), loadJobs(), refreshStatus()]);
  connectEvents();
  setInterval(refreshStatus, 5000);
  setInterval(loadJobs, 8000);
  if ('serviceWorker' in navigator && (location.protocol === 'https:' || location.hostname === '127.0.0.1' || location.hostname === 'localhost')) navigator.serviceWorker.register('/sw.js').catch(()=>{});
}

let eventsBound = false;
function bindEvents() {
  if (eventsBound) return; eventsBound = true;
  $('#nav').addEventListener('click', e => { const button=e.target.closest('button[data-page]'); if(button) openPage(button.dataset.page); });
  $('#menuButton').onclick = () => $('.sidebar').classList.toggle('open');
  $('#authForm').onsubmit = e => { e.preventDefault(); authenticate($('#authToken').value.trim()); };
  $('#importButton').onclick = () => $('#workflowFile').click();
  $('#workflowFile').onchange = importWorkflow;
  $('#workflowSelect').onchange = () => selectWorkflow($('#workflowSelect').value);
  $('#generateButton').onclick = () => generate(false);
  $('#queueButton').onclick = () => generate(true);
  $('#stopButton').onclick = cancelActiveJob;
  $('#discardEnhanced').onclick = () => { state.enhancedReady=false; $('#enhancedBox').classList.add('hidden'); $('#enhancedPrompt').value=''; };
  $('#refreshJobs').onclick = loadJobs; $('#galleryRefresh').onclick = async()=>{await loadJobs();renderGallery();};
  $('#referenceFiles').onchange = e => uploadFiles(e.target.files);
  const drop=$('#dropZone'); ['dragenter','dragover'].forEach(name=>drop.addEventListener(name,e=>{e.preventDefault();drop.classList.add('drag')})); ['dragleave','drop'].forEach(name=>drop.addEventListener(name,e=>{e.preventDefault();drop.classList.remove('drag')})); drop.ondrop=e=>uploadFiles(e.dataTransfer.files);
  $('#chatForm').onsubmit = sendChat;
  $('#scanModels').onclick = loadModels; $('#modelCategory').onchange=loadModels; $('#modelSearch').oninput=renderModels; $('#nodeSearch').oninput=renderNodes;
  $('#settingsForm').onsubmit = saveSettings; $('#testProvider').onclick=testProvider; $('#engineStart').onclick=()=>engineAction('start'); $('#engineStop').onclick=()=>engineAction('stop');
  $('#freeMemory').onclick=async()=>{try{await api('/api/engine/free',{method:'POST'});toast('Запрос на выгрузку отправлен ComfyUI.')}catch(e){toast(e.message,true)}};
  $$('.profile-grid button').forEach(button=>button.onclick=()=>setProfile(button.dataset.profile));
  bindGraphEvents();
  document.addEventListener('visibilitychange',()=>{ if(!document.hidden){refreshStatus();loadJobs();connectEvents();} });
}

async function refreshStatus() {
  try {
    const snapshot=await api('/api/status'); const ok=snapshot.reachable; $('#engineDot').className=`dot ${ok?'ok':'bad'}`;
    $('#engineLabel').textContent=ok?'ComfyUI подключён':'ComfyUI недоступен'; $('#engineUrl').textContent=snapshot.baseUrl;
    const device=snapshot.systemStats?.devices?.[0];
    $('#gpuMetric').textContent=device?.name ? device.name.replace(/^.*?: /,'').slice(0,24) : 'GPU —';
    $('#vramMetric').textContent=device?.vram_total ? `VRAM ${formatBytes(device.vram_total-device.vram_free)} / ${formatBytes(device.vram_total)}`:'VRAM —';
    const system=snapshot.systemStats?.system; $('#ramMetric').textContent=system?.ram_total ? `RAM ${formatBytes(system.ram_total-system.ram_free)} / ${formatBytes(system.ram_total)}`:'RAM —';
    const running=snapshot.queue?.queue_running?.length||0,pending=snapshot.queue?.queue_pending?.length||0; $('#queueMetric').textContent=`Очередь ${running+pending}`;
  } catch(e) { $('#engineDot').className='dot bad';$('#engineLabel').textContent='Сервер недоступен'; }
}

function connectEvents() {
  if (state.socket && [WebSocket.OPEN,WebSocket.CONNECTING].includes(state.socket.readyState)) return;
  clearTimeout(state.reconnectTimer);
  const scheme=location.protocol==='https:'?'wss':'ws'; const socket=new WebSocket(`${scheme}://${location.host}/ws/events?token=${encodeURIComponent(state.token)}`); state.socket=socket;
  socket.onmessage=e=>{ if(typeof e.data!=='string')return; try{handleEngineEvent(JSON.parse(e.data));}catch{} };
  socket.onclose=()=>{state.reconnectTimer=setTimeout(connectEvents,2000)}; socket.onerror=()=>socket.close();
}
function handleEngineEvent(event) {
  const data=event.data||{},job=state.jobs.find(x=>x.promptId===data.prompt_id); if(!job)return;
  if(event.type==='execution_start')job.status='running';
  if(event.type==='executing'){job.currentNode=data.node;if(data.node==null){job.status='completed';job.progress=1;setTimeout(()=>loadJobResult(job),800)}}
  if(event.type==='progress'){job.progress=data.max?data.value/data.max:0;job.currentNode=data.node||job.currentNode}
  if(event.type==='execution_success'){job.status='completed';job.progress=1;setTimeout(()=>loadJobResult(job),500)}
  if(event.type==='execution_error'){job.status='failed';job.error=data.exception_message}
  state.activeJob=job; renderJobs(); renderProgress(job);
}

async function loadWorkflows() {
  state.workflows=await api('/api/workflows'); const select=$('#workflowSelect'); const selected=state.current?.id||localStorage.getItem('aistudio.workflow')||'';
  select.innerHTML='<option value="">Выберите workflow</option>'+state.workflows.map(x=>`<option value="${x.id}">${escapeHtml(x.name)}</option>`).join('');
  if(selected&&state.workflows.some(x=>x.id===selected)){select.value=selected;await selectWorkflow(selected)}
}
async function selectWorkflow(id) {
  if(!id){state.current=null;renderWorkflow();return} state.current=await api(`/api/workflows/${id}`); localStorage.setItem('aistudio.workflow',id); renderWorkflow();
}
async function importWorkflow() {
  const file=$('#workflowFile').files[0]; if(!file)return;
  try { const graph=JSON.parse(await file.text()); const profile=await api('/api/workflows/import',{method:'POST',body:{name:file.name.replace(/\.json$/i,''),graph}}); await loadWorkflows();$('#workflowSelect').value=profile.id;await selectWorkflow(profile.id);toast(`Импортирован ${profile.sourceFormat.toUpperCase()} JSON`); }
  catch(e){toast(e.message,true)} finally{$('#workflowFile').value=''}
}
function renderWorkflow() {
  const profile=state.current; $('#workflowTitle').textContent=profile?.name||'Workflow не выбран';
  if(!profile){$('#dynamicForm').innerHTML='<div class="empty"><b>Импортируйте API JSON или UI JSON</b><span>Форма будет построена по реальным входам нод.</span></div>';return}
  $('#workflowAlerts').innerHTML=[...(profile.missingNodes?.length?[`<div class="alert">Недостающие ноды: ${profile.missingNodes.map(escapeHtml).join(', ')}</div>`]:[]),...(profile.missingModels?.length?[`<div class="alert">Недостающие модели: ${profile.missingModels.map(escapeHtml).join(', ')}</div>`]:[]),...(profile.warnings||[]).map(x=>`<div class="alert warn">${escapeHtml(x)}</div>`)].join('');
  const form=$('#dynamicForm'); if(!profile.bindings?.length){form.innerHTML='<div class="empty"><b>Автоматические параметры не найдены</b><span>Используйте редактор Workflow или добавьте ручные привязки.</span></div>';return}
  form.innerHTML='<div class="binding-grid">'+profile.bindings.map(binding=>bindingHtml(binding)).join('')+'</div>';
  form.querySelectorAll('[data-binding]').forEach(input=>input.addEventListener('input',()=>{state.enhancedReady=false;$('#enhancedBox').classList.add('hidden')}));
  state.graph=structuredClone(profile.uiGraph);state.undo=[];state.redo=[];renderGraph();
}
function bindingHtml(binding) {
  const value=binding.value??''; const wide=['prompt','negativePrompt','reference'].includes(binding.kind);
  let input='';
  if(binding.choices?.length)input=`<select data-binding="${binding.id}" data-type="choice">${binding.choices.map(x=>`<option ${x===value?'selected':''}>${escapeHtml(x)}</option>`).join('')}</select>`;
  else if(['prompt','negativePrompt'].includes(binding.kind))input=`<textarea data-binding="${binding.id}" data-kind="${binding.kind}" data-type="string">${escapeHtml(value)}</textarea>`;
  else if(binding.valueType==='boolean')input=`<input data-binding="${binding.id}" data-type="boolean" type="checkbox" ${value?'checked':''}>`;
  else if(['integer','number'].includes(binding.valueType))input=`<input data-binding="${binding.id}" data-kind="${binding.kind}" data-type="${binding.valueType}" type="number" value="${escapeHtml(value)}">`;
  else input=`<input data-binding="${binding.id}" data-kind="${binding.kind}" data-type="string" value="${escapeHtml(value)}">`;
  const seed=binding.kind==='seed'?`<button type="button" class="random-seed" data-target="${binding.id}">Случайный</button>`:'';
  queueMicrotask(()=>$$('.random-seed').forEach(button=>button.onclick=()=>{const input=document.querySelector(`[data-binding="${CSS.escape(button.dataset.target)}"]`);input.value=Math.floor(Math.random()*2147483647);input.dispatchEvent(new Event('input'))}));
  return `<label class="binding ${wide?'wide':''}"><span>${escapeHtml(binding.label)}</span>${input}${seed}</label>`;
}
function collectValues() {
  const values={}; $$('[data-binding]').forEach(input=>{let value=input.type==='checkbox'?input.checked:input.value;if(input.dataset.type==='integer')value=Number.parseInt(value,10);if(input.dataset.type==='number')value=Number.parseFloat(value);values[input.dataset.binding]=value});return values;
}
async function generate(queueOnly) {
  if(!state.current)return toast('Сначала выберите workflow.',true); if($('#generateButton').disabled)return;
  const promptInput=$('[data-kind="prompt"]'); const original=promptInput?.value||'';
  if($('#enhancerToggle').checked&&!state.enhancedReady){
    try{$('#generateButton').disabled=true;const response=await api('/api/assistant/enhance',{method:'POST',body:{prompt:original,workflowName:state.current.name,modelName:findModelName(),referenceIds:selectedReferences()}});$('#enhancedPrompt').value=response.text;$('#enhancedBox').classList.remove('hidden');state.enhancedReady=true;if(!$('#autoContinue').checked){toast('Проверьте улучшенный промт и снова нажмите «Генерировать».');return}}
    catch(e){toast(e.message,true);return}finally{$('#generateButton').disabled=false}
  }
  let enhanced=state.enhancedReady?$('#enhancedPrompt').value:''; if(enhanced&&promptInput)promptInput.value=enhanced;
  try{$('#generateButton').disabled=true;const result=await api('/api/generate',{method:'POST',body:{workflowId:state.current.id,revision:state.current.revision,values:collectValues(),originalPrompt:original,enhancedPrompt:enhanced}});state.current.revision=result.workflowRevision;state.activeJob=result.job;state.jobs.unshift(result.job);renderJobs();renderProgress(result.job);toast(queueOnly?'Добавлено в очередь':'Задание отправлено в ComfyUI');state.enhancedReady=false}
  catch(e){if(e.body?.missingNodes)toast(`Нет нод: ${e.body.missingNodes.join(', ')}`,true);else toast(e.message,true)}finally{$('#generateButton').disabled=false}
}
function findModelName(){return state.current?.bindings?.find(x=>x.kind==='model')?.value?.toString()||''}
function selectedReferences(){return [...document.querySelectorAll('.reference-select:checked')].map(x=>x.value)}

async function loadJobs(){try{state.jobs=await api('/api/jobs');state.activeJob=state.jobs.find(x=>['queued','running'].includes(x.status))||state.activeJob;renderJobs();if(state.activeJob)renderProgress(state.activeJob)}catch{}}
function renderJobs(){const box=$('#jobList');box.innerHTML=state.jobs.slice(0,12).map(job=>`<div class="job"><b>${escapeHtml(job.workflowName)} · ${statusName(job.status)}</b><small>${new Date(job.createdAt).toLocaleString()}${job.currentNode?` · нода ${escapeHtml(job.currentNode)}`:''}</small>${['queued','running'].includes(job.status)?`<button data-cancel="${job.id}">■</button>`:''}</div>`).join('')||'<div class="empty">История пуста</div>';box.querySelectorAll('[data-cancel]').forEach(button=>button.onclick=()=>cancelJob(button.dataset.cancel));}
function statusName(status){return({queued:'в очереди',running:'выполняется',completed:'готово',failed:'ошибка',cancelled:'отменено'})[status]||status}
function renderProgress(job){const progress=job.progress||0;$('#progressBar').value=progress;$('#progressPercent').textContent=`${Math.round(progress*100)}%`;$('#progressText').textContent=`${job.workflowName} · ${statusName(job.status)}`;$('#currentNode').textContent=`Текущая нода: ${job.currentNode||'—'}`;$('#jobBadge').textContent=statusName(job.status);}
async function cancelJob(id){try{await api(`/api/jobs/${id}/cancel`,{method:'POST'});await loadJobs();toast('Отмена отправлена движку.')}catch(e){toast(e.message,true)}}
function cancelActiveJob(){if(state.activeJob)cancelJob(state.activeJob.id)}

async function loadJobResult(job){try{const history=await api(`/api/jobs/${job.id}/history`);const files=extractOutputs(history);if(files.length)showPreview(files[0]);await loadJobs();renderGallery()}catch{}}
function extractOutputs(history){const files=[];const visit=node=>{if(!node||typeof node!=='object')return;if(node.filename)files.push(node);else Object.values(node).forEach(visit)};visit(history);return files}
function showPreview(file){const url=withToken(`/api/comfy/view?filename=${encodeURIComponent(file.filename)}&subfolder=${encodeURIComponent(file.subfolder||'')}&type=${encodeURIComponent(file.type||'output')}`);const ext=file.filename.split('.').pop().toLowerCase();$('#preview').innerHTML=['mp4','webm','mov'].includes(ext)?`<video src="${url}" controls autoplay></video>`:['mp3','wav','flac','m4a'].includes(ext)?`<audio src="${url}" controls autoplay></audio>`:`<img src="${url}" alt="Результат">`;}
function renderGallery(){const completed=state.jobs.filter(x=>x.status==='completed');$('#galleryGrid').innerHTML=completed.map(job=>`<article class="media-card"><div class="thumb"><span>✦</span></div><div><b>${escapeHtml(job.workflowName)}</b><small>${new Date(job.createdAt).toLocaleString()}</small><div class="action-row"><button data-result="${job.id}">Открыть</button><button data-reuse="${job.id}">Настройки</button><button data-repeat="${job.id}">Повторить</button></div><div class="action-row"><button data-reference="${job.id}">В референсы</button><button data-folder="${job.id}">Открыть папку</button></div></div></article>`).join('')||'<div class="empty">Готовых заданий пока нет</div>';$$('[data-result]').forEach(b=>b.onclick=async()=>{const job=state.jobs.find(x=>x.id===b.dataset.result);await loadJobResult(job);openPage('generation')});$$('[data-reuse]').forEach(b=>b.onclick=()=>reuseJob(b.dataset.reuse,false));$$('[data-repeat]').forEach(b=>b.onclick=()=>reuseJob(b.dataset.repeat,true));$$('[data-reference]').forEach(b=>b.onclick=async()=>{try{await api(`/api/jobs/${b.dataset.reference}/reference`,{method:'POST'});toast('Результат добавлен в референсы.');await loadReferences()}catch(e){toast(e.message,true)}});$$('[data-folder]').forEach(b=>b.onclick=async()=>{try{await api(`/api/jobs/${b.dataset.folder}/open-folder`,{method:'POST'})}catch(e){toast(e.message,true)}});}
async function reuseJob(id,repeat){const job=state.jobs.find(x=>x.id===id);if(!job)return;openPage('generation');$('#workflowSelect').value=job.workflowId;await selectWorkflow(job.workflowId);for(const [binding,value] of Object.entries(job.parameters||{})){const input=document.querySelector(`[data-binding="${CSS.escape(binding)}"]`);if(!input)continue;if(input.type==='checkbox')input.checked=Boolean(value);else input.value=value??''}toast('Настройки задания восстановлены.');if(repeat)await generate(true)}

async function uploadFiles(files){for(const file of files){const form=new FormData();form.append('file',file);try{await api('/api/references',{method:'POST',body:form});toast(`Загружен ${file.name}`)}catch(e){toast(e.message,true)}}await loadReferences()}
async function loadReferences(){try{state.references=await api('/api/references');$('#referenceGrid').innerHTML=state.references.map((asset,i)=>`<article class="media-card"><div class="thumb">${mediaTag(asset)}</div><div><b>${i<9?`Picture ${i+1}`:escapeHtml(asset.fileName)}</b><small>${formatBytes(asset.size)}</small><label class="tiny-check"><input class="reference-select" type="checkbox" value="${asset.id}"> для ИИ</label></div></article>`).join('')||'<div class="empty">Референсов пока нет</div>'}catch{}}
function mediaTag(asset){const url=withToken(asset.downloadUrl);if(asset.mediaType.startsWith('image/'))return`<img src="${url}" loading="lazy">`;if(asset.mediaType.startsWith('video/'))return`<video src="${url}" controls></video>`;if(asset.mediaType.startsWith('audio/'))return`<audio src="${url}" controls></audio>`;return'Файл'}

async function sendChat(e){e.preventDefault();const input=$('#chatInput'),message=input.value.trim();if(!message)return;addMessage(message,'user');input.value='';try{if($('#chatGraph').checked){if(!state.current)throw new Error('Сначала выберите workflow.');const proposal=await api('/api/assistant/graph/propose',{method:'POST',body:{workflowId:state.current.id,instruction:message}});showProposal(proposal)}else{const response=await api('/api/assistant/chat',{method:'POST',body:{message,useVision:$('#chatVision').checked,referenceIds:selectedReferences()}});addMessage(response.text,'assistant')}}catch(error){addMessage(`Ошибка: ${error.message}`,'assistant')}}
function showProposal(proposal){const box=$('#graphProposal');box.classList.remove('hidden');box.innerHTML=`<b>${escapeHtml(proposal.summary)}</b><ul>${proposal.operations.map(x=>`<li>Нода ${escapeHtml(x.nodeId)}: ${escapeHtml(x.inputName)} → <code>${escapeHtml(JSON.stringify(x.value))}</code></li>`).join('')}</ul><div class="action-row"><button class="primary" id="applyProposal">Применить подтверждённые изменения</button><button id="rejectProposal">Отклонить</button></div>`;$('#rejectProposal').onclick=()=>box.classList.add('hidden');$('#applyProposal').onclick=async()=>{try{const profile=await api(`/api/assistant/graph/apply/${proposal.id}`,{method:'POST'});state.current=profile;state.graph=structuredClone(profile.uiGraph);renderWorkflow();box.classList.add('hidden');addMessage('Изменения применены. Предыдущая ревизия сохранена для отмены.','assistant')}catch(e){toast(e.message,true)}}}
function addMessage(text,role){const item=document.createElement('div');item.className=`message ${role}`;item.textContent=text;$('#chatMessages').append(item);item.scrollIntoView({behavior:'smooth'})}

async function loadSettings(){const response=await api('/api/settings');state.settings=response.settings;fillSettings();}
function fillSettings(){const s=state.settings;if(!s)return;$('#engineMode').value=s.engine.mode;$('#externalUrl').value=s.engine.externalUrl;$('#managedDirectory').value=s.engine.managedDirectory;$('#pythonExecutable').value=s.engine.pythonExecutable;$('#managedPort').value=s.engine.managedPort;$('#autoStart').checked=s.engine.autoStart;$('#aiProvider').value=s.ai.provider;$('#aiEndpoint').value=s.ai.endpoint;$('#aiModel').value=s.ai.model;$('#visionModel').value=s.ai.visionModel;$('#studioPort').value=s.port;$('#allowLan').checked=s.allowLan;$('#modelRoots').value=(s.modelRoots||[]).join('\n');$$('.profile-grid button').forEach(x=>x.classList.toggle('active',x.dataset.profile===s.optimizationProfile));}
async function saveSettings(e){e.preventDefault();const s=structuredClone(state.settings);s.port=Number($('#studioPort').value);s.allowLan=$('#allowLan').checked;s.modelRoots=$('#modelRoots').value.split(/\r?\n/).map(x=>x.trim()).filter(Boolean);Object.assign(s.engine,{mode:$('#engineMode').value,externalUrl:$('#externalUrl').value.trim(),managedDirectory:$('#managedDirectory').value.trim(),pythonExecutable:$('#pythonExecutable').value.trim(),managedPort:Number($('#managedPort').value),autoStart:$('#autoStart').checked});Object.assign(s.ai,{provider:$('#aiProvider').value,endpoint:$('#aiEndpoint').value.trim(),model:$('#aiModel').value.trim(),visionModel:$('#visionModel').value.trim()});try{const response=await api('/api/settings',{method:'PUT',body:{settings:s,apiKey:$('#apiKey').value||null}});state.settings=response.settings;$('#apiKey').value='';$('#settingsMessage').textContent='Сохранено. Для сервера и управляемого движка нужен перезапуск.';toast('Настройки сохранены')}catch(error){toast(error.message,true)}}
async function testProvider(){try{const result=await api('/api/assistant/models');const count=result?.models?.length||result?.data?.length||0;toast(`Подключение работает. Моделей: ${count}`)}catch(e){toast(e.message,true)}}
async function engineAction(action){try{await api(`/api/engine/${action}`,{method:'POST'});await refreshStatus();toast(action==='start'?'Движок запущен':'Движок остановлен')}catch(e){toast(e.message,true)}}
function setProfile(profile){state.settings.optimizationProfile=profile;$$('.profile-grid button').forEach(x=>x.classList.toggle('active',x.dataset.profile===profile));toast('Профиль будет применён после сохранения и перезапуска управляемого движка.')}

async function loadModels(){try{state.models=await api(`/api/comfy/models/${encodeURIComponent($('#modelCategory').value)}`);renderModels()}catch(e){$('#modelList').innerHTML=`<div class="alert">${escapeHtml(e.message)}</div>`}}
function renderModels(){const q=$('#modelSearch').value.toLowerCase();const items=(Array.isArray(state.models)?state.models:[]).filter(x=>String(x).toLowerCase().includes(q));$('#modelList').innerHTML=items.map(x=>`<div class="list-item"><span>${escapeHtml(x)}</span></div>`).join('')||'<div class="empty">Модели не найдены</div>'}
async function loadFiles(path=''){try{const items=await api(`/api/files${path?`?path=${encodeURIComponent(path)}`:''}`);$('#fileBrowser').innerHTML=items.map(x=>`<button class="list-item" data-path="${escapeHtml(x.path)}" ${x.directory?'':'disabled'}><span>${x.directory?'▸':'·'} ${escapeHtml(x.name)}</span><small>${x.size?formatBytes(x.size):''}</small></button>`).join('');$('#fileBrowser').querySelectorAll('[data-path]:not(:disabled)').forEach(b=>b.onclick=()=>loadFiles(b.dataset.path))}catch(e){$('#fileBrowser').innerHTML=`<div class="alert">${escapeHtml(e.message)}</div>`}}
async function loadNodes(){if(!state.nodes){try{state.nodes=await api('/api/comfy/nodes')}catch(e){$('#nodeList').innerHTML=`<div class="alert">${escapeHtml(e.message)}</div>`;return}}renderNodes()}
function renderNodes(){const q=$('#nodeSearch').value.toLowerCase();const nodes=Object.values(state.nodes||{}).filter(x=>(x.display_name+' '+x.name+' '+x.category).toLowerCase().includes(q)).slice(0,1000);$('#nodeList').innerHTML=nodes.map(x=>`<article class="node-card"><b>${escapeHtml(x.display_name||x.name)}</b><small>${escapeHtml(x.name)} · ${escapeHtml(x.category)}</small></article>`).join('')}

function bindGraphEvents(){
  $('#graphSave').onclick=saveGraph;$('#graphUndo').onclick=undoGraph;$('#graphRedo').onclick=redoGraph;$('#graphDuplicate').onclick=duplicateNode;$('#graphDelete').onclick=deleteNode;$('#graphBypass').onclick=()=>setNodeMode(4);$('#graphMute').onclick=()=>setNodeMode(2);$('#zoomIn').onclick=()=>setZoom(state.zoom+.1);$('#zoomOut').onclick=()=>setZoom(state.zoom-.1);$('#graphFit').onclick=()=>setZoom(.75);
  $('#graphViewport').addEventListener('wheel',e=>{if(e.ctrlKey){e.preventDefault();setZoom(state.zoom+(e.deltaY<0?.1:-.1))}},{passive:false});
  document.addEventListener('keydown',e=>{if(!$('#page-workflow').classList.contains('active'))return;if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='z'){e.preventDefault();e.shiftKey?redoGraph():undoGraph()}if(e.key==='Delete')deleteNode()});
}
function pushUndo(){if(!state.graph)return;state.undo.push(structuredClone(state.graph));if(state.undo.length>40)state.undo.shift();state.redo=[]}
function undoGraph(){if(!state.undo.length)return;state.redo.push(structuredClone(state.graph));state.graph=state.undo.pop();renderGraph()}
function redoGraph(){if(!state.redo.length)return;state.undo.push(structuredClone(state.graph));state.graph=state.redo.pop();renderGraph()}
function setZoom(value){state.zoom=Math.max(.35,Math.min(1.8,value));$('#zoomValue').textContent=`${Math.round(state.zoom*100)}%`;renderGraph()}
function renderGraph(){const graph=state.graph;if(!graph){$('#graphCanvas').innerHTML='';$('#graphLinks').innerHTML='';return}const canvas=$('#graphCanvas'),svg=$('#graphLinks');canvas.style.transform=`scale(${state.zoom})`;svg.style.transform=`scale(${state.zoom})`;canvas.innerHTML='';for(const node of graph.nodes||[]){const item=document.createElement('div');item.className=`graph-node ${state.selectedNode==node.id?'selected':''}`;item.dataset.node=node.id;item.style.left=`${node.pos?.[0]||0}px`;item.style.top=`${node.pos?.[1]||0}px`;const inputs=(node.inputs||[]).map((x,i)=>`<button class="port input" data-node="${node.id}" data-slot="${i}" data-type="${escapeHtml(x.type||'*')}">${escapeHtml(x.name||'input')}</button>`).join('');const outputs=(node.outputs||[]).map((x,i)=>`<button class="port output" data-node="${node.id}" data-slot="${i}" data-type="${escapeHtml(x.type||'*')}">${escapeHtml(x.name||'output')}</button>`).join('');item.innerHTML=`<div class="node-head"><span>${escapeHtml(node.title||node.type)}</span><small>${node.mode===4?'BYPASS':node.mode===2?'MUTE':''}</small></div><div class="node-body"><div>${inputs}</div><div>${outputs}</div></div>`;canvas.append(item);bindNode(item,node)}drawLinks()}
function bindNode(element,node){let start=null;element.onclick=e=>{if(!e.target.classList.contains('port')){state.selectedNode=node.id;renderGraph()}};const head=element.querySelector('.node-head');head.onpointerdown=e=>{e.preventDefault();pushUndo();state.selectedNode=node.id;start={x:e.clientX,y:e.clientY,pos:[...(node.pos||[0,0])]};head.setPointerCapture(e.pointerId)};head.onpointermove=e=>{if(!start)return;node.pos=[start.pos[0]+(e.clientX-start.x)/state.zoom,start.pos[1]+(e.clientY-start.y)/state.zoom];element.style.left=`${node.pos[0]}px`;element.style.top=`${node.pos[1]}px`;drawLinks()};head.onpointerup=()=>start=null;element.querySelectorAll('.port.output').forEach(port=>port.onclick=e=>{e.stopPropagation();state.connectionStart={node:node.id,slot:Number(port.dataset.slot),type:port.dataset.type};toast('Теперь выберите совместимый вход.')});element.querySelectorAll('.port.input').forEach(port=>port.onclick=e=>{e.stopPropagation();finishConnection(node,port)})}
function finishConnection(target,port){const source=state.connectionStart;if(!source)return;const a=source.type,b=port.dataset.type;if(a!==b&&a!=='*'&&b!=='*'){toast(`Несовместимые типы: ${a} → ${b}`,true);return}pushUndo();const linkId=Math.max(0,...(state.graph.links||[]).map(x=>Number(x[0]||x.id)||0))+1;const targetInput=target.inputs[Number(port.dataset.slot)];if(targetInput.link!=null)state.graph.links=state.graph.links.filter(x=>String(x[0])!==String(targetInput.link));targetInput.link=linkId;state.graph.links.push([linkId,source.node,source.slot,target.id,Number(port.dataset.slot),b]);const sourceNode=state.graph.nodes.find(x=>String(x.id)===String(source.node));sourceNode.outputs[source.slot].links=[...(sourceNode.outputs[source.slot].links||[]),linkId];state.connectionStart=null;renderGraph()}
function drawLinks(){const svg=$('#graphLinks');if(!state.graph){svg.innerHTML='';return}svg.innerHTML='';for(const link of state.graph.links||[]){const [id,origin,originSlot,target,targetSlot]=Array.isArray(link)?link:[link.id,link.origin_id,link.origin_slot,link.target_id,link.target_slot];const a=state.graph.nodes.find(x=>String(x.id)===String(origin)),b=state.graph.nodes.find(x=>String(x.id)===String(target));if(!a||!b)continue;const x1=(a.pos?.[0]||0)+230,y1=(a.pos?.[1]||0)+47+originSlot*23,x2=b.pos?.[0]||0,y2=(b.pos?.[1]||0)+47+targetSlot*23;const path=document.createElementNS('http://www.w3.org/2000/svg','path');path.setAttribute('d',`M ${x1} ${y1} C ${x1+80} ${y1}, ${x2-80} ${y2}, ${x2} ${y2}`);path.setAttribute('class','graph-link');path.ondblclick=()=>removeLink(id);svg.append(path)}}
function removeLink(id){pushUndo();state.graph.links=state.graph.links.filter(x=>String(Array.isArray(x)?x[0]:x.id)!==String(id));for(const node of state.graph.nodes||[])for(const input of node.inputs||[])if(String(input.link)===String(id))input.link=null;renderGraph()}
function selectedGraphNode(){return state.graph?.nodes?.find(x=>String(x.id)===String(state.selectedNode))}
function duplicateNode(){const node=selectedGraphNode();if(!node)return;pushUndo();const copy=structuredClone(node);copy.id=Math.max(0,...state.graph.nodes.map(x=>Number(x.id)||0))+1;copy.pos=[(node.pos?.[0]||0)+35,(node.pos?.[1]||0)+35];copy.inputs?.forEach(x=>x.link=null);copy.outputs?.forEach(x=>x.links=[]);copy.properties={...(copy.properties||{}),aistudio_api_id:String(copy.id)};state.graph.nodes.push(copy);state.selectedNode=copy.id;renderGraph()}
function deleteNode(){const node=selectedGraphNode();if(!node)return;pushUndo();const ids=new Set((state.graph.links||[]).filter(x=>String(x[1])===String(node.id)||String(x[3])===String(node.id)).map(x=>String(x[0])));state.graph.links=state.graph.links.filter(x=>!ids.has(String(x[0])));state.graph.nodes=state.graph.nodes.filter(x=>String(x.id)!==String(node.id));for(const other of state.graph.nodes)for(const input of other.inputs||[])if(ids.has(String(input.link)))input.link=null;state.selectedNode=null;renderGraph()}
function setNodeMode(mode){const node=selectedGraphNode();if(!node)return;pushUndo();node.mode=node.mode===mode?0:mode;renderGraph()}
async function saveGraph(){if(!state.current||!state.graph)return;try{const profile=await api(`/api/workflows/${state.current.id}/graph`,{method:'PUT',body:{revision:state.current.revision,graph:state.graph}});state.current=profile;state.graph=structuredClone(profile.uiGraph);renderWorkflow();toast('Граф сохранён и API-форма синхронизирована.')}catch(e){toast(e.message,true)}}

if(state.token) authenticate(state.token); else $('#authGate').classList.remove('hidden');
