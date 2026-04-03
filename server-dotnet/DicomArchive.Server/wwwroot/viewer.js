// DICOM Viewer — uses cornerstone-core + cornerstone-tools + dicomParser (UMD globals)
// Custom image loader bypasses cornerstoneWADOImageLoader web worker issues with CDN loading.
(function () {
  'use strict';

  var cs = cornerstone;
  var csTools = cornerstoneTools;

  // ── State ──────────────────────────────────────────────────────────────────
  var _initialized = false;
  var _loadingMode = 'server';
  var _currentStudyUid = null;
  var _currentSeriesUid = null;
  var _seriesList = [];
  var _element = null;

  // ── Custom DICOM Image Loader ──────────────────────────────────────────────
  // Loads uncompressed DICOM via fetch + dicomParser, returns a cornerstone image.
  function loadDicomImage(imageId) {
    var url = imageId.substring(imageId.indexOf(':') + 1);

    var promise = fetch(url)
      .then(function (resp) {
        if (!resp.ok) throw new Error('HTTP ' + resp.status + ' loading ' + url);
        return resp.arrayBuffer();
      })
      .then(function (arrayBuffer) {
        var byteArray = new Uint8Array(arrayBuffer);

        // Parse as Part 10 if DICM prefix present, otherwise raw Explicit VR LE
        var dataSet;
        var hasDICM = byteArray.length > 132 &&
          byteArray[128] === 0x44 && byteArray[129] === 0x49 &&
          byteArray[130] === 0x43 && byteArray[131] === 0x4D;
        if (hasDICM) {
          dataSet = dicomParser.parseDicom(byteArray);
        } else {
          dataSet = new dicomParser.DataSet(dicomParser.littleEndianByteArrayParser, byteArray, {});
          var byteStream = new dicomParser.ByteStream(dicomParser.littleEndianByteArrayParser, byteArray, 0);
          try {
            dicomParser.parseDicomDataSetExplicit(dataSet, byteStream, byteArray.length);
          } catch (_) {
            // Fall back to Implicit VR
            dataSet = new dicomParser.DataSet(dicomParser.littleEndianByteArrayParser, byteArray, {});
            byteStream = new dicomParser.ByteStream(dicomParser.littleEndianByteArrayParser, byteArray, 0);
            dicomParser.parseDicomDataSetImplicit(dataSet, byteStream, byteArray.length);
          }
        }

        var rows = dataSet.uint16('x00280010');
        var cols = dataSet.uint16('x00280011');
        var bitsAllocated = dataSet.uint16('x00280100') || 16;
        var bitsStored = dataSet.uint16('x00280101') || bitsAllocated;
        var pixelRepresentation = dataSet.uint16('x00280103') || 0;
        var samplesPerPixel = dataSet.uint16('x00280002') || 1;
        var photometric = dataSet.string('x00280004') || 'MONOCHROME2';

        var rescaleIntercept = parseFloat(dataSet.string('x00281052') || '0');
        var rescaleSlope = parseFloat(dataSet.string('x00281053') || '1');
        var wcStr = dataSet.string('x00281050');
        var wwStr = dataSet.string('x00281051');
        // Handle multi-value window (take first value)
        var windowCenter = wcStr ? parseFloat(wcStr.split('\\')[0]) : (1 << (bitsStored - 1));
        var windowWidth = wwStr ? parseFloat(wwStr.split('\\')[0]) : (1 << bitsStored);

        var pixelSpacingStr = dataSet.string('x00280030') || '';
        var spacingParts = pixelSpacingStr.split('\\');
        var rowSpacing = parseFloat(spacingParts[0]) || 1;
        var colSpacing = parseFloat(spacingParts[1]) || rowSpacing;

        // Extract pixel data
        var pixelDataElement = dataSet.elements.x7fe00010;
        if (!pixelDataElement) throw new Error('No pixel data found in DICOM file');

        var pixelData;
        if (bitsAllocated <= 8) {
          pixelData = new Uint8Array(arrayBuffer, pixelDataElement.dataOffset, pixelDataElement.length);
        } else if (pixelRepresentation === 0) {
          pixelData = new Uint16Array(arrayBuffer, pixelDataElement.dataOffset, pixelDataElement.length / 2);
        } else {
          pixelData = new Int16Array(arrayBuffer, pixelDataElement.dataOffset, pixelDataElement.length / 2);
        }

        // Compute min/max pixel values
        var minPx = pixelData[0], maxPx = pixelData[0];
        for (var i = 1; i < pixelData.length; i++) {
          if (pixelData[i] < minPx) minPx = pixelData[i];
          if (pixelData[i] > maxPx) maxPx = pixelData[i];
        }

        return {
          imageId: imageId,
          minPixelValue: minPx,
          maxPixelValue: maxPx,
          slope: rescaleSlope,
          intercept: rescaleIntercept,
          windowCenter: windowCenter,
          windowWidth: windowWidth,
          getPixelData: function () { return pixelData; },
          rows: rows,
          columns: cols,
          height: rows,
          width: cols,
          color: samplesPerPixel > 1,
          columnPixelSpacing: colSpacing,
          rowPixelSpacing: rowSpacing,
          sizeInBytes: pixelData.byteLength,
          invert: photometric === 'MONOCHROME1',
        };
      });

    return { promise: promise };
  }

  // ── Initialization ─────────────────────────────────────────────────────────
  function initViewer() {
    if (_initialized) return;
    try {
      // Register our custom DICOM image loader for the wadouri scheme
      cs.registerImageLoader('wadouri', loadDicomImage);

      // Initialise cornerstone-tools
      csTools.external.cornerstone = cs;
      csTools.external.Hammer = Hammer;
      csTools.init();

      _initialized = true;
      console.log('DICOM viewer initialized (custom loader)');
    } catch (err) {
      console.error('Cornerstone init failed:', err);
      if (typeof toast === 'function') toast('Viewer init failed: ' + err.message, 'error');
    }
  }

  // ── Enable / tear-down the viewport element ────────────────────────────────
  function enableElement() {
    var el = document.getElementById('viewerCanvas');
    if (!el) return null;

    // If already enabled on this element, clear stack state and return
    if (_element === el) {
      try { csTools.clearToolState(el, 'stack'); } catch (_) {}
      return el;
    }

    // Disable previous element if different
    if (_element) {
      try { cs.disable(_element); } catch (_) {}
    }

    cs.enable(el);
    _element = el;

    // Add tools
    csTools.addToolForElement(el, csTools.WwwcTool);
    csTools.addToolForElement(el, csTools.ZoomTool);
    csTools.addToolForElement(el, csTools.PanTool);
    csTools.addToolForElement(el, csTools.StackScrollMouseWheelTool);

    // Activate default bindings: left=W/L, right=Zoom, middle=Pan, wheel=Scroll
    csTools.setToolActiveForElement(el, 'Wwwc', { mouseButtonMask: 1 });
    csTools.setToolActiveForElement(el, 'Zoom', { mouseButtonMask: 2 });
    csTools.setToolActiveForElement(el, 'Pan', { mouseButtonMask: 4 });
    csTools.setToolActiveForElement(el, 'StackScrollMouseWheel', {});

    // Image index tracking
    el.addEventListener('cornerstoneimagerendered', _onImageRendered);

    return el;
  }

  function disableElement() {
    if (!_element) return;
    _element.removeEventListener('cornerstoneimagerendered', _onImageRendered);
    try { cs.disable(_element); } catch (_) {}
    _element = null;
  }

  function _onImageRendered() {
    if (!_element) return;
    try {
      var stack = csTools.getToolState(_element, 'stack');
      if (stack && stack.data && stack.data.length) {
        var data = stack.data[0];
        var el = document.getElementById('viewerImageIndex');
        if (el) el.textContent = (data.currentImageIdIndex + 1) + ' / ' + data.imageIds.length;
      }
    } catch (_) {}
  }

  // ── Helpers ────────────────────────────────────────────────────────────────
  function errMsg(err) {
    if (!err) return 'unknown error';
    if (typeof err === 'string') return err;
    if (err.message) return err.message;
    if (err.error) return String(err.error);
    if (err.status) return 'HTTP ' + err.status;
    try { return JSON.stringify(err); } catch (_) { return String(err); }
  }

  // ── Image loading ──────────────────────────────────────────────────────────
  async function buildImageIds(seriesUid, instanceUids) {
    if (_loadingMode === 'blob') {
      try {
        var resp = await fetch('/api/series/' + seriesUid + '/viewer-urls');
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        var urls = await resp.json();
        return urls.map(function (e) { return 'wadouri:' + e.url; });
      } catch (err) {
        console.warn('Blob URL fetch failed, falling back to server mode:', err);
      }
    }
    return instanceUids.map(function (uid) { return 'wadouri:/api/instances/' + uid + '/file'; });
  }

  async function buildSingleImageId(instanceUid) {
    if (_loadingMode === 'blob') {
      try {
        var resp = await fetch('/api/instances/' + instanceUid + '/blob-url');
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        var data = await resp.json();
        return 'wadouri:' + data.url;
      } catch (err) {
        console.warn('Blob URL fetch failed, falling back to server mode:', err);
      }
    }
    return 'wadouri:/api/instances/' + instanceUid + '/file';
  }

  // ── Load a stack into the viewport ─────────────────────────────────────────
  async function loadStack(imageIds) {
    if (!imageIds || imageIds.length === 0) return;

    var el = enableElement();
    if (!el) return;

    var startTime = performance.now();

    try {
      // Load and display the first image
      var firstImage = await cs.loadAndCacheImage(imageIds[0]);
      cs.displayImage(el, firstImage);

      // Set up the stack for scrolling
      csTools.addStackStateManager(el, ['stack']);
      csTools.addToolState(el, 'stack', {
        currentImageIdIndex: 0,
        imageIds: imageIds,
      });

      var elapsed = Math.round(performance.now() - startTime);
      var timeEl = document.getElementById('viewerLoadTime');
      if (timeEl) timeEl.textContent = elapsed + ' ms (first image)';

      var idxEl = document.getElementById('viewerImageIndex');
      if (idxEl) idxEl.textContent = '1 / ' + imageIds.length;

      // Prefetch remaining images in background (don't await)
      if (imageIds.length > 1) {
        Promise.all(imageIds.slice(1).map(function (id) {
          return cs.loadAndCacheImage(id).catch(function (err) {
            console.warn('Prefetch error:', id, errMsg(err));
          });
        })).then(function () {
          var totalElapsed = Math.round(performance.now() - startTime);
          if (timeEl) timeEl.textContent = totalElapsed + ' ms (all ' + imageIds.length + ')';
        });
      }

    } catch (err) {
      console.error('loadStack error:', err);
      if (typeof toast === 'function') toast('Failed to load image: ' + errMsg(err), 'error');
    }
  }

  // ── Series management ──────────────────────────────────────────────────────
  async function loadSeries(seriesUid) {
    _currentSeriesUid = seriesUid;

    var resp = await fetch('/api/series/' + seriesUid + '/instances');
    if (!resp.ok) throw new Error('HTTP ' + resp.status);
    var instances = await resp.json();
    if (instances.length === 0) {
      if (typeof toast === 'function') toast('No instances in this series', 'error');
      return;
    }

    var uids = instances.map(function (i) { return i.instance_uid; });
    var imageIds = await buildImageIds(seriesUid, uids);

    if (_seriesList.length > 0) populateThumbs(_seriesList);
    var sel = document.getElementById('viewerSeriesSelect');
    if (sel) sel.value = seriesUid;

    await loadStack(imageIds);
  }

  // ── Thumbnail strip ────────────────────────────────────────────────────────
  function populateThumbs(seriesList) {
    var container = document.getElementById('viewerThumbs');
    if (!container) return;
    container.innerHTML = '';
    seriesList.forEach(function (s) {
      var uid = s.series_uid;
      var div = document.createElement('div');
      div.className = 'viewer-thumb' + (uid === _currentSeriesUid ? ' active' : '');
      div.textContent = s.description || 'Series ' + (s.series_number || '?');
      div.title = uid;
      div.addEventListener('click', function () { switchSeries(uid); });
      container.appendChild(div);
    });
  }

  function populateSeriesSelect(seriesList) {
    var sel = document.getElementById('viewerSeriesSelect');
    if (!sel) return;
    sel.innerHTML = seriesList.map(function (s) {
      return '<option value="' + s.series_uid + '">' +
        (s.description || 'Series ' + (s.series_number || '?')) + '</option>';
    }).join('');
    if (_currentSeriesUid) sel.value = _currentSeriesUid;
  }

  // ── Public API ─────────────────────────────────────────────────────────────
  async function openViewer(level, uid) {
    initViewer();
    if (!_initialized) return;

    var panel = document.getElementById('viewerPanel');
    if (panel) panel.classList.remove('hidden');

    try {
      if (level === 'study') {
        _currentStudyUid = uid;
        var resp = await fetch('/api/studies/' + uid + '/series');
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        _seriesList = await resp.json();
        if (_seriesList.length === 0) {
          if (typeof toast === 'function') toast('No series in this study', 'error');
          return;
        }

        try {
          var studyResp = await fetch('/api/studies/' + uid);
          if (studyResp.ok) {
            var info = await studyResp.json();
            var infoEl = document.getElementById('viewerInfo');
            if (infoEl) {
              var name = (info.patient && info.patient.name) || '';
              var desc = info.description || '';
              infoEl.textContent = [name, desc].filter(Boolean).join(' \u2014 ') || 'DICOM Study';
            }
          }
        } catch (_) {}

        populateThumbs(_seriesList);
        populateSeriesSelect(_seriesList);
        await loadSeries(_seriesList[0].series_uid);

      } else if (level === 'series') {
        _currentSeriesUid = uid;
        _seriesList = [];
        clearThumbs();
        await loadSeries(uid);

      } else if (level === 'instance') {
        _seriesList = [];
        clearThumbs();
        var imageId = await buildSingleImageId(uid);
        if (imageId) await loadStack([imageId]);
      }
    } catch (err) {
      console.error('openViewer error:', err);
      if (typeof toast === 'function') toast('Viewer error: ' + errMsg(err), 'error');
    }
  }

  function closeViewer() {
    disableElement();
    var panel = document.getElementById('viewerPanel');
    if (panel) panel.classList.add('hidden');

    _currentStudyUid = null;
    _currentSeriesUid = null;
    _seriesList = [];

    ['viewerInfo', 'viewerImageIndex', 'viewerLoadTime'].forEach(function (id) {
      var el = document.getElementById(id);
      if (el) el.textContent = '';
    });
    clearThumbs();
  }

  function clearThumbs() {
    var t = document.getElementById('viewerThumbs');
    if (t) t.innerHTML = '';
    var s = document.getElementById('viewerSeriesSelect');
    if (s) s.innerHTML = '';
  }

  function switchSeries(seriesUid) {
    if (seriesUid === _currentSeriesUid) return;
    loadSeries(seriesUid).catch(function (err) {
      console.error('switchSeries error:', err);
      if (typeof toast === 'function') toast('Failed to switch series', 'error');
    });
  }

  function setLoadingMode(mode) {
    if (mode !== 'server' && mode !== 'blob') return;
    _loadingMode = mode;
    var serverBtn = document.getElementById('modeServer');
    var blobBtn = document.getElementById('modeBlob');
    if (serverBtn) serverBtn.classList.toggle('active', mode === 'server');
    if (blobBtn) blobBtn.classList.toggle('active', mode === 'blob');
    if (_currentSeriesUid) {
      loadSeries(_currentSeriesUid).catch(function (err) {
        console.error('Mode switch reload failed:', err);
      });
    }
  }

  function setViewerTool(toolName) {
    if (!_element) return;
    var map = { wl: 'Wwwc', zoom: 'Zoom', pan: 'Pan' };
    var csName = map[toolName];
    if (!csName) return;

    csTools.setToolActiveForElement(_element, csName, { mouseButtonMask: 1 });
    if (toolName !== 'zoom') csTools.setToolActiveForElement(_element, 'Zoom', { mouseButtonMask: 2 });
    if (toolName !== 'pan') csTools.setToolActiveForElement(_element, 'Pan', { mouseButtonMask: 4 });

    document.querySelectorAll('.viewer-tool-btn[id^="tool"]').forEach(function (btn) {
      btn.classList.remove('active');
    });
    var btnMap = { wl: 'toolWL', zoom: 'toolZoom', pan: 'toolPan' };
    var btn = document.getElementById(btnMap[toolName]);
    if (btn) btn.classList.add('active');
  }

  function resetViewerView() {
    if (!_element) return;
    cs.reset(_element);
  }

  // ── Expose on window ──────────────────────────────────────────────────────
  window.openViewer = openViewer;
  window.closeViewer = closeViewer;
  window.setLoadingMode = setLoadingMode;
  window.switchSeries = switchSeries;
  window.resetViewerView = resetViewerView;
  window.setViewerTool = setViewerTool;
})();
