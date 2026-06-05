// All of the hub's client JS, served as a static file so the Content-Security-Policy can stay strict
// (script-src 'self', no 'unsafe-inline'). Per-request data is passed via a JSON data-island, never
// inline executable script.
(function () {
  // Map images render cold in a few seconds; show a "Generating map…" spinner, then reveal on load.
  function wireMapBox(img) {
    var box = img.closest('.map-box');
    if (!box) return;
    img.addEventListener('load', function () { box.classList.remove('loading', 'error'); });
    img.addEventListener('error', function () {
      box.classList.remove('loading');
      box.classList.add('error');
      var s = box.querySelector('.map-status');
      if (s) s.textContent = 'Map unavailable';
    });
    if (img.getAttribute('src') && (!img.complete || img.naturalWidth === 0)) box.classList.add('loading');
  }

  // Time-machine scrubber: only present on the timeline page (reads its data from #tm-data).
  function wireScrubber() {
    var dataEl = document.getElementById('tm-data');
    if (!dataEl) return;
    var B = JSON.parse(dataEl.textContent), repo = dataEl.getAttribute('data-repo');
    var img = document.getElementById('tm-map'), box = img.closest('.map-box'),
        cap = document.getElementById('tm-cap'), when = document.getElementById('tm-when'),
        slider = document.getElementById('tm-scrub'), playBtn = document.getElementById('tm-play');
    function show(i) {
      var b = B[i];
      if (!b) return;
      box.classList.remove('error');
      box.classList.add('loading');
      box.querySelector('.map-status').textContent = 'Generating map…';
      img.src = '/r/' + repo + '/map/' + b.Hash + '.png';
      cap.textContent = b.Short + ' · ' + b.Msg;
      when.textContent = '#' + (i + 1) + '/' + B.length + ' · ' + b.Author + ' · ' + b.When;
      [i - 1, i + 1].forEach(function (j) { if (B[j]) { var p = new Image(); p.src = '/r/' + repo + '/map/' + B[j].Hash + '.png'; } });
    }
    slider.addEventListener('input', function (e) { show(+e.target.value); });
    var timer = null;
    playBtn.addEventListener('click', function () {
      if (timer) { clearInterval(timer); timer = null; playBtn.textContent = '▶ play'; return; }
      playBtn.textContent = '⏸ pause';
      timer = setInterval(function () { var v = +slider.value + 1; if (v > B.length - 1) v = 0; slider.value = v; show(v); }, 1200);
    });
    show(+slider.value);
  }

  // Replaces inline onsubmit="return confirm(...)" handlers (blocked by CSP) with a delegated one.
  function wireConfirms() {
    document.querySelectorAll('form[data-confirm]').forEach(function (f) {
      f.addEventListener('submit', function (e) { if (!confirm(f.getAttribute('data-confirm'))) e.preventDefault(); });
    });
  }

  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.map-box img').forEach(wireMapBox);
    wireScrubber();
    wireConfirms();
  });
})();
