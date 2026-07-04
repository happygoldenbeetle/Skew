namespace Mori.Cef;

/// <summary>
/// JavaScript agents injected into pages by <see cref="BrowserClient"/>. Ports of
/// the mac PasskeyAgentScript.h and MediaAgentScript.h payloads.
///
/// The scripts are loaded from <c>Assets/Scripts</c> when present (so they can be
/// edited without recompiling) and fall back to compact built-in versions.
/// </summary>
internal static class MoriAgentScripts
{
    private static readonly string ScriptsRoot =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Scripts");

    private static string? _passkey;
    private static string? _mediaTemplate;

    /// <summary>
    /// WebAuthn/passkey shim. Injected at OnLoadStart so it runs before page
    /// scripts can capture the original navigator.credentials methods.
    /// </summary>
    public static string PasskeyAgent =>
        _passkey ??= Load("passkeyAgent.js", DefaultPasskeyAgent);

    /// <summary>
    /// Media/PiP agent. The <paramref name="autoPiP"/> flag is substituted for
    /// the <c>__MORI_AUTO_PIP__</c> token so the engine pops the video out when
    /// the tab is hidden only when the user preference is enabled.
    /// </summary>
    public static string MediaAgent(bool autoPiP)
    {
        _mediaTemplate ??= Load("mediaAgent.js", DefaultMediaAgent);
        return _mediaTemplate.Replace("__MORI_AUTO_PIP__", autoPiP ? "true" : "false");
    }

    private static string Load(string fileName, string fallback)
    {
        try
        {
            string path = Path.Combine(ScriptsRoot, fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        catch (IOException)
        {
            // fall through to built-in
        }
        return fallback;
    }

    // ── Built-in fallbacks ────────────────────────────────────────────────

    private const string DefaultPasskeyAgent = """
        (function(){
          if (window.__moriPasskeyInstalled) return;
          window.__moriPasskeyInstalled = true;
          // Placeholder shim: preserves references to the native implementations
          // so a future native passkey bridge can intercept create/get. Faithful
          // port target is mac PasskeyAgentScript.h.
          try {
            if (navigator.credentials) {
              window.__moriNativeCredentials = {
                create: navigator.credentials.create && navigator.credentials.create.bind(navigator.credentials),
                get: navigator.credentials.get && navigator.credentials.get.bind(navigator.credentials)
              };
            }
          } catch (e) {}
        })();
        """;

    private const string DefaultMediaAgent = """
        (function(){
          if (window.__moriMediaInstalled) return;
          window.__moriMediaInstalled = true;
          var autoPiP = __MORI_AUTO_PIP__;

          function emit(payload){
            try { console.debug("__MORI_MEDIA__" + JSON.stringify(payload)); } catch(e){}
          }

          function primaryVideo(){
            var vids = Array.prototype.slice.call(document.querySelectorAll("video"));
            vids = vids.filter(function(v){ return v.readyState > 0 && v.duration > 0; });
            vids.sort(function(a,b){ return (b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight); });
            return vids[0] || null;
          }

          window.__moriMediaCommand = function(action, value){
            var v = primaryVideo();
            if (!v) return;
            switch(action){
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

          window.__moriApplyAutoPiP = function(enabled){
            autoPiP = !!enabled;
            var v = primaryVideo();
            if (v) { try { v.autoPictureInPicture = autoPiP; } catch(e){} }
          };

          function mediaMeta(){
            try { return (navigator.mediaSession && navigator.mediaSession.metadata) || null; }
            catch(e){ return null; }
          }
          function artworkSrc(m){
            try {
              if (m && m.artwork && m.artwork.length) {
                var a = m.artwork.slice().sort(function(x,y){
                  return (parseInt((y.sizes||"0").split("x")[0])||0)-(parseInt((x.sizes||"0").split("x")[0])||0);
                });
                return a[0].src || "";
              }
            } catch(e){}
            return "";
          }

          function report(){
            var v = primaryVideo();
            if (!v) { emit({ hasMedia:false }); return; }
            try { v.autoPictureInPicture = autoPiP; } catch(e){}
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
        """;
}
