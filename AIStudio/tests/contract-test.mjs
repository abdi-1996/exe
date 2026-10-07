// Run while AI Studio uses an isolated data directory and fake-comfy.mjs is listening.
import fs from 'node:fs';
import assert from 'node:assert/strict';

const dataDirectory = process.env.AISTUDIO_DATA_DIR;
if (!dataDirectory) throw new Error('AISTUDIO_DATA_DIR is required');
const settings = JSON.parse(fs.readFileSync(`${dataDirectory}/settings/settings.json`, 'utf8'));
const headers = { 'content-type': 'application/json', 'X-AIStudio-Token': settings.accessToken };
const call = async (path, options={}) => {
  const response = await fetch(`http://127.0.0.1:${settings.port}${path}`, { ...options, headers: { ...headers, ...(options.headers||{}) } });
  const body = await response.json(); if (!response.ok) throw new Error(`${response.status}: ${JSON.stringify(body)}`); return body;
};

const graph = {
  '1': { class_type: 'CheckpointLoaderSimple', inputs: { ckpt_name: 'model.safetensors' } },
  '2': { class_type: 'CLIPTextEncode', inputs: { text: 'old prompt', clip: ['1', 1] }, _meta: { title: 'Positive Prompt' } },
  '3': { class_type: 'KSampler', inputs: { seed: 1, steps: 20, cfg: 7, model: ['1', 0], positive: ['2', 0], negative: ['2', 0] } }
};
const profile = await call('/api/workflows/import', { method: 'POST', body: JSON.stringify({ name: 'Contract workflow', graph }) });
assert.equal(profile.sourceFormat, 'api');
assert.equal(profile.missingNodes.length, 0);
assert.equal(profile.missingModels.length, 0);
const promptBinding = profile.bindings.find(item => item.kind === 'prompt');
const seedBinding = profile.bindings.find(item => item.kind === 'seed');
assert.ok(promptBinding); assert.ok(seedBinding);

const uiGraph = structuredClone(profile.uiGraph);
const uiPromptNode = uiGraph.nodes.find(item => item.type === 'CLIPTextEncode');
uiPromptNode.widgets_values[0] = 'prompt changed in graph editor';
const synchronized = await call(`/api/workflows/${profile.id}/graph`, { method: 'PUT', body: JSON.stringify({ revision: profile.revision, graph: uiGraph }) });
assert.equal(synchronized.apiGraph['2'].inputs.text, 'prompt changed in graph editor');
const synchronizedPrompt = synchronized.bindings.find(item => item.kind === 'prompt');
const synchronizedSeed = synchronized.bindings.find(item => item.kind === 'seed');

const queued = await call('/api/generate', { method: 'POST', body: JSON.stringify({
  workflowId: synchronized.id,
  revision: synchronized.revision,
  values: { [synchronizedPrompt.id]: 'new prompt from UI', [synchronizedSeed.id]: 424242 },
  originalPrompt: 'old prompt', enhancedPrompt: ''
}) });
assert.ok(queued.job.promptId);
const submitted = await (await fetch('http://127.0.0.1:8188/test/last-prompt')).json();
assert.equal(submitted.prompt['2'].inputs.text, 'new prompt from UI');
assert.equal(submitted.prompt['3'].inputs.seed, 424242);
assert.equal(queued.job.graphSha256.length, 64);
console.log(JSON.stringify({ imported: true, graphToFormSynchronized: true, submittedCurrentPrompt: true, submittedCurrentSeed: true, promptId: queued.job.promptId }, null, 2));
