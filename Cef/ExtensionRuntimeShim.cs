namespace Skew.Cef;

/// <summary>
/// Generates the JavaScript polyfill that provides chrome.* API stubs for
/// extension content scripts and background pages. Mirrors the Mac app's
/// ExtensionRuntimeShim approach: calls are serialised as console.info
/// messages with a __SKEW_EXTENSION__ prefix, intercepted on the C# side
/// by OnConsoleMessage, and resolved by injecting __skewExtResolve back
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
window.__skewExtensionID=extId;
chrome.runtime=chrome.runtime||{{}};
var runtime=chrome.runtime;
runtime.id=runtime.id||extId;

// --- Event emitter factory ---
function __skewEvent(){{
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
function __skewExtCall(method,args){{
  var rid=extId+':'+Date.now()+':'+Math.random().toString(36).slice(2);
  window.__skewExtCallbacks=window.__skewExtCallbacks||{{}};
  var promise=new Promise(function(resolve,reject){{
    window.__skewExtCallbacks[rid]={{resolve:resolve,reject:reject}};
  }});
  console.info('__SKEW_EXTENSION__'+JSON.stringify({{
    requestId:rid,extensionId:extId,method:method,args:args||{{}}
  }}));
  return promise;
}}

window.__skewExtResolve=window.__skewExtResolve||function(response){{
  var cb=window.__skewExtCallbacks&&window.__skewExtCallbacks[response.requestId];
  if(!cb)return;
  if(response.deferred)return;
  delete window.__skewExtCallbacks[response.requestId];
  if(response.error)cb.reject(new Error(response.error));
  else cb.resolve(response.result);
}};

// --- chrome.runtime ---
runtime.onMessage=runtime.onMessage||__skewEvent();
runtime.onInstalled=runtime.onInstalled||__skewEvent();
runtime.onStartup=runtime.onStartup||__skewEvent();
runtime.getURL=runtime.getURL||function(path){{
  var clean=String(path||'').replace(/^\/+/,'');
  return 'skew-extension://'+extId+'/'+encodeURI(clean);
}};
runtime.getManifest=runtime.getManifest||function(){{
  return JSON.parse(JSON.stringify(manifest));
}};
runtime.sendMessage=runtime.sendMessage||function(message,options,cb){{
  if(typeof options==='function'){{cb=options;options={{}};}}
  var p=__skewExtCall('runtime.sendMessage',{{message:message}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
runtime.setUninstallURL=runtime.setUninstallURL||function(url,cb){{
  var p=Promise.resolve(); if(typeof cb==='function')p.then(cb); return p;
}};

// Host to content page message delivery. The first listener response is sent
// back through the same authenticated request id used by the native bridge.
window.__skewExtDispatchMessage=window.__skewExtDispatchMessage||function(targetId,message,requestId,sourceUrl,sourceOrigin){{
  if(targetId!==extId||!runtime.onMessage)return;
  var sent=false;
  function sendResponse(value){{
    if(sent||!requestId)return; sent=true;
    console.info('__SKEW_EXTENSION_RESPONSE__'+JSON.stringify({{
      requestId:requestId,extensionId:extId,result:value
    }}));
  }}
  var sender={{id:extId,url:sourceUrl||'',origin:sourceOrigin||'',tab:{{id:0,url:sourceUrl||''}}}};
  runtime.onMessage._listeners.slice().forEach(function(listener){{
    try{{
      var returned=listener(message,sender,sendResponse);
      if(returned&&typeof returned.then==='function')returned.then(sendResponse);
    }}catch(e){{console.error(e);}}
  }});
  if(requestId)setTimeout(function(){{sendResponse(null);}},1000);
}};

// --- chrome.contextMenus ---
chrome.contextMenus=chrome.contextMenus||{{}};
chrome.contextMenus.ACTION_MENU_TOP_LEVEL_LIMIT=6;
chrome.contextMenus.onClicked=chrome.contextMenus.onClicked||__skewEvent();
chrome.contextMenus.create=chrome.contextMenus.create||function(createProperties,cb){{
  var p=__skewExtCall('contextMenus.create',{{createProperties:createProperties||{{}}}});
  if(typeof cb==='function')p.then(function(id){{cb(id);}});
  return createProperties&&createProperties.id?createProperties.id:undefined;
}};
chrome.contextMenus.update=chrome.contextMenus.update||function(id,updateProperties,cb){{
  var p=__skewExtCall('contextMenus.update',{{id:id,updateProperties:updateProperties||{{}}}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.contextMenus.remove=chrome.contextMenus.remove||function(id,cb){{
  var p=__skewExtCall('contextMenus.remove',{{id:id}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.contextMenus.removeAll=chrome.contextMenus.removeAll||function(cb){{
  var p=__skewExtCall('contextMenus.removeAll',{{}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.menus=chrome.menus||chrome.contextMenus;

// --- chrome.storage (basic local stub) ---
chrome.storage=chrome.storage||{{}};
chrome.storage.onChanged=chrome.storage.onChanged||__skewEvent();
chrome.storage.local=chrome.storage.local||{{}};
chrome.storage.local.get=chrome.storage.local.get||function(keys,cb){{
  var p=__skewExtCall('storage.local.get',{{keys:keys}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
chrome.storage.local.set=chrome.storage.local.set||function(items,cb){{
  var p=__skewExtCall('storage.local.set',{{items:items}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.storage.local.remove=chrome.storage.local.remove||function(keys,cb){{
  var p=__skewExtCall('storage.local.remove',{{keys:keys}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.storage.local.clear=chrome.storage.local.clear||function(cb){{
  var p=__skewExtCall('storage.local.clear',{{}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.storage.sync=chrome.storage.sync||chrome.storage.local;
chrome.storage.session=chrome.storage.session||chrome.storage.local;

// --- chrome.tabs (minimal stubs) ---
chrome.tabs=chrome.tabs||{{}};
chrome.tabs.query=chrome.tabs.query||function(queryInfo,cb){{
  var p=__skewExtCall('tabs.query',{{queryInfo:queryInfo||{{}}}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
chrome.tabs.create=chrome.tabs.create||function(createProperties,cb){{
  var p=__skewExtCall('tabs.create',{{createProperties:createProperties||{{}}}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
chrome.tabs.sendMessage=chrome.tabs.sendMessage||function(tabId,message,options,cb){{
  if(typeof options==='function'){{cb=options;options={{}};}}
  var p=__skewExtCall('tabs.sendMessage',{{tabId:tabId,message:message}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
chrome.tabs.reload=chrome.tabs.reload||function(tabId,reloadProperties,cb){{
  if(typeof tabId==='object'||typeof tabId==='function'){{cb=reloadProperties;reloadProperties=tabId;tabId=null;}}
  var p=__skewExtCall('tabs.reload',{{tabId:tabId,reloadProperties:reloadProperties||{{}}}});
  if(typeof cb==='function')p.then(function(){{cb();}}); return p;
}};

// --- chrome.action, browserAction, scripting and commands ---
chrome.action=chrome.action||{{}};
chrome.action.onClicked=chrome.action.onClicked||__skewEvent();
['setTitle','setIcon','setBadgeText','setBadgeBackgroundColor','enable','disable'].forEach(function(name){{
  chrome.action[name]=chrome.action[name]||function(details,cb){{
    var p=Promise.resolve(); if(typeof cb==='function')p.then(cb); return p;
  }};
}});
chrome.browserAction=chrome.browserAction||chrome.action;

chrome.scripting=chrome.scripting||{{}};
chrome.scripting.executeScript=chrome.scripting.executeScript||function(injection,cb){{
  var p=__skewExtCall('scripting.executeScript',{{injection:injection||{{}}}});
  if(typeof cb==='function')p.then(cb); return p;
}};

chrome.commands=chrome.commands||{{}};
chrome.commands.onCommand=chrome.commands.onCommand||__skewEvent();

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
