namespace SpecRunner.Web;

/// <summary>
/// The operator's console, client side. One page, no build step, no dependencies - the target
/// user is the developer of this application, so ergonomics favour legibility and debuggability
/// over polish (project_info.md, "Target user").
///
/// The page is served from a string rather than from wwwroot so that the whole console is one
/// file a developer can read top to bottom, and so there is no second deployment concern for an
/// application that has no distribution story by design.
/// </summary>
public static class ConsolePage
{
    public const string Html =
        """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Spec Runner</title>
        <style>
          :root {
            --bg: #12141a; --panel: #181b23; --edge: #262b36; --text: #d7dbe4; --dim: #7d859a;
            --accent: #6ea8fe; --good: #6ec98a; --warn: #e0b654; --bad: #e0736d; --skip: #5f6980;
          }
          * { box-sizing: border-box; }
          body {
            margin: 0; background: var(--bg); color: var(--text);
            font: 13px/1.55 ui-monospace, "Cascadia Mono", Consolas, monospace;
          }
          header {
            position: sticky; top: 0; z-index: 5; background: var(--panel);
            border-bottom: 1px solid var(--edge); padding: 8px 14px;
          }
          .row { display: flex; gap: 14px; align-items: center; flex-wrap: wrap; }
          .grow { flex: 1; }
          h1 { font-size: 13px; margin: 0; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
          button {
            font: inherit; background: #222735; color: var(--text); border: 1px solid var(--edge);
            border-radius: 3px; padding: 4px 11px; cursor: pointer;
          }
          button:hover:not(:disabled) { border-color: var(--accent); color: #fff; }
          button:disabled { opacity: .35; cursor: default; }
          button.primary { background: #1d3a63; border-color: #2f5f9e; }
          button.danger { background: #452420; border-color: #7a3c35; }
          .pill { border: 1px solid var(--edge); border-radius: 999px; padding: 1px 9px; color: var(--dim); }
          .pill.running { color: var(--good); border-color: #2c5f42; }
          .pill.blocked { color: var(--warn); border-color: #6b5525; }
          .pill.halted, .pill.stopped { color: var(--bad); border-color: #6f3630; }

          #inflight {
            background: #101a26; border-bottom: 1px solid var(--edge); padding: 6px 14px; color: var(--dim);
          }
          #inflight b { color: var(--accent); font-weight: 600; }
          #inflight .hashes { color: #59617a; overflow-x: auto; white-space: nowrap; display: block; }

          main { display: grid; grid-template-columns: minmax(340px, 30%) 1fr; height: calc(100vh - 84px); }
          section { overflow-y: auto; padding: 10px 14px; }
          #side { border-right: 1px solid var(--edge); background: #14171e; }

          .card { border: 1px solid var(--edge); border-radius: 4px; margin-bottom: 10px; }
          .card > h2 {
            font-size: 11px; letter-spacing: .12em; text-transform: uppercase; color: var(--dim);
            margin: 0; padding: 6px 9px; border-bottom: 1px solid var(--edge); background: #171b24;
          }
          .card > div { padding: 8px 9px; }

          .step { padding: 3px 6px; border-radius: 3px; cursor: pointer; display: block; width: 100%;
                  text-align: left; border: none; background: none; color: inherit; }
          .step:hover { background: #1d222d; }
          .step .tag { display: inline-block; width: 92px; color: var(--dim); }
          .step.execute .tag { color: var(--accent); }
          .step.skip .tag { color: var(--skip); }
          .step.notapplicable .tag { color: #4d5468; }
          .step .why { color: var(--dim); padding-left: 92px; display: block; font-size: 12px; }

          .ev { border-left: 2px solid var(--edge); padding: 2px 0 2px 9px; margin-bottom: 3px; }
          .ev .meta { color: #545c72; }
          .ev .kind { color: var(--dim); }
          .ev .msg { white-space: pre-wrap; word-break: break-word; }
          .ev .fields { color: #5d6579; font-size: 12px; }
          .ev.terminal { border-left-color: #3a3f4d; opacity: .8; }
          .ev.step-started { border-left-color: var(--accent); }
          .ev.step-completed { border-left-color: var(--good); }
          .ev.artifact-written { border-left-color: #3d7d5a; }
          .ev.step-skipped { border-left-color: var(--skip); }
          .ev.record-invalidated, .ev.llm-condition { border-left-color: var(--warn); }
          .ev.block, .ev.defect-finding { border-left-color: var(--warn); }
          .ev.run-halted, .ev.fatal { border-left-color: var(--bad); }
          .ev.llm-response .msg { color: #b7c6de; }
          #stream { color: #9fb3d1; white-space: pre-wrap; border-left: 2px solid #2f4c78; padding-left: 9px; }

          .block { border: 1px solid #6b5525; border-radius: 4px; background: #1e1a10; padding: 10px; margin-bottom: 12px; }
          .block h3 { margin: 0 0 6px; font-size: 13px; color: var(--warn); }
          .block .answers { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 8px; }
          .block .hint { color: var(--dim); font-size: 12px; margin-top: 8px; }

          pre { margin: 0; white-space: pre-wrap; word-break: break-word; }
          a { color: var(--accent); cursor: pointer; text-decoration: none; }
          a:hover { text-decoration: underline; }
          .dim { color: var(--dim); }
          .warn { color: var(--warn); }
          .bad { color: var(--bad); }
          dialog { background: var(--panel); color: var(--text); border: 1px solid var(--edge);
                   border-radius: 5px; max-width: 90vw; width: 900px; max-height: 85vh; }
          dialog::backdrop { background: rgba(0,0,0,.6); }
        </style>
        </head>
        <body>

        <header>
          <div class="row">
            <h1>Spec Runner</h1>
            <span class="pill" id="phase">connecting</span>
            <span class="dim" id="runid"></span>
            <span class="grow"></span>
            <button id="btn-start" class="primary" disabled>Start run</button>
            <button id="btn-stop" disabled>Stop at next boundary</button>
            <button id="btn-rebuild">Rebuild state</button>
          </div>
          <div class="row dim" style="margin-top:4px">
            <span id="project"></span><span id="endpoint"></span>
          </div>
        </header>

        <div id="inflight"><span class="dim">Nothing in flight.</span></div>

        <main>
          <section id="side">
            <div class="card">
              <h2>Pre-flight plan</h2>
              <div id="plan"><span class="dim">Reconciling…</span></div>
            </div>
            <div class="card" id="conditions-card" style="display:none">
              <h2>Startup conditions</h2>
              <div id="conditions"></div>
            </div>
          </section>

          <section id="log">
            <div id="block-host"></div>
            <div id="events"></div>
            <div id="stream"></div>
          </section>
        </main>

        <dialog id="detail"><div style="padding:14px"><div class="row"><b id="detail-title" class="grow"></b>
          <button onclick="detail.close()">Close</button></div><pre id="detail-body"></pre></div></dialog>

        <script>
        const $ = (id) => document.getElementById(id);
        const events = $('events'), stream = $('stream');
        let currentPhase = '', inflightStarted = null, inflightData = null;

        // ---- event stream (feature 8.3: history first, then live, with no gap) ----
        const source = new EventSource('/api/events');
        source.onmessage = (m) => render(JSON.parse(m.data));
        source.onerror = () => { $('phase').textContent = 'disconnected'; $('phase').className = 'pill halted'; };

        function render(e) {
          if (e.kind === 'llm-token') { stream.textContent += e.message; scroll(); return; }
          if (e.kind === 'llm-response') stream.textContent = '';

          const div = document.createElement('div');
          div.className = 'ev ' + e.kind + (e.surface === 'terminal' ? ' terminal' : '');

          const meta = document.createElement('span');
          meta.className = 'meta';
          meta.textContent = String(e.sequence).padStart(5, '0') + ' ' + e.timestampUtc.slice(11, 23) + ' ';
          const kind = document.createElement('span');
          kind.className = 'kind';
          kind.textContent = e.kind + (e.surface === 'terminal' ? ' (terminal)' : '') + ' ';
          const msg = document.createElement('span');
          msg.className = 'msg';
          msg.textContent = e.message;

          div.append(meta, kind, msg);

          if (e.fields && e.fields.length) {
            const f = document.createElement('div');
            f.className = 'fields';
            f.textContent = e.fields.map(x => x.key + '=' + x.value).join('  ');
            div.append(f);
          }
          if (e.kind === 'block' && e.data) showBlock(e.data);
          if (e.kind === 'block-resolved') $('block-host').innerHTML = '';

          events.append(div);
          scroll();
        }

        function scroll() {
          const log = $('log');
          if (log.scrollHeight - log.scrollTop - log.clientHeight < 220) log.scrollTop = log.scrollHeight;
        }

        // ---- feature 6.1: answering from the browser is the server writing the same record ----
        function showBlock(b) {
          const host = $('block-host');
          host.innerHTML = '';
          const card = document.createElement('div');
          card.className = 'block';
          const h = document.createElement('h3');
          h.textContent = 'Blocked — a person must decide';
          const q = document.createElement('div');
          q.textContent = b.question;
          const where = document.createElement('div');
          where.className = 'hint';
          where.textContent = b.stepId + (b.iterationTarget ? ' [' + b.iterationTarget + ']' : '')
                            + ' · round ' + b.questionVersion;
          const answers = document.createElement('div');
          answers.className = 'answers';

          for (const option of b.permittedAnswers) {
            const btn = document.createElement('button');
            btn.textContent = option;
            btn.onclick = () => answer(b, option, btn);
            answers.append(btn);
          }

          const hint = document.createElement('div');
          hint.className = 'hint';
          hint.innerHTML = 'Or answer by hand: write <code>' + b.answerPath + '</code> using the template in '
                         + '<a onclick="showFile(\'' + b.questionPath + '\')">' + b.questionPath + '</a>. '
                         + 'Both channels produce the same kind of record.';

          card.append(h, q, where, answers, hint);
          host.append(card);
        }

        async function answer(b, value, btn) {
          btn.disabled = true;
          const res = await fetch('/api/answer', {
            method: 'POST', headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ stepId: b.stepId, target: b.iterationTarget, version: b.questionVersion, answer: value })
          });
          if (!res.ok) {
            const body = await res.json();
            alert(body.error || 'The answer was refused.');
            btn.disabled = false;
          }
        }

        // ---- feature 8.4: the one thing in flight, always visible ----
        async function poll() {
          try {
            const s = await (await fetch('/api/status')).json();
            $('runid').textContent = s.runId;
            $('project').textContent = 'project: ' + s.projectRoot + '  ';
            $('endpoint').textContent = 'endpoint: ' + s.endpoint;
            if (s.phase !== currentPhase) { currentPhase = s.phase; onPhase(s); }

            $('btn-start').disabled = s.phase !== 'AwaitingStart' || s.startRequested;
            $('btn-stop').disabled = !(s.phase === 'Running' || s.phase === 'Blocked') || s.stopRequested;

            if (s.current) {
              if (!inflightData || inflightData.stepId !== s.current.stepId
                  || inflightData.iterationTarget !== s.current.iterationTarget) {
                inflightData = s.current;
                inflightStarted = Date.parse(s.current.startedUtc);
              }
            } else { inflightData = null; }
            paintInflight();
          } catch { /* the server is gone; the phase pill already says so */ }
        }

        function paintInflight() {
          const el = $('inflight');
          if (!inflightData) {
            el.innerHTML = currentPhase === 'Blocked'
              ? '<span class="warn">Blocked — waiting for a person. Nothing is executing.</span>'
              : '<span class="dim">Nothing in flight.</span>';
            return;
          }
          const secs = ((Date.now() - inflightStarted) / 1000).toFixed(1);
          const hashes = (inflightData.inputHashes || [])
            .map(h => h.key + '=' + h.value.replace('sha256:', '').slice(0, 12)).join('  ');
          el.innerHTML = 'in flight: <b>' + inflightData.stepId + '</b>'
            + (inflightData.iterationTarget ? ' <b>[' + inflightData.iterationTarget + ']</b>' : '')
            + '  ' + secs + 's'
            + '<span class="hashes">consuming: ' + (hashes || '(no declared inputs)') + '</span>';
        }

        function onPhase(s) {
          const pill = $('phase');
          pill.textContent = s.phase.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
          pill.className = 'pill ' + s.phase.toLowerCase();
          loadPlan();
        }

        // ---- feature 1.10: the plan, rendered before anything runs ----
        async function loadPlan() {
          const p = await (await fetch('/api/plan')).json();
          if (!p.ready) return;

          const host = $('plan');
          host.innerHTML = '';
          const summary = document.createElement('div');
          summary.className = 'dim';
          summary.style.marginBottom = '6px';
          summary.textContent = p.steps.length + ' rows · '
            + p.steps.filter(s => s.action === 'execute').length + ' to execute · '
            + p.steps.filter(s => s.action === 'skip').length + ' to skip · '
            + p.definiteModelCalls + ' model call' + (p.definiteModelCalls === 1 ? '' : 's')
            + (p.modelCallCountIsLowerBound ? ' (plus one per unfrozen iteration item)' : '');
          host.append(summary);

          for (const s of p.steps) {
            const row = document.createElement('button');
            row.className = 'step ' + s.action;
            row.onclick = () => showStep(s.stepId, s.target);
            row.innerHTML = '<span class="tag">' + s.action + '</span>' + s.stepId
                          + (s.target ? ' [' + s.target + ']' : '')
                          + (s.callsModel ? ' <span class="dim">· model</span>' : '')
                          + '<span class="why">' + s.reason + '</span>';
            host.append(row);
          }

          const conditions = [];
          for (const o of p.orphans) conditions.push(['warn', 'orphaned artifact ' + o.relativePath
            + ' — likely from run ' + o.likelyRunId + ' (' + o.producingStepId + '); never loaded as input']);
          for (const h of p.handEdited) conditions.push(['warn', 'hand-edited artifact ' + h
            + ' — content taken as truth, downstream invalidated']);
          for (const c of p.incompleteModelCalls) conditions.push(['warn', 'model call initiated, never completed: ' + c]);
          for (const d of p.stateDivergences) conditions.push(['dim', 'state divergence: ' + d]);
          for (const i of p.invalidations) conditions.push(['bad', 'invalidated ' + i.stepId
            + (i.target ? ' [' + i.target + ']' : '') + ' — ' + i.cause
            + (i.differingInput && i.differingInput !== '(whole step)' ? ' on ' + i.differingInput : '')]);

          const card = $('conditions-card'), body = $('conditions');
          body.innerHTML = '';
          card.style.display = conditions.length ? '' : 'none';
          for (const [cls, text] of conditions) {
            const d = document.createElement('div');
            d.className = cls;
            d.textContent = '• ' + text;
            body.append(d);
          }
        }

        // ---- feature 8.6: per-step detail, including skipped steps and what justified the skip ----
        async function showStep(stepId, target) {
          const d = await (await fetch('/api/step?stepId=' + encodeURIComponent(stepId)
                                     + '&target=' + encodeURIComponent(target || ''))).json();
          $('detail-title').textContent = stepId + (target ? ' [' + target + ']' : '');

          let text = d.description + '\n\n';
          if (d.template) text += 'template        ' + d.template + '\n';
          if (d.iteratesOver) text += 'iterates over   ' + d.iteratesOver + '\n';
          if (d.guard) text += 'guard           ' + d.guard + '\n';
          text += 'reads vars      ' + (d.readsVariables.join(', ') || '(none)') + '\n';
          text += 'writes vars     ' + (d.writesVariables.join(', ') || '(none)') + '\n';
          text += 'reads artifacts ' + (d.readsArtifacts.join(', ') || '(none)') + '\n';
          text += 'writes artifact ' + (d.writesArtifacts.join(', ') || '(none)') + '\n';
          text += 'downstream      ' + (d.downstream.join('\n                ') || '(nothing)') + '\n\n';

          if (d.inForceRecord) {
            const r = d.inForceRecord;
            text += 'IN-FORCE COMPLETION RECORD (this is what justifies a skip)\n';
            text += '  ' + r.path + '\n  run ' + r.runId + ' seq ' + r.sequence + ' at ' + r.timestampUtc + '\n';
            text += '  inputs:\n' + r.inputs.map(i => '    ' + i.kind + ' ' + i.name + '\n      ' + i.hash
                                                     + '  (from ' + i.producer + ')').join('\n') + '\n';
            text += '  outputs:\n' + (r.outputs.map(o => '    ' + o.name + '  ' + o.hash).join('\n') || '    (none)') + '\n';
            text += '  artifacts:\n' + (r.artifacts.map(a => '    ' + a.path + '  ' + a.hash).join('\n') || '    (none)') + '\n\n';
          } else {
            text += 'No completion record is in force for this step.\n\n';
          }

          if (d.invalidations.length) {
            text += 'INVALIDATIONS\n';
            for (const i of d.invalidations) {
              text += '  ' + i.timestampUtc + '  ' + i.cause + '  raised by ' + i.raisedByStep + '\n';
              text += '    input ' + i.differingInput + '\n      was ' + i.expectedHash + '\n      now ' + i.actualHash + '\n';
              text += '    ' + i.path + '\n';
            }
            text += '\n';
          }

          $('detail-body').textContent = text;
          const invalidate = document.createElement('button');
          invalidate.className = 'danger';
          invalidate.textContent = 'Invalidate this step and everything downstream';
          invalidate.onclick = async () => {
            const res = await fetch('/api/invalidate', {
              method: 'POST', headers: { 'content-type': 'application/json' },
              body: JSON.stringify({ stepId, target })
            });
            const body = await res.json();
            alert(res.ok ? body.note : body.error);
            detail.close();
          };
          $('detail-body').append(invalidate);
          if (d.writesArtifacts.length && d.inForceRecord) {
            for (const a of d.inForceRecord.artifacts) {
              const explain = document.createElement('button');
              explain.style.marginLeft = '6px';
              explain.textContent = 'Explain ' + a.artifactId;
              explain.onclick = () => showProvenance(a.path);
              $('detail-body').append(explain);
            }
          }
          detail.showModal();
        }

        // ---- feature 8.7: provenance walker ----
        async function showProvenance(path) {
          const node = await (await fetch('/api/explain?path=' + encodeURIComponent(path))).json();
          $('detail-title').textContent = 'origin chain — ' + path;
          $('detail-body').textContent = renderNode(node, 0);
          detail.showModal();
        }

        function renderNode(n, depth) {
          const pad = '  '.repeat(depth);
          let t = pad + n.path + '\n';
          t += pad + '  ' + n.artifactId + ' v' + n.version + ' by ' + n.producingStepId
             + (n.iterationTarget !== '-' ? ' [' + n.iterationTarget + ']' : '') + '\n';
          t += pad + '  run ' + n.runId + ' at ' + n.timestampUtc + '\n';
          if (n.modelRequested !== '-') {
            t += pad + '  model ' + n.modelRequested + ' → reported ' + n.modelReported + '\n';
            t += pad + '  prompt ' + n.promptTemplatePath + ' resolved ' + n.resolvedPromptHash + '\n';
          }
          if (n.supersedesVersion !== '-') t += pad + '  supersedes v' + n.supersedesVersion + ' because ' + n.supersededBecause + '\n';
          if (n.handEdited) t += pad + '  ** HAND-EDITED: body no longer matches this file\'s own recorded hash **\n';
          for (const e of n.inputs) {
            t += pad + '  ← ' + e.name + (e.matches ? '  [hash holds]' : '  [HASH DIFFERS: recorded ' + e.recordedHash
                                                                      + ', now ' + e.currentHash + ']') + '\n';
            if (e.parent) t += renderNode(e.parent, depth + 2);
          }
          return t;
        }

        async function showFile(path) {
          const text = await (await fetch('/api/file?path=' + encodeURIComponent(path))).text();
          $('detail-title').textContent = path;
          $('detail-body').textContent = text;
          detail.showModal();
        }

        $('btn-start').onclick = async () => { await fetch('/api/start', { method: 'POST' }); poll(); };
        $('btn-stop').onclick = async () => { await fetch('/api/stop', { method: 'POST' }); poll(); };
        $('btn-rebuild').onclick = async () => { await fetch('/api/rebuild-state', { method: 'POST' }); };

        setInterval(poll, 500);
        setInterval(paintInflight, 100);
        poll();
        </script>
        </body>
        </html>
        """;
}
