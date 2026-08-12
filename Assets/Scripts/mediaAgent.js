// Skew media / Picture-in-Picture agent. Port target: mac MediaAgentScript.h.
// Injected at OnLoadEnd into each frame. Emits structured "__SKEW_MEDIA__"
// console markers (read by BrowserClient.OnConsoleMessage) and exposes command
// hooks the host calls via SendMediaCommand / ApplyAutoPiP. The __SKEW_AUTO_PIP__
// token is substituted by SkewAgentScripts.MediaAgent(bool) at injection time.
(function () {
  if (window.__skewMediaInstalled) return;
  window.__skewMediaInstalled = true;
  var autoPiP = __SKEW_AUTO_PIP__;

  function emit(payload) {
    try { console.debug("__SKEW_MEDIA__" + JSON.stringify(payload)); } catch (e) {}
  }

  function primaryVideo() {
    var vids = Array.prototype.slice.call(document.querySelectorAll("video"));
    vids = vids.filter(function (v) { return v.readyState > 0 && v.duration > 0; });
    vids.sort(function (a, b) {
      return (b.clientWidth * b.clientHeight) - (a.clientWidth * a.clientHeight);
    });
    return vids[0] || null;
  }

  window.__skewMediaCommand = function (action, value) {
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

  window.__skewApplyAutoPiP = function (enabled) {
    autoPiP = !!enabled;
    var v = primaryVideo();
    if (v) { try { v.autoPictureInPicture = autoPiP; } catch (e) {} }
  };

  function mediaMeta() {
    try { return (navigator.mediaSession && navigator.mediaSession.metadata) || null; }
    catch (e) { return null; }
  }

  function artworkSrc(m) {
    try {
      if (m && m.artwork && m.artwork.length) {
        var a = m.artwork.slice().sort(function (x, y) {
          return (parseInt((y.sizes || "0").split("x")[0]) || 0) -
                 (parseInt((x.sizes || "0").split("x")[0]) || 0);
        });
        return a[0].src || "";
      }
    } catch (e) {}
    return "";
  }

  function report() {
    var v = primaryVideo();
    if (!v) { emit({ hasMedia: false }); return; }
    try { v.autoPictureInPicture = autoPiP; } catch (e) {}
    var m = mediaMeta();
    emit({
      hasMedia: true,
      playing: !v.paused,
      paused: v.paused,
      muted: v.muted,
      position: v.currentTime,
      currentTime: v.currentTime,
      duration: isFinite(v.duration) ? v.duration : 0,
      title: (m && m.title) ? m.title : document.title,
      artist: (m && m.artist) ? m.artist : "",
      artwork: artworkSrc(m),
      isVideo: true,
      inPiP: (document.pictureInPictureElement === v),
      canPiP: !!(v.requestPictureInPicture && document.pictureInPictureEnabled)
    });
  }

  document.addEventListener("play", report, true);
  document.addEventListener("pause", report, true);
  setInterval(report, 1000);
  report();
})();
