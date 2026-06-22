namespace Mori.Cef;

/// <summary>
/// Generates the JavaScript polyfill that provides chrome.* API stubs for
/// extension content scripts and background pages. Mirrors the Mac app's
/// ExtensionRuntimeShim approach: calls are serialised as console.info
/// messages with a __MORI_EXTENSION__ prefix, intercepted on the C# side
/// by OnConsoleMessage, and resolved by injecting __moriExtResolve back
/// into the page.
/// </summary>
internal static class ExtensionRuntimeShim
{
    /// <summary>
    /// Build the full shim JS for the given extension. Injected once per
    /// frame before any content scripts run.
    /// </summary>
    internal static string Generate(string extensionId, Models.ManifestMeta? manifest)
    {
        var manifestJson = "{}";
        if (manifest != null)
        {
            try
            {
                manifestJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    name = manifest.Name ?? "",
                    version = manifest.Version ?? "",
                    description = manifest.Description ?? "",
                    manifest_version = manifest.ManifestVersion
                });
            }
            catch { manifestJson = "{}"; }
        }

        // The shim is a self-executing function that sets up all the chrome.*
        // polyfills and the IPC bridge. Mirrors BrowserClient.mm lines 2316–3470.
        return $@"(function(){{
var extId='{extensionId}';
var manifest={manifestJson};
var chrome=globalThis.chrome=globalThis.chrome||{{}};
window.__moriExtensionID=extId;
chrome.runtime=chrome.runtime||{{}};
var runtime=chrome.runtime;
runtime.id=runtime.id||extId;

// --- Event emitter factory ---
function __moriEvent(){{
  var listeners=[];
  return {{
    addListener:function(fn){{if(typeof fn==='function'&&listeners.indexOf(fn)<0)listeners.push(fn);}},
    removeListener:function(fn){{var i=listeners.indexOf(fn);if(i>=0)listeners.splice(i,1);}},
    hasListener:function(fn){{return listeners.indexOf(fn)>=0;}},
    hasListeners:function(){{return listeners.length>0;}},
    _listeners:listeners,
    _fire:function(){{var args=arguments;listeners.slice().forEach(function(fn){{try{{fn.apply(null,args);}}catch(e){{console.error(e);}}}});}}
  }};
}}

// --- IPC bridge ---
function __moriExtCall(method,args){{
  var rid=extId+':'+Date.now()+':'+Math.random().toString(36).slice(2);
  window.__moriExtCallbacks=window.__moriExtCallbacks||{{}};
  var promise=new Promise(function(resolve,reject){{
    window.__moriExtCallbacks[rid]={{resolve:resolve,reject:reject}};
  }});
  console.info('__MORI_EXTENSION__'+JSON.stringify({{
    requestId:rid,extensionId:extId,method:method,args:args||{{}}
  }}));
  return promise;
}}

window.__moriExtResolve=window.__moriExtResolve||function(response){{
  var cb=window.__moriExtCallbacks&&window.__moriExtCallbacks[response.requestId];
  if(!cb)return;
  if(response.deferred)return;
  delete window.__moriExtCallbacks[response.requestId];
  if(response.error)cb.reject(new Error(response.error));
  else cb.resolve(response.result);
}};

// --- chrome.runtime ---
runtime.onMessage=runtime.onMessage||__moriEvent();
runtime.onInstalled=runtime.onInstalled||__moriEvent();
runtime.onStartup=runtime.onStartup||__moriEvent();
runtime.getURL=runtime.getURL||function(path){{
  var clean=String(path||'').replace(/^\/+/,'');
  return 'mori-extension://'+extId+'/'+encodeURI(clean);
}};
runtime.getManifest=runtime.getManifest||function(){{
  return JSON.parse(JSON.stringify(manifest));
}};
runtime.sendMessage=runtime.sendMessage||function(message,options,cb){{
  if(typeof options==='function'){{cb=options;options={{}};}}
  var p=__moriExtCall('runtime.sendMessage',{{message:message}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};

// --- chrome.contextMenus ---
chrome.contextMenus=chrome.contextMenus||{{}};
chrome.contextMenus.ACTION_MENU_TOP_LEVEL_LIMIT=6;
chrome.contextMenus.onClicked=chrome.contextMenus.onClicked||__moriEvent();
chrome.contextMenus.create=chrome.contextMenus.create||function(createProperties,cb){{
  var p=__moriExtCall('contextMenus.create',{{createProperties:createProperties||{{}}}});
  if(typeof cb==='function')p.then(function(id){{cb(id);}});
  return createProperties&&createProperties.id?createProperties.id:undefined;
}};
chrome.contextMenus.update=chrome.contextMenus.update||function(id,updateProperties,cb){{
  var p=__moriExtCall('contextMenus.update',{{id:id,updateProperties:updateProperties||{{}}}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.contextMenus.remove=chrome.contextMenus.remove||function(id,cb){{
  var p=__moriExtCall('contextMenus.remove',{{id:id}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.contextMenus.removeAll=chrome.contextMenus.removeAll||function(cb){{
  var p=__moriExtCall('contextMenus.removeAll',{{}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.menus=chrome.menus||chrome.contextMenus;

// --- chrome.storage (basic local stub) ---
chrome.storage=chrome.storage||{{}};
chrome.storage.onChanged=chrome.storage.onChanged||__moriEvent();
chrome.storage.local=chrome.storage.local||{{}};
chrome.storage.local.get=chrome.storage.local.get||function(keys,cb){{
  var p=__moriExtCall('storage.local.get',{{keys:keys}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
chrome.storage.local.set=chrome.storage.local.set||function(items,cb){{
  var p=__moriExtCall('storage.local.set',{{items:items}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.storage.local.remove=chrome.storage.local.remove||function(keys,cb){{
  var p=__moriExtCall('storage.local.remove',{{keys:keys}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.storage.local.clear=chrome.storage.local.clear||function(cb){{
  var p=__moriExtCall('storage.local.clear',{{}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.storage.sync=chrome.storage.sync||chrome.storage.local;
chrome.storage.session=chrome.storage.session||chrome.storage.local;

// --- chrome.tabs (minimal stubs) ---
chrome.tabs=chrome.tabs||{{}};
chrome.tabs.query=chrome.tabs.query||function(queryInfo,cb){{
  var p=__moriExtCall('tabs.query',{{queryInfo:queryInfo||{{}}}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
chrome.tabs.create=chrome.tabs.create||function(createProperties,cb){{
  var p=__moriExtCall('tabs.create',{{createProperties:createProperties||{{}}}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
chrome.tabs.sendMessage=chrome.tabs.sendMessage||function(tabId,message,options,cb){{
  if(typeof options==='function'){{cb=options;options={{}};}}
  var p=__moriExtCall('tabs.sendMessage',{{tabId:tabId,message:message}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};

// --- chrome.i18n ---
chrome.i18n=chrome.i18n||{{}};
chrome.i18n.getUILanguage=chrome.i18n.getUILanguage||function(){{return 'en';}};
chrome.i18n.getMessage=chrome.i18n.getMessage||function(name){{return name||'';}};

// --- chrome.extension ---
chrome.extension=chrome.extension||{{}};
chrome.extension.getURL=chrome.extension.getURL||runtime.getURL;

// --- globalThis.browser mirror ---
try{{
  globalThis.browser=globalThis.browser||chrome;
}}catch(e){{}}

}})();";
    }
}
