(() => {
  const drop = document.getElementById('drop');
  const fileInput = document.getElementById('fileInput');
  const fileList = document.getElementById('fileList');
  const analyzeBtn = document.getElementById('analyzeBtn');
  const copyBtn = document.getElementById('copyBtn');
  const downloadBtn = document.getElementById('downloadBtn');
  const output = document.getElementById('output');
  const status = document.getElementById('status');

  let selected = [];
  let lastJson = null;

  function renderList() {
    fileList.textContent = selected.length
      ? selected.map(f => `${f.name} (${fmtSize(f.size)})`).join(', ')
      : '';
  }
  function fmtSize(s) {
    if (s < 1024) return `${s} B`;
    if (s < 1024*1024) return `${(s/1024).toFixed(1)} KB`;
    return `${(s/1024/1024).toFixed(1)} MB`;
  }

  ['dragenter','dragover'].forEach(ev => drop.addEventListener(ev, e => {
    e.preventDefault(); e.stopPropagation(); drop.classList.add('drag');
  }));
  ['dragleave','drop'].forEach(ev => drop.addEventListener(ev, e => {
    e.preventDefault(); e.stopPropagation(); drop.classList.remove('drag');
  }));
  drop.addEventListener('drop', e => addFiles(Array.from(e.dataTransfer?.files || [])));

  fileInput.addEventListener('change', () => {
    addFiles(Array.from(fileInput.files || []));
    fileInput.value = ''; 
  });

  function addFiles(files) {
    const acc = [];
    for (const f of files) {
      const n = f.name.toLowerCase();
      if (n.endsWith('.cs') || n.endsWith('.zip')) acc.push(f);
    }
    selected = acc;
    renderList();
    status.textContent = selected.length ? `Selected ${selected.length} file(s)` : '';
  }

  analyzeBtn.addEventListener('click', async () => {
    if (!selected.length) { status.textContent = 'Choose at least one .cs or .zip file.'; return; }
    analyzeBtn.disabled = true; analyzeBtn.textContent = 'Analyzing…';
    copyBtn.disabled = true; downloadBtn.disabled = true; output.textContent = ''; status.textContent = '';

    try {
      const fd = new FormData();
      selected.forEach(f => fd.append('files', f, f.name));

      const res = await fetch('/analyze', { method: 'POST', body: fd });
      if (!res.ok) throw new Error(`HTTP ${res.status}: ${await res.text()}`);

      lastJson = await res.json();
      output.textContent = JSON.stringify(lastJson, null, 2);

      copyBtn.disabled = false;
      downloadBtn.disabled = false;

      const t = lastJson.summary ? `${lastJson.summary.typeCount} types, ${lastJson.summary.edgeCount} edges` : '';
      status.textContent = `Done. ${t}`;
    } catch (err) {
      output.textContent = '';
      status.textContent = String(err);
    } finally {
      analyzeBtn.disabled = false; analyzeBtn.textContent = 'Analyze';
    }
  });

  copyBtn.addEventListener('click', async () => {
    if (!lastJson) return;
    try {
      await navigator.clipboard.writeText(JSON.stringify(lastJson, null, 2));
      status.textContent = 'Copied JSON to clipboard.';
    } catch { status.textContent = 'Copy failed.'; }
  });

  downloadBtn.addEventListener('click', () => {
    if (!lastJson) return;
    const blob = new Blob([JSON.stringify(lastJson, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a'); a.href = url; a.download = 'lighter-result.json';
    document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(url);
  });
})();