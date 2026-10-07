// Protocol test double only. It validates AI Studio's ComfyUI wire contract; it does not generate media.
import http from 'node:http';
import { randomUUID } from 'node:crypto';

let lastPrompt = null;
const schemas = {
  CLIPTextEncode: { input: { required: { text: ['STRING', { multiline: true }], clip: ['CLIP'] } }, output: ['CONDITIONING'], output_name: ['CONDITIONING'], display_name: 'CLIP Text Encode', category: 'conditioning' },
  CheckpointLoaderSimple: { input: { required: { ckpt_name: [['model.safetensors'], {}] } }, output: ['MODEL', 'CLIP', 'VAE'], output_name: ['MODEL', 'CLIP', 'VAE'], display_name: 'Load Checkpoint', category: 'loaders' },
  KSampler: { input: { required: { seed: ['INT', {}], steps: ['INT', {}], cfg: ['FLOAT', {}], model: ['MODEL'], positive: ['CONDITIONING'], negative: ['CONDITIONING'] } }, output: ['LATENT'], output_name: ['LATENT'], display_name: 'KSampler', category: 'sampling' }
};

const server = http.createServer(async (request, response) => {
  const chunks = []; for await (const chunk of request) chunks.push(chunk);
  const body = chunks.length ? JSON.parse(Buffer.concat(chunks).toString('utf8')) : null;
  const send = (status, value) => { response.writeHead(status, {'content-type':'application/json'}); response.end(JSON.stringify(value)); };
  if (request.url === '/system_stats') return send(200, { system: { os: 'protocol-test', ram_total: 1, ram_free: 1 }, devices: [{ name: 'Protocol test GPU', vram_total: 1, vram_free: 1 }] });
  if (request.url === '/object_info') return send(200, schemas);
  if (request.url === '/queue' || request.url === '/prompt' && request.method === 'GET') return send(200, { queue_running: [], queue_pending: [] });
  if (request.url?.startsWith('/history')) return send(200, {});
  if (request.url?.startsWith('/models/')) return send(200, ['model.safetensors']);
  if (request.url === '/prompt' && request.method === 'POST') { lastPrompt = body; return send(200, { prompt_id: body.prompt_id || randomUUID(), number: 1, node_errors: {} }); }
  if (request.url === '/interrupt' || request.url === '/free' || request.url === '/queue' && request.method === 'POST') return send(200, {});
  if (request.url === '/test/last-prompt') return send(200, lastPrompt);
  send(404, { error: 'not found' });
});
server.listen(8188, '127.0.0.1', () => console.log('Protocol test double listening on 8188'));

