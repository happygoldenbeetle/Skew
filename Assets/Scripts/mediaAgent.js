// Mori media / Picture-in-Picture agent. Port target: mac MediaAgentScript.h.
// Injected at OnLoadEnd into each frame. Emits structured "__MORI_MEDIA__"
// console markers (read by BrowserClient.OnConsoleMessage) and exposes command
// hooks the host calls via SendMediaCommand / ApplyAutoPiP. The __MORI_AUTO_PIP__
// token is substituted by MoriAgentScripts.MediaAgent(bool) at injection time.
(function () {
  if (window.__moriMediaInstalled) return;
  window.__moriMediaInstalled = true;
  var autoPiP = __MORI_AUTO_PIP__;

  function emit(payload) {
    try { console.debug("__MORI_MEDIA__" + JSON.stringify(payload)); } catch (e) {}
  }

  function primaryVideo() {
    var vids = Array.prototype.slice.call(document.querySelectorAll("video"));
    vids = vids.filter(function (v) { return v.readyState > 0 && v.duration > 0; });
    vids.sort(function (a, b) {
      return (b.clientWidth * b.clientHeight) - (a.clientWidth * a.clientHeight);
    });
    return vids[0] || null;
  }

  window.__moriMediaCommand = function (action, value) {
    var v = primaryVideo();
    if (!v) return;
    switch (action) {
      case "play": v.play(); break;
      case "pause": v.pause(); break;
      case "seek": v.currentTime = value; break;
      case "skip": v.currentTime = Math.max(0, v.currentTime + value); break;
      case "mute": v.muted = !v.muted; break;
      case "pip":
        if (document.pictureInPictureElement) document.exitPictureInPicture();
        else if (v.requestPictureInPicture) v.requestPictureInPicture();
        break;
    }
  };

  window.__moriApplyAutoPiP = function (enabled) {
    autoPiP = !!enabled;
    var v = primaryVideo();
    if (v) { try { v.autoPictureInPicture = autoPiP; } catch (e) {} }
  };

  function report() {
    var v = primaryVideo();
    if (!v) { emit({ hasMedia: false }); return; }
    try { v.autoPictureInPicture = autoPiP; } catch (e) {}
    emit({
      hasMedia: true,
      paused: v.paused,
      muted: v.muted,
      currentTime: v.currentTime,
      duration: v.duration,
      title: document.title
    });
  }

  document.addEventListener("play", report, true);
  document.addEventListener("pause", report, true);
  setInterval(report, 1000);
  report();
})();
