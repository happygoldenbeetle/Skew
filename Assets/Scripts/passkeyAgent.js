// Mori passkey/WebAuthn agent. Port target: mac PasskeyAgentScript.h.
// Injected at OnLoadStart so it runs before page scripts can capture the
// original navigator.credentials methods. This baseline preserves references to
// the native implementations for a future native passkey bridge.
(function () {
  if (window.__moriPasskeyInstalled) return;
  window.__moriPasskeyInstalled = true;
  try {
    if (navigator.credentials) {
      window.__moriNativeCredentials = {
        create: navigator.credentials.create &&
          navigator.credentials.create.bind(navigator.credentials),
        get: navigator.credentials.get &&
          navigator.credentials.get.bind(navigator.credentials)
      };
    }
  } catch (e) { /* ignore */ }
})();
