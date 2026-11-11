#!/usr/bin/env bash
# Offline source/security checks; safe to run in public PR Actions.
set -euo pipefail
cd "$(dirname "$0")"
bash -n cloudflare_switch_termux.sh cloudflare_switch_remote.sh
node - <<'JS'
const fs = require('node:fs');
const cfg = JSON.parse(fs.readFileSync('wrangler.jsonc','utf8'));
const remote = fs.readFileSync('cloudflare_switch_remote.sh','utf8');
const phone = fs.readFileSync('cloudflare_switch_termux.sh','utf8');
if (cfg.name !== 'aaris-turbo-terminal' || cfg.main !== './worker.mjs') throw Error('wrong_worker');
if (cfg.durable_objects?.bindings?.[0]?.name !== 'MACHINES' || cfg.durable_objects.bindings[0].class_name !== 'TurboMachine') throw Error('wrong_binding');
if (cfg.exports?.TurboMachine?.storage !== 'sqlite') throw Error('sqlite_required');
if ('account_id' in cfg || 'migrations' in cfg) throw Error('account_binding_or_migration');
for (const text of [remote,phone]) {
  if (/(rm\s+-rf\s+[^\n]*(Library|\/workspaces)|git\s+(reset\s+--hard|clean\s+-fdx)|gh\s+codespace\s+(stop|delete))/i.test(text)) throw Error('destructive_command');
}
if (!remote.includes('wrangler login --device --browser=false')) throw Error('approved_login_missing');
if (!phone.includes('gh variable set AARIS_BROKER_URL')) throw Error('variable_update_missing');
if (!phone.includes('gh workflow run aaris-turbo-bootstrap.yml')) throw Error('safe_agent_bootstrap_missing');
if (!phone.includes('SOURCE_SHA=%q')) throw Error('unpinned_source');
if (remote.includes('wrangler secret put GITHUB_TOKEN') || remote.includes('wrangler secret put TURBO_AUTH_TOKEN')) throw Error('unsafe_worker_secret');
console.log('AARIS_CLOUDFLARE_ACCOUNT_SWITCH_STATIC_PASS');
JS
