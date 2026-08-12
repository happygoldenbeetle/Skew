using System.Text.Json;

namespace Skew.Cef;

/// <summary>
/// Bridge between the extension scheme handler (<see cref="SkewSchemeHandler"/>)
/// and the chrome.* runtime builder. Port of the mac ExtensionRuntimeBridge.h /
/// SkewExtensionPageRuntimeJS, which lived in BrowserClient.mm.
///
/// <para>
/// The Alloy/child-view embedding model has no built-in extension runtime, so
/// Skew implements the chrome.* surface itself. This shim is injected at serve
/// time (and post-load) so an extension page can talk to the native host through
/// the <c>window.__skewExt*</c> hooks that <see cref="Controls.SkewBrowserView"/>
/// drives.
/// </para>
/// </summary>
public static class ExtensionRuntimeBridge
{
    /// <summary>
    /// Full, wrapped chrome.* runtime shim JS for an enabled extension page, or
    /// null if the id doesn't resolve to an enabled extension. Mirrors
    /// SkewExtensionPageRuntimeJS(NSString*).
    /// </summary>
    public static string? ExtensionPageRuntimeJs(string extensionId)
    {
        if (string.IsNullOrEmpty(extensionId))
            return null;
        if (SkewExtensionCatalog.EnabledExtensionRootForId(extensionId) is null)
            return null;

        string idLiteral = JsonSerializer.Serialize(extensionId);

        // Minimal, self-contained runtime: identity + the dispatch/resolve hooks
        // the static SkewBrowserView fan-out methods call into. Message passing
        // and event delivery flow through the native host via WebMessage / JS.
        return $$"""
            (function(){
              if (window.__skewExtensionRuntimeInstalled) return;
              window.__skewExtensionRuntimeInstalled = true;
              window.__skewExtensionID = {{idLiteral}};

              var pending = Object.create(null);
              var messageListeners = [];
              var eventListeners = Object.create(null);

              function post(payload){
                payload.extensionId = window.__skewExtensionID;
                try { window.chrome.webview && window.chrome.webview.postMessage(payload); }
                catch(e) {}
                // CEF host also reads structured console markers as a channel.
                try { console.debug("__SKEW_EXT__" + JSON.stringify(payload)); } catch(e) {}
              }

              // Host -> page: deliver a runtime message to listeners.
              window.__skewExtDispatchMessage =
                  function(extId, message, requestId, sourceUrl, sourceOrigin){
                if (extId !== window.__skewExtensionID) return;
                var sender = { id: extId, url: sourceUrl, origin: sourceOrigin };
                var responded = false;
                var sendResponse = function(resp){
                  if (responded) return; responded = true;
                  if (requestId != null) post({ kind:"response", requestId: requestId, response: resp });
                };
                for (var i=0;i<messageListeners.length;i++){
                  try { messageListeners[i](message, sender, sendResponse); } catch(e){}
                }
              };

              // Host -> page: resolve a pending bridge request (screenshots, etc.).
              window.__skewExtResolve = function(resp){
                if (!resp || resp.extensionId !== window.__skewExtensionID) return;
                var rid = resp.requestId;
                if (rid != null && pending[rid]){
                  var p = pending[rid]; delete pending[rid];
                  if (resp.error) p.reject(new Error(resp.error)); else p.resolve(resp.result);
                }
              };

              // Host -> page: fire a chrome-style event (tabs.onUpdated, etc.).
              window.__skewExtDispatchEvent = function(name, args, extId){
                if (extId != null && extId !== window.__skewExtensionID) return;
                var ls = eventListeners[name] || [];
                for (var i=0;i<ls.length;i++){ try { ls[i].apply(null, args||[]); } catch(e){} }
              };

              function makeEvent(name){
                return {
                  addListener: function(fn){ (eventListeners[name]=eventListeners[name]||[]).push(fn); },
                  removeListener: function(fn){
                    var ls = eventListeners[name]||[]; var k = ls.indexOf(fn); if(k>=0) ls.splice(k,1);
                  }
                };
              }

              var nextRequestId = 1;
              function request(kind, body){
                return new Promise(function(resolve, reject){
                  var rid = "r" + (nextRequestId++);
                  pending[rid] = { resolve: resolve, reject: reject };
                  post(Object.assign({ kind: kind, requestId: rid }, body||{}));
                });
              }

              window.chrome = window.chrome || {};
              window.chrome.runtime = window.chrome.runtime || {};
              window.chrome.runtime.id = window.__skewExtensionID;
              window.chrome.runtime.onMessage = { addListener: function(fn){ messageListeners.push(fn); } };
              window.chrome.runtime.sendMessage = function(msg){ return request("sendMessage", { message: msg }); };
              window.chrome.tabs = window.chrome.tabs || {};
              window.chrome.tabs.onUpdated = makeEvent("tabs.onUpdated");
              window.chrome.tabs.captureVisibleTab = function(){ return request("captureVisibleTab", {}); };
            })();
            """;
    }
}
